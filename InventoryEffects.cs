using System;
using System.Collections.Generic;
using EFT.HealthSystem;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private static bool IsWinterEffect(IHealthEffect effect)
        {
            return effect is IWinterLowTemperature || effect is IWinterFrostbite
                || effect is IWinterWarmingUp || effect is IWinterSnowSlowed || effect is IWinterGearWarmth;
        }

        /// <summary>
        /// Makes sure a started effect and its icon reach the inventory list as well
        /// </summary>
        [HarmonyPatch(typeof(EffectsPanel), nameof(EffectsPanel.UpdateEffects))]
        private static class InventoryEffectsPatch
        {
            private static void Postfix(IHealthController ____healthController, EBodyPart ____bodyPart,
                Diz.Binding.BindableList<EffectDescription> ____bindableActiveEffects,
                Dictionary<Type, Sprite> ____icons, IEnumerable<Type> ____effectsFilterList)
            {
                try
                {
                    if (____effectsFilterList == null)
                    {
                        AddMissingWinterEffects(____healthController, ____bodyPart, ____bindableActiveEffects, ____icons);
                    }
                }
                catch (Exception exception)
                {
                    _plugin?.Logger.LogWarning($"Winter Outbreak could not list its effects in the health panel: {exception}");
                }
            }

            private static void AddMissingWinterEffects(IHealthController health, EBodyPart bodyPart,
                Diz.Binding.BindableList<EffectDescription> shown, Dictionary<Type, Sprite> icons)
            {
                if (health == null || shown == null)
                {
                    return;
                }

                var table = EFTHardSettings.Instance?.StaticIcons?.EffectIcons?.EffectIcons;
                foreach (var effect in health.GetAllEffects(bodyPart))
                {
                    if (!IsWinterEffect(effect) || effect.State != EEffectState.Started)
                    {
                        continue;
                    }

                    Type type = effect.Type;
                    if (table == null || !table.TryGetValue(type, out Sprite sprite) || sprite == null)
                    {
                        continue;
                    }

                    if (icons != null && !icons.ContainsKey(type))
                    {
                        icons[type] = sprite;
                    }

                    bool listed = false;
                    for (int i = 0; i < shown.Count && !listed; i++)
                    {
                        listed = shown[i]?.Type == type;
                    }

                    if (listed)
                    {
                        continue;
                    }

                    foreach (var description in effect.DisplayableVariations)
                    {
                        shown.Add(description);
                    }
                }
            }
        }
    }
}
