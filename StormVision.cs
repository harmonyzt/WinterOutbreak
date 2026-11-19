using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private ConfigEntry<bool> _stormVisionEnabled;
        private ConfigEntry<float> _snowstormSightDistance;
        private ConfigEntry<float> _blizzardSightDistance;
        private ConfigEntry<float> _snowstormSightSpeed;
        private ConfigEntry<float> _blizzardSightSpeed;

        private void BindStormVisionSettings()
        {
            const string oldSection = "AI Vision";
            var sightRange = new AcceptableValueRange<float>(10f, 300f);
            var speedRange = new AcceptableValueRange<float>(0.1f, 1f);

            _stormVisionEnabled = BindSetting(AiVisionSection, "Enabled", true,
                "Storms limit how far and how quickly bots can see.", 1, oldSection: oldSection);

            _snowstormSightDistance = BindSetting(AiVisionSection, "Snowstorm Sight Distance (m)", 100f,
                "How far bots can see through a Snowstorm.", 2, true, sightRange, oldSection);

            _blizzardSightDistance = BindSetting(AiVisionSection, "Blizzard Sight Distance (m)", 55f,
                "How far bots can see through a Blizzard.", 3, true, sightRange, oldSection);

            _snowstormSightSpeed = BindSetting(AiVisionSection, "Snowstorm Spotting Speed", 0.8f,
                "How quickly bots spot you in a Snowstorm. 1 is normal speed. With SAIN, SAIN's own weather rules decide this.", 4,
                true, speedRange, oldSection);

            _blizzardSightSpeed = BindSetting(AiVisionSection, "Blizzard Spotting Speed", 0.6f,
                "How quickly bots spot you in a Blizzard. 1 is normal speed. With SAIN, SAIN's own weather rules decide this.", 5,
                true, speedRange, oldSection);
        }

        private const string SainId = "me.sol.sain";
        private static bool? _sainLoaded;

        private static bool SainLoaded =>
            _sainLoaded ??= BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(SainId);

        [HarmonyPatch(typeof(EnemyPartVision), nameof(EnemyPartVision.CheckLineOfSight))]
        private static class StormVisionPatch
        {
            [HarmonyPriority(Priority.Low)]
            private static void Prefix(BotOwner owner, EnemyPart part, ref float addSensorDistance,
                ref float visibilityChangeSpeedK)
            {
                var plugin = _plugin;
                if (plugin == null || plugin._weather == null || !plugin._stormVisionEnabled.Value)
                {
                    return;
                }

                var target = part.EnemyPlayer;
                if (target == null || (owner.AIData?.IsInside == true && target.AIData?.IsInside == true))
                {
                    return;
                }

                bool blizzard = plugin._preset == Blizzard;
                float distance = blizzard ? plugin._blizzardSightDistance.Value : plugin._snowstormSightDistance.Value;
                float speed = blizzard ? plugin._blizzardSightSpeed.Value : plugin._snowstormSightSpeed.Value;

                // The native check adds this offset to VisibleDist before comparing distance
                addSensorDistance = Mathf.Min(addSensorDistance, distance - owner.LookSensor.VisibleDist);

                // SAIN slows spotting in bad weather by itself, and it reads this mod's storm through the weather curve
                if (!SainLoaded)
                {
                    visibilityChangeSpeedK *= speed;
                }
            }
        }
    }
}
