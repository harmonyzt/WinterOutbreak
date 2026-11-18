using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using UnityEngine;
using UnityEngine.Networking;

namespace WinterOutbreak
{
    /// <summary>
    /// Placeable bonfires
    /// </summary>
    public sealed partial class WinterOutbreakPlugin
    {
        private static readonly HashSet<string> FireStarters = new HashSet<string>
        {
            "57347b8b24597737dd42e192", // Classic matches
            "5e2af2bc86f7746d3f3c33fc", // Hunting matches
            "56742c284bdc2d98058b456d", // Crickent lighter
            "56742c2e4bdc2d95058b456d", // Zibbo lighter
            "5e2af37686f774755a234b65" // SurvL Survivor Lighter
        };

        private static readonly string[] FireTinder =
        {
            "5c13cef886f774072e618e82", // Toilet paper
            "5e2af4a786f7746d3f3c3400", // Ripstop fabric
            "5e2af41e86f774755a234b67", // Cordura polyamide fabric
            "5e2af47786f7746d404f3aaa", // Fleece fabric
            "590a373286f774287540368b" // Dry fuel
        };

        // Where a bonfire may go
        private const float BonfireReach = 3.5f;
        private const float BonfireMinDistance = 1f;
        private const float BonfireMaxDistance = 4f;
        private const float BonfireFootprint = 0.55f;
        private const float BonfireClearance = 0.5f;
        private const float BonfireLowestNormal = 0.9f;
        private const float BonfireFlatTolerance = 0.12f;
        private const float BonfireHeadroom = 1.9f;
        private const float BonfireHeatHeight = 0.3f;

        private ConfigEntry<bool> _bonfiresEnabled;
        private ConfigEntry<KeyboardShortcut> _bonfireKey;
        private ConfigEntry<bool> _bonfireUsesTinder;
        private ConfigEntry<int> _bonfireLimit;

        private readonly List<PlacedBonfire> _bonfires = new List<PlacedBonfire>();
        private GameObject _bonfireGhost;
        private PreviewMaterialSetter _bonfireGhostLook;
        private Player _bonfirePlacer;
        private bool _bonfireHidWeapon;
        private float _bonfirePlacingStarted;
        private float _bonfireYaw;
        private float _nextBonfireRegister;

        private AudioClip _bonfirePlaceClip;
        private AudioClip _bonfireLoopClip;
        private bool _bonfireAudioLoadStarted;
        private Coroutine _bonfireAudioLoad;

        private void BindBonfireSettings()
        {
            _bonfiresEnabled = Config.Bind("Bonfires", "Enabled", true, "Place bonfires in winter raids. You need matches or a lighter, and dry fuel, toilet paper or a rag.");

            _bonfireKey = Config.Bind("Bonfires", "Place Key", new KeyboardShortcut(KeyCode.K),
                "Starts placing a bonfire. Left click places it; right click, Escape or this key cancels.");

            _bonfireUsesTinder = Config.Bind("Bonfires", "Uses Tinder", true, "Each bonfire uses up one piece of tinder. Matches and lighters are kept.");

            _bonfireLimit = Config.Bind("Bonfires", "Max Per Raid", 6, new ConfigDescription("How many bonfires you can place in one raid.", new AcceptableValueRange<int>(1, 30)));
        }

        private void UpdateBonfires(Player player)
        {
            if (player == null)
            {
                CancelBonfirePlacing(false);
                ClearBonfireExposure();
                return;
            }

            BonfireVisuals.Flicker(_bonfires);
            UpdateBonfireExposure(player);
            RegisterBonfires();

            if (_bonfireGhost != null)
            {
                UpdateBonfirePlacing(player);
                return;
            }

            if (_bonfiresEnabled.Value && _bonfireKey.Value.IsDown() && CanStartBonfire(player))
            {
                StartBonfirePlacing(player);
            }
        }

        // The fire list is cleared whenever exposure resets, so placed bonfires are put back on it every second.
        private void RegisterBonfires()
        {
            if (_bonfires.Count == 0 || Time.time < _nextBonfireRegister)
            {
                return;
            }

            _nextBonfireRegister = Time.time + 1f;
            foreach (var bonfire in _bonfires)
            {
                _fires.AddPlaced(bonfire.Heat);
            }
        }

