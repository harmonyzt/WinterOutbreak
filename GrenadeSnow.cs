using System.Collections.Generic;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private static readonly Vector2[] BlastSnowOffsets =
        {
            Vector2.zero, Vector2.right, Vector2.left, Vector2.up, Vector2.down,
            new Vector2(0.7f, 0.7f), new Vector2(-0.7f, 0.7f),
            new Vector2(0.7f, -0.7f), new Vector2(-0.7f, -0.7f)
        };

        private readonly Queue<(Vector3 Position, float Radius)> _snowBlasts =
            new Queue<(Vector3, float)>();

        private Fixed<bool> _grenadeSnowEnabled;
        private Fixed<int> _grenadeSnowParticles;
        private Fixed<float> _craterRefillSeconds;
        private GrenadeSnowBurst _grenadeSnowBurst;

        private void BindGrenadeSnowSettings()
        {
            _grenadeSnowEnabled = new Fixed<bool>(true);
            _grenadeSnowParticles = new Fixed<int>(240);
            _craterRefillSeconds = new Fixed<float>(150f);
        }

        private void UpdateGrenadeSnow(Player player)
        {
            if (player == null || _voxelSnow == null || !_grenadeSnowEnabled.Value)
            {
                ClearGrenadeSnow();
                return;
            }

            _grenadeSnowBurst?.Update(Time.time);
            UpdateGrenadePits();
            if (_snowBlasts.Count == 0)
            {
                return;
            }

            var blast = _snowBlasts.Dequeue();
            if ((player.Position - blast.Position).sqrMagnitude > 90f * 90f
                || !FindBlastSnow(blast.Position, blast.Radius, out var ground, out float depth))
            {
                return;
            }

            float lift = Mathf.Max(0f, blast.Position.y - ground.point.y);
            float coupling = 1f - Mathf.InverseLerp(0.18f, 1.4f, lift);
            if (coupling <= 0f)
            {
                return;
            }

            float radius = blast.Radius * Mathf.Lerp(0.65f, 1f, coupling) * Random.Range(1.3f, 1.65f);
            float compression = depth * 0.97f * Mathf.Lerp(0.6f, 1f, coupling);
            _voxelSnow.StampMelt(ground.point, radius, compression, _craterRefillSeconds.Value, scorch: true);

            if (_grenadeSnowBurst == null)
            {
                _grenadeSnowBurst = new GrenadeSnowBurst();
                if (!_grenadeSnowBurst.IsAvailable)
                {
                    Logger.LogWarning("Grenade snow could not find a supported particle shader.");
                }
            }

            float powder = Mathf.Lerp(0.4f, 1f, Mathf.InverseLerp(0.02f, 0.15f, depth));
            int count = Mathf.RoundToInt(_grenadeSnowParticles.Value * powder * Mathf.Lerp(0.35f, 1f, coupling));
            float soilMix = IsLooseGround(ground) ? SoilMix(depth, coupling) : 0f;
            _grenadeSnowBurst.Emit(ground.point, radius, count, powder * coupling, soilMix);
        }

        private static float SoilMix(float depth, float coupling)
        {
            return Mathf.Lerp(1f, 0.30f, Mathf.InverseLerp(0.025f, 0.16f, depth)) * coupling;
        }

        private static bool IsLooseGround(RaycastHit hit)
        {
            BaseBallistic surface = hit.collider.GetComponentInParent<BaseBallistic>();
            if (surface is TerrainBallistic terrain && terrain.TerrainData != null
                && terrain.TerrainMaterials is { Length: > 0 } materials)
            {
                int texture = terrain.GetMainTexture(hit.point);
                if (texture >= 0 && texture < materials.Length)
                {
                    return materials[texture] is BaseBallistic.ESurfaceSound.Soil or BaseBallistic.ESurfaceSound.Grass
                        or BaseBallistic.ESurfaceSound.Gravel or BaseBallistic.ESurfaceSound.Swamp;
                }
            }

            if (surface is BallisticCollider ballistic)
            {
                return ballistic.TypeOfMaterial is MaterialType.Soil or MaterialType.SoilForest or MaterialType.Mud
                    or MaterialType.GrassLow or MaterialType.GrassHigh or MaterialType.Gravel or MaterialType.Swamp;
            }

            // Untagged terrain can still carry soil
            return hit.collider is TerrainCollider;
        }

        private bool FindBlastSnow(Vector3 explosion, float radius, out RaycastHit ground, out float depth)
        {
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            int solids = LayersMaskController.HighPolyCollider | LayersMaskController.LowPolyColliderLayerMask;
            int floorMask = solids | LayersMaskController.TerrainMask;
            int blockers = solids | LayersMaskController.PlayerStaticDoorMask;

            foreach (Vector2 offset in BlastSnowOffsets)
            {
                Vector3 start = explosion + new Vector3(offset.x * radius * 0.48f, 0.16f, offset.y * radius * 0.48f);
                if (!Physics.Raycast(start, Vector3.down, out var hit, 1.56f, floorMask, triggers)
                    || hit.normal.y < 0.42f || !_voxelSnow.TryGetDepth(hit.point, out depth) || depth < 0.018f)
                {
                    continue;
                }

                Vector3 contact = hit.point + Vector3.up * 0.09f;
                if (!Physics.Linecast(explosion + Vector3.up * 0.04f, contact, blockers, triggers))
                {
                    ground = hit;
                    return true;
                }
            }

            ground = default;
            depth = 0f;
            return false;
        }

        private const float PitRadius = 0.13f;
        private const float PitRefillSeconds = 30f;
        private const float PitRestampSeconds = 2f;
        private readonly List<LiveGrenade> _liveGrenades = new List<LiveGrenade>();
        private float _nextPitCheck;

        private sealed class LiveGrenade
        {
            public Grenade Grenade;
            public Vector3 LastStamp;
            public float NextStamp;
            public bool Stamped;
            public Vector3 LastPosition;
            public float StillSince = -1f;
        }

        /// <summary>
        /// Refreshed while it lies there; the pit fills back in after it explodes.
        /// </summary>
        private void UpdateGrenadePits()
        {
            float now = Time.time;
            if (_liveGrenades.Count == 0 || now < _nextPitCheck)
            {
                return;
            }

            _nextPitCheck = now + 0.05f;
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            int floorMask = LayersMaskController.HighPolyCollider | LayersMaskController.LowPolyColliderLayerMask
                | LayersMaskController.TerrainMask;
            for (int i = _liveGrenades.Count - 1; i >= 0; i--)
            {
                var live = _liveGrenades[i];
                if (live.Grenade == null || !live.Grenade.gameObject.activeInHierarchy)
                {
                    _liveGrenades.RemoveAt(i);
                    continue;
                }

                // Resting means it moved less than 2 cm between checks for a quarter second
                Vector3 position = live.Grenade.transform.position;
                bool still = (position - live.LastPosition).sqrMagnitude < 0.02f * 0.02f;
                live.LastPosition = position;
                if (!still)
                {
                    live.StillSince = -1f;
                    continue;
                }

                if (live.StillSince < 0f)
                {
                    live.StillSince = now;
                }

                bool moved = !live.Stamped || (position - live.LastStamp).sqrMagnitude > 0.06f * 0.06f;
                if (now - live.StillSince < 0.25f || (!moved && now < live.NextStamp))
                {
                    continue;
                }

                // Only on the ground: a grenade in flight leaves nothing
                if (!Physics.Raycast(position + Vector3.up * 0.1f, Vector3.down, out var ground, 0.32f, floorMask, triggers)
                    || ground.normal.y < 0.5f || !_voxelSnow.TryGetDepth(ground.point, out float depth) || depth < 0.02f)
                {
                    continue;
                }

                _voxelSnow.StampBlast(ground.point, PitRadius, Mathf.Min(depth * 0.92f, 0.3f), PitRefillSeconds);
                live.LastStamp = position;
                live.NextStamp = now + PitRestampSeconds;
                live.Stamped = true;
            }
        }

        [HarmonyPatch]
        private static class GrenadeTrackPatch
        {
            // Grenade declares one Init (settings, profile id, throw weapon, time spent, ballistics, planted)
            private static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.DeclaredMethod(typeof(Grenade), nameof(Grenade.Init));
            }

            private static void Postfix(Grenade __instance)
            {
                var plugin = _plugin;
                if (plugin?._voxelSnow == null || __instance == null || plugin._liveGrenades.Count >= 32)
                {
                    return;
                }

                plugin._liveGrenades.Add(new LiveGrenade { Grenade = __instance });
            }
        }

        private void ClearGrenadeSnow()
        {
            _snowBlasts.Clear();
            _liveGrenades.Clear();
            _grenadeSnowBurst?.Dispose();
            _grenadeSnowBurst = null;
        }

        [HarmonyPatch(typeof(Grenade), nameof(Grenade.Explosion), new[]
        {
            typeof(Grenade), typeof(IExplosiveItem), typeof(Vector3), typeof(string),
            typeof(IBallisticsCalculator), typeof(Item), typeof(Vector3), typeof(bool)
        })]
        private static class GrenadeSnowPatch
        {
            private static void Postfix(Grenade grenade, IExplosiveItem grenadeItem, Vector3 grenadePosition,
                Vector3 shift, bool isPlanted)
            {
                var plugin = _plugin;
                if (plugin?._voxelSnow == null || !plugin._grenadeSnowEnabled.Value || plugin._snowBlasts.Count >= 8
                    || ReferenceEquals(grenade, null) || grenade is SmokeGrenade || isPlanted
                    || grenadeItem == null || grenadeItem.IsDummy || grenadeItem.FragmentsCount <= 0)
                {
                    return;
                }

                float radius = Mathf.Clamp(grenadeItem.MaxExplosionDistance * 0.30f, 0.8f, 1.65f);
                plugin._snowBlasts.Enqueue((grenadePosition + shift, radius));
            }
        }
    }
}
