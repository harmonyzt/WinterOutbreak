using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using UnityEngine;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private const float InitialIce = 0.65f;
        // Ice shows on the item from the first tick. Below this fraction it can still be eaten or drunk.
        private const float VisibleIce = 0.01f;
        private const float FrozenThreshold = 0.1f;
        private const string FrozenMessage = "This food or drink is frozen.";

        private static readonly HashSet<string> WetFood = new HashSet<string>
        {
            "5673de654bdc2d180f8b456d", // Pacific saury
            "57347d5f245977448b40fa81", // Humpback salmon
            "57347d692459774491567cf1", // Green peas
            "57347d7224597744596b4e72", // Small beef stew
            "57347d8724597744596b4e76", // Squash spread
            "57347d9c245977448b40fa85", // Herring
            "57347da92459774491567cf5", // Large beef stew
            "5bc9b156d4351e00367fbce9", // DevilDog mayo
            "5bc9c29cd4351e003562b8a3", // Sprats
            "67586b7e49c2fa592e0d8ed9" // Olivier salad
        };

        private static readonly HashSet<string> Spirits = new HashSet<string>
        {
            "5d1b376e86f774252519444e", // Moonshine
            "5d403f9186f7743cac3f229b", // Whiskey
            "5d40407c86f774318526545a", // Vodka
            "614451b71e5874611e2c7ae5" // Alternate vodka
        };

        private static readonly HashSet<string> NeverFreezes = new HashSet<string>
        {
            "60098b1705871270cd5352a1" // Emergency water ration (EWR)
        };

        private readonly Dictionary<string, float> _foodIce = new Dictionary<string, float>();
        private Fixed<float> _outdoorFreezeSeconds;
        private Fixed<float> _indoorThawSeconds;
        private Fixed<float> _fireThawSeconds;
        private Sprite _frozenFoodIcon;
        private bool _frozenFoodIconFailed;

        private bool FrozenFoodActive => _raid != null
            && _raid.SeasonsController?.Season == ESeason.Winter
            && _raid.SeasonsController.Status == ESeasonStatus.Winter
            && Singleton<AbstractGame>.Instance?.Status == GameStatus.Started;

        private void BindFrozenFoodSettings()
        {
            _outdoorFreezeSeconds = new Fixed<float>(180f);
            _indoorThawSeconds = new Fixed<float>(360f);
            _fireThawSeconds = new Fixed<float>(60f);
        }

        private static bool CanFreeze(FoodDrink item)
        {
            string templateId = item.TemplateId;
            if (NeverFreezes.Contains(templateId))
            {
                return false;
            }

            return WetFood.Contains(templateId)
                || (item.Template.IsChildOf("5448e8d64bdc2dce718b4568") && !Spirits.Contains(templateId));
        }

        private float GetIce(FoodDrink item)
        {
            return _foodIce.TryGetValue(item.Id, out float ice) ? ice : InitialIce;
        }

        internal static float GetFrozenFoodAmount(FoodDrink item)
        {
            var plugin = _plugin;
            if (item == null || plugin == null || !plugin.FrozenFoodActive || !CanFreeze(item))
            {
                return 0f;
            }

            float ice = plugin.GetIce(item);
            return ice >= VisibleIce ? ice : 0f;
        }

        /// <summary>
        /// True once the ice is past the grace fraction and the item cannot be used.
        /// </summary>
        internal static bool IsFoodFrozen(FoodDrink item)
        {
            return GetFrozenFoodAmount(item) >= FrozenThreshold;
        }

        private void UpdateFrozenFood(Player player, bool nearFire, bool indoors, float elapsed)
        {
            var inventory = player.InventoryController?.Inventory;
            if (inventory == null)
            {
                return;
            }

            float change = nearFire ? -elapsed / _fireThawSeconds.Value
                : indoors ? -elapsed / _indoorThawSeconds.Value : elapsed / _outdoorFreezeSeconds.Value;

            foreach (var item in inventory.GetPlayerItems(EPlayerItems.Equipment))
            {
                if (item is FoodDrink food && CanFreeze(food))
                {
                    _foodIce[food.Id] = Mathf.Clamp01(GetIce(food) + change);
                }
            }
        }

        internal static Sprite GetFrozenFoodIcon()
        {
            var plugin = _plugin;
            if (plugin == null || plugin._frozenFoodIconFailed)
            {
                return null;
            }

            if (plugin._frozenFoodIcon == null)
            {
                Texture2D texture = null;
                try
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!texture.LoadImage(System.IO.File.ReadAllBytes(ResourcePath("effects/effect_frozen.png")), true))
                    {
                        throw new System.IO.InvalidDataException("Could not decode the frozen food icon.");
                    }

                    plugin._frozenFoodIcon = Sprite.Create(texture,
                        new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                }
                catch (System.Exception exception)
                {
                    Destroy(texture);
                    plugin._frozenFoodIconFailed = true;
                    plugin.Logger.LogWarning(exception.Message);
                }
            }

            return plugin._frozenFoodIcon;
        }

        private void ClearFrozenFood()
        {
            _foodIce.Clear();
            _frozenFoodIconFailed = false;
            if (_frozenFoodIcon != null)
            {
                Destroy(_frozenFoodIcon.texture);
                Destroy(_frozenFoodIcon);
                _frozenFoodIcon = null;
            }
        }

        [HarmonyPatch(typeof(ItemView), nameof(ItemView.NewItemView))]
        private static class FrozenFoodItemViewPatch
        {
            private static void Postfix(ItemView __instance)
            {
                var overlay = __instance.GetComponent<FrozenFoodOverlay>();
                if (_plugin?.FrozenFoodActive == true && overlay == null && __instance.Item is FoodDrink food && CanFreeze(food))
                {
                    overlay = __instance.gameObject.AddComponent<FrozenFoodOverlay>();
                }

                if (overlay != null)
                {
                    overlay.Bind(__instance);
                }
            }
        }

        [HarmonyPatch(typeof(ItemView), nameof(ItemView.Kill))]
        private static class FrozenFoodItemViewKillPatch
        {
            private static void Prefix(ItemView __instance)
            {
                var overlay = __instance.GetComponent<FrozenFoodOverlay>();
                if (overlay != null)
                {
                    overlay.Bind(null);
                }
            }
        }

        [HarmonyPatch(typeof(FoodDrink), nameof(FoodDrink.ItemInteractionButtons), MethodType.Getter)]
        private static class FrozenFoodMenuPatch
        {
            private static void Postfix(FoodDrink __instance, ref IEnumerable<EItemInfoButton> __result)
            {
                if (IsFoodFrozen(__instance))
                {
                    __result = __result.Where(button => button != EItemInfoButton.Use && button != EItemInfoButton.UseAll);
                }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.Proceed), typeof(FoodDrink), typeof(float),
            typeof(Callback<IMedsController>), typeof(int), typeof(bool))]
        private static class FrozenFoodHandsPatch
        {
            private static bool Prefix(Player __instance, FoodDrink __0, Callback<IMedsController> __2)
            {
                if (!__instance.IsYourPlayer || !IsFoodFrozen(__0))
                {
                    return true;
                }

                if (Singleton<NotificationManager>.Instantiated)
                {
                    NotificationManager.DisplayWarningNotification(FrozenMessage);
                }

                __2?.Invoke(new Result<IMedsController>(null, FrozenMessage));
                return false;
            }
        }

        [HarmonyPatch(typeof(ActiveHealthController), nameof(ActiveHealthController.DoMedEffect),
            typeof(Item), typeof(EBodyPart), typeof(float?))]
        private static class FrozenFoodConsumePatch
        {
            private static bool Prefix(ActiveHealthController __instance, Item __0, ref IHealthEffect __result)
            {
                if (!ReferenceEquals(__instance, _plugin?._raid?.MainPlayer?.ActiveHealthController)
                    || !IsFoodFrozen(__0 as FoodDrink))
                {
                    return true;
                }

                __result = null;
                return false;
            }
        }

        [HarmonyPatch]
        private static class FrozenFoodHealthPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var type in new[] { typeof(PlayerHealthController), typeof(ClientPlayerHealthController) })
                {
                    foreach (var method in AccessTools.GetDeclaredMethods(type))
                    {
                        if (method.Name == nameof(IHealthController.ApplyItem))
                        {
                            yield return method;
                        }
                    }
                }
            }

            private static bool Prefix(IHealthController __instance, Item __0, ref bool __result)
            {
                if (!ReferenceEquals(__instance, _plugin?._raid?.MainPlayer?.HealthController)
                    || !IsFoodFrozen(__0 as FoodDrink))
                {
                    return true;
                }

                __result = false;
                return false;
            }
        }
    }

    internal sealed class FrozenFoodOverlay : MonoBehaviour
    {
        private ItemView _view;
        private GameObject _panel;
        private UnityEngine.UI.Image _icon;
        private RectTransform _fill;
        private float _refreshAt;

        internal void Bind(ItemView view)
        {
            _view = view;
            _refreshAt = 0f;
            enabled = view != null && view.Item is FoodDrink;
            Refresh();
        }

        private void Update()
        {
            if (Time.unscaledTime >= _refreshAt)
            {
                _refreshAt = Time.unscaledTime + 0.2f;
                Refresh();
            }
        }

        private void Refresh()
        {
            float ice = WinterOutbreakPlugin.GetFrozenFoodAmount(_view != null ? _view.Item as FoodDrink : null);
            if (ice <= 0f)
            {
                if (_panel != null)
                {
                    _panel.SetActive(false);
                }

                return;
            }

            if (_panel == null)
            {
                CreateOverlay();
            }

            _panel.SetActive(true);
            if (_icon.sprite == null)
            {
                _icon.sprite = WinterOutbreakPlugin.GetFrozenFoodIcon();
                _icon.enabled = _icon.sprite != null;
            }

            // Still usable during the grace fraction: the icon stays faint until the item locks.
            var food = _view != null ? _view.Item as FoodDrink : null;
            _icon.color = WinterOutbreakPlugin.IsFoodFrozen(food) ? Color.white : new Color(1f, 1f, 1f, 0.45f);
            _fill.sizeDelta = new Vector2(18f * ice, 2f);
        }

        private void CreateOverlay()
        {
            _panel = new GameObject("Winter Outbreak frozen food", typeof(RectTransform));
            var panel = (RectTransform)_panel.transform;
            Place(panel, transform, new Vector2(3f, -3f), new Vector2(18f, 20f));

            _icon = AddImage(panel, "Frostbite icon", Vector2.zero, new Vector2(16f, 16f), Color.white);
            _icon.preserveAspect = true;

            var background = AddImage(panel, "Ice bar background", new Vector2(0f, -18f),
                new Vector2(18f, 2f), new Color(0.08f, 0.16f, 0.26f, 0.9f));
            _fill = AddImage(background.rectTransform, "Ice bar", Vector2.zero,
                new Vector2(18f, 2f), new Color(0.33f, 0.72f, 1f)).rectTransform;
        }

        private static UnityEngine.UI.Image AddImage(Transform parent, string name, Vector2 position, Vector2 size,
            Color color)
        {
            var image = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Image))
                .GetComponent<UnityEngine.UI.Image>();
            Place(image.rectTransform, parent, position, size);
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static void Place(RectTransform rect, Transform parent, Vector2 position, Vector2 size)
        {
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }
    }
}