        private bool CanStartBonfire(Player player)
        {
            if (!player.IsYourPlayer || player.HealthController == null || !player.HealthController.IsAlive || player.IsInventoryOpened || Cursor.visible)
            {
                return false;
            }

            // Bonfires other players built do not count against yours.
            if (_bonfires.Count(bonfire => !bonfire.Remote) >= _bonfireLimit.Value)
            {
                NotificationManager.DisplayWarningNotification($"You can't place more than {_bonfireLimit.Value} bonfires in a raid.");
                return false;
            }

            bool hasStarter = FindInventoryItem(player, FireStarters) != null;
            bool hasTinder = FindInventoryItem(player, FireTinder) != null;
            if (!hasStarter || !hasTinder)
            {
                string missing = !hasStarter && !hasTinder ? "matches or a lighter, and dry fuel, toilet paper or a rag" :
                    !hasStarter ? "matches or a lighter" : "dry fuel, toilet paper or a rag";
                NotificationManager.DisplayWarningNotification($"To build a bonfire you need {missing}.");
                return false;
            }

            return true;
        }

        private void StartBonfirePlacing(Player player)
        {
            EnsureBonfireAudio();

            _bonfirePlacer = player;
            _bonfirePlacingStarted = Time.time;
            _bonfireYaw = UnityEngine.Random.Range(0f, 360f);
            _bonfireHidWeapon = false;

            if (!player.HandsIsEmpty)
            {
                player.TrySaveLastItemInHands();
                player.SetEmptyHands(null);
                _bonfireHidWeapon = true;
            }

            _bonfireGhost = BonfireVisuals.CreateGhost();
            _bonfireGhost.SetActive(false);
            _bonfireGhostLook = _bonfireGhost.GetComponent<PreviewMaterialSetter>();
        }

