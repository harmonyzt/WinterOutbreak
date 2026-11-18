using Audio.AmbientSubsystem;
using HarmonyLib;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private Fixed<float> _windVolume;
        private WindAmbientBlender _windBlender;
        private float _previousWindGain = 1f;

        private float WindGain => _weather != null ? _windVolume.Value / 100f : 1f;

        private void BindWindSettings()
        {
            _windVolume = new Fixed<float>(100f);
        }

        [HarmonyPatch(typeof(WindAmbientBlender), "UpdateWindSound")]
        private static class WindRefreshPatch
        {
            private static void Prefix(WindAmbientBlender __instance, ref bool force)
            {
                var plugin = _plugin;
                if (plugin == null)
                {
                    return;
                }

                float gain = plugin.WindGain;
                if (!ReferenceEquals(plugin._windBlender, __instance) || plugin._previousWindGain != gain)
                {
                    plugin._windBlender = __instance;
                    force |= plugin._previousWindGain != gain || gain != 1f;
                    plugin._previousWindGain = gain;
                }
            }
        }

        [HarmonyPatch(typeof(WindAmbientBlender), "GetWindVolume")]
        private static class WindVolumePatch
        {
            private static void Postfix(ref float __result)
            {
                if (_plugin != null)
                {
                    __result *= _plugin.WindGain;
                }
            }
        }
    }
}