        private void UpdateBonfirePlacing(Player player)
        {
            if (player != _bonfirePlacer || player.HealthController == null || !player.HealthController.IsAlive || player.IsInventoryOpened)
            {
                CancelBonfirePlacing(false);
                return;
            }

            if (_bonfireKey.Value.IsDown() || Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
            {
                CancelBonfirePlacing(true);
                return;
            }

            // Taking a weapon out again (scroll, number keys) ends placing; the first moment is the holster itself.
            if (Time.time - _bonfirePlacingStarted > 1.5f && !player.HandsIsEmpty)
            {
                CancelBonfirePlacing(false);
                return;
            }

            bool found = TryFindBonfireSpot(player, out Vector3 spot, out bool fits);
            _bonfireGhost.SetActive(found);
            if (!found)
            {
                return;
            }

            // The Customs pile is only 20 cm tall, so on the ground it would vanish under the voxel snow. While aiming,
            // the preview rides on the snow; the real bonfire goes on the ground and melts its way out.
            Vector3 shown = spot;
            if (TryGetVoxelSnowDepth(spot, out float snow) && snow > 0f)
            {
                shown.y += snow;
            }

            _bonfireGhost.transform.SetPositionAndRotation(shown, Quaternion.Euler(0f, _bonfireYaw, 0f));
            _bonfireGhostLook?.SetAvailable(fits);

            if (fits && player.HandsIsEmpty && Input.GetMouseButtonDown(0))
            {
                PlaceBonfire(player, spot);
            }
        }

        private void PlaceBonfire(Player player, Vector3 spot)
        {
            if (_bonfireUsesTinder.Value && !UseTinder(player))
            {
                NotificationManager.DisplayWarningNotification("The tinder could not be used, so no bonfire was built.");
                CancelBonfirePlacing(true);
                return;
            }

            var rotation = Quaternion.Euler(0f, _bonfireYaw, 0f);
            var bonfire = BonfireVisuals.CreateBonfire(spot, rotation, spot + Vector3.up * BonfireHeatHeight, _bonfireLoopClip);
            _bonfires.Add(bonfire);
            _fires.AddPlaced(bonfire.Heat);
            CoopSupport.Network?.SendBonfire(new SharedBonfire
            {
                Position = spot,
                Yaw = _bonfireYaw
            });

            if (_bonfirePlaceClip != null)
            {
                AudioSource.PlayClipAtPoint(_bonfirePlaceClip, spot + Vector3.up * 0.2f, 0.8f);
            }

            CancelBonfirePlacing(true);
        }

        private void CancelBonfirePlacing(bool giveWeaponBack)
        {
            if (_bonfireGhost != null)
            {
                Destroy(_bonfireGhost);
            }

            _bonfireGhost = null;
            _bonfireGhostLook = null;

            var player = _bonfirePlacer;
            if (giveWeaponBack && _bonfireHidWeapon && player != null && player.HealthController != null && player.HealthController.IsAlive && player.HandsIsEmpty)
            {
                player.TrySetLastEquippedWeapon();
            }

            _bonfirePlacer = null;
            _bonfireHidWeapon = false;
        }

        private void ClearBonfires()
        {
            CancelBonfirePlacing(false);
            foreach (var bonfire in _bonfires)
            {
                if (bonfire.Root != null)
                {
                    Destroy(bonfire.Root);
                }
            }

            _bonfires.Clear();
            _nextBonfireRegister = 0f;
            ClearBonfireExposure();
            BonfireVisuals.Clear();
        }

        private bool TryFindBonfireSpot(Player player, out Vector3 spot, out bool fits)
        {
            spot = default;
            fits = false;

            Ray ray = player.InteractionRay;
            int obstacles = LayersMaskController.TripwireCheckLayerMaskNoPlayer;
            int ground = LayersMaskController.TerrainLowPoly;

            // Aiming at a wall or a ceiling puts the bonfire on the floor in front of it.
            Vector3 probe;
            if (Physics.Raycast(ray, out RaycastHit aim, BonfireReach, obstacles, QueryTriggerInteraction.Ignore))
            {
                probe = aim.normal.y >= 0.7f ? aim.point : aim.point - ray.direction * 0.6f;
            }
            else
            {
                probe = ray.GetPoint(BonfireReach);
            }

            if (!Physics.Raycast(probe + Vector3.up * 0.4f, Vector3.down, out RaycastHit floor, 3f, ground, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            spot = floor.point;

            Vector3 offset = spot - player.Position;
            float height = offset.y;
            offset.y = 0f;
            float distance = offset.magnitude;

            fits = distance >= BonfireMinDistance && distance <= BonfireMaxDistance && Mathf.Abs(height) < 1.5f && floor.normal.y >= BonfireLowestNormal
                   && IsBonfireGroundFlat(spot, ground) && IsBonfireSpaceClear(spot, obstacles | LayersMaskController.PlayerMask)
                   && !Physics.Linecast(ray.origin, spot + Vector3.up * BonfireHeatHeight, obstacles, QueryTriggerInteraction.Ignore) && !IsUnderWater(spot)
                   && !IsNearOtherBonfire(spot);
            return true;
        }

        private static bool IsBonfireGroundFlat(Vector3 spot, int ground)
        {
            for (int i = 0; i < 6; i++)
            {
                float angle = i * Mathf.PI / 3f;
                Vector3 edge = spot + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * BonfireFootprint;
                if (!Physics.Raycast(edge + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 1f, ground, QueryTriggerInteraction.Ignore))
                {
                    return false;
                }

                if (Mathf.Abs(hit.point.y - spot.y) > BonfireFlatTolerance)
                {
                    return false;
                }
            }

            return true;
        }

        // Half a metre of free space around the fire, nothing small on the ground under it, and no low ceiling.
        private static bool IsBonfireSpaceClear(Vector3 spot, int obstacles)
        {
            Vector3 bottom = spot + Vector3.up * (0.15f + BonfireClearance);
            Vector3 top = spot + Vector3.up * 1.3f;
            if (Physics.CheckCapsule(bottom, top, BonfireClearance, obstacles, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            int clutter = LayersMaskController.HighPolyCollider | LayersMaskController.DefaultLayerMask | LayersMaskController.LootLayerMask;
            var footprint = new Vector3(BonfireFootprint, 0.07f, BonfireFootprint);
            if (Physics.CheckBox(spot + Vector3.up * 0.17f, footprint, Quaternion.identity, clutter, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            return !Physics.Raycast(spot + Vector3.up * 0.2f, Vector3.up, BonfireHeadroom, obstacles, QueryTriggerInteraction.Ignore);
        }

        private static bool IsUnderWater(Vector3 spot)
        {
            int water = 1 << LayersMaskController.WaterLayer;
            return Physics.Raycast(spot + Vector3.up * 1f, Vector3.down, out RaycastHit hit, 1.2f, water, QueryTriggerInteraction.Collide) && hit.point.y > spot.y - 0.05f;
        }

        private bool IsNearOtherBonfire(Vector3 spot)
        {
            foreach (var bonfire in _bonfires)
            {
                if (bonfire.Root != null && (bonfire.Root.transform.position - spot).sqrMagnitude < 1.2f * 1.2f)
                {
                    return true;
                }
            }

            return false;
        }

        private static Item FindInventoryItem(Player player, IEnumerable<string> templates)
        {
            var inventory = player.Inventory;
            if (inventory == null)
            {
                return null;
            }

            foreach (string template in templates)
            {
                foreach (var item in inventory.AllRealPlayerItems)
                {
                    if (item.TemplateId == template)
                    {
                        return item;
                    }
                }
            }

            return null;
        }

        private bool UseTinder(Player player)
        {
            var tinder = FindInventoryItem(player, FireTinder);
            var controller = player.InventoryController;
            if (tinder == null || controller == null)
            {
                return false;
            }

            var removal = ItemManipulator.Remove(tinder, controller, true);
            if (removal.Failed)
            {
                Logger.LogWarning($"Bonfire: could not remove {tinder.LocalizedName()}: {removal.Error}");
                return false;
            }

            controller.TryRunNetworkTransaction(removal);
            return true;
        }

        // Optional sounds next to the footsteps: sound/bonfire_place.* when placing, sound/bonfire_loop.* as the
        // crackle. Without the loop file the bonfire borrows a fire sound from the map when it can find one.
        private void EnsureBonfireAudio()
        {
            if (_bonfireAudioLoadStarted)
            {
                return;
            }

            _bonfireAudioLoadStarted = true;
            _bonfireAudioLoad = StartCoroutine(LoadBonfireAudio());
        }

        private IEnumerator LoadBonfireAudio()
        {
            AudioClip place = null;
            yield return LoadOptionalClip("bonfire_place", clip => place = clip);
            _bonfirePlaceClip = place;

            AudioClip loop = null;
            yield return LoadOptionalClip("bonfire_loop", clip => loop = clip);
            _bonfireLoopClip = loop;

            _bonfireAudioLoad = null;
        }

        private IEnumerator LoadOptionalClip(string name, Action<AudioClip> done)
        {
            string[] extensions = { ".ogg", ".wav", ".mp3" };
            AudioType[] types = { AudioType.OGGVORBIS, AudioType.WAV, AudioType.MPEG };

            for (int i = 0; i < extensions.Length; i++)
            {
                string path = ResourcePath("sound/" + name + extensions[i]);
                if (!File.Exists(path))
                {
                    continue;
                }

                using var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, types[i]);
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    done(DownloadHandlerAudioClip.GetContent(request));
                }
                else
                {
                    Logger.LogWarning($"Could not load {name}{extensions[i]}: {request.error}");
                }

                yield break;
            }
        }

        private void ClearBonfireAudio()
        {
            if (_bonfireAudioLoad != null)
            {
                StopCoroutine(_bonfireAudioLoad);
                _bonfireAudioLoad = null;
            }

            if (_bonfirePlaceClip != null)
            {
                Destroy(_bonfirePlaceClip);
            }

            if (_bonfireLoopClip != null)
            {
                Destroy(_bonfireLoopClip);
            }

            _bonfirePlaceClip = null;
            _bonfireLoopClip = null;
            _bonfireAudioLoadStarted = false;
        }
    }
}
