using BepInEx.Configuration;
using UnityEngine;

namespace WinterOutbreak
{
    /// <summary>
    /// Only the GPU snow material reads these; with CPU prints they have no effect.
    /// </summary>
    public sealed partial class WinterOutbreakPlugin
    {
        private static readonly int WindId = Shader.PropertyToID("_WSWind");
        private static readonly int SurfaceId = Shader.PropertyToID("_WSSurface");
        private static readonly int SunId = Shader.PropertyToID("_WSSun");
        private static readonly int RimParamsId = Shader.PropertyToID("_WSRimParams");
        private static readonly int ClockId = Shader.PropertyToID("_WSClock");

        private ConfigEntry<float> _windRipples;
        private ConfigEntry<float> _snowSparkle;
        private ConfigEntry<float> _packedSnow;
        private ConfigEntry<float> _printRims;
        private ConfigEntry<SnowShaderQuality> _snowQuality;
        private bool? _lowQualityApplied;

        public enum SnowShaderQuality
        {
            High,
            Low
        }
        private ConfigEntry<bool> _gpuSnowPrints;
        private Vector2 _rippleDirection = Vector2.right;
        private float _rippleStrength;
        private float _rippleTravel;

        private void BindSnowSurfaceSettings()
        {
            BindBulletSnowSettings();
            const string oldSection = "Snow Surface";

            _snowQuality = BindSetting(PerformanceSection, "Snow Quality", SnowShaderQuality.High,
                "Low is cheaper for weaker graphics cards: simpler ripples, no sparkle and simpler prints.", 1,
                oldSection: oldSection, oldKey: "Quality");
            _gpuSnowPrints = BindSetting(PerformanceSection, "GPU Snow Prints", true,
                "Draws footprints, prone and body marks and craters on the graphics card, which is faster. "
                + "Turn off only if prints look wrong. Applies from the next raid.", 10,
                advanced: true, oldSection: oldSection);

            _windRipples = BindSetting(SnowSection, "Wind Ripples", 1f,
                "Small ridges the wind carves across the snow. 0 turns them off.", 10,
                true, new AcceptableValueRange<float>(0f, 2f), oldSection);
            _snowSparkle = BindSetting(SnowSection, "Sparkle", 1f,
                "Glints from snow crystals in sunlight. 0 turns them off.", 11,
                true, new AcceptableValueRange<float>(0f, 2f), oldSection);
            _packedSnow = BindSetting(SnowSection, "Packed Snow", 1f,
                "How much darker and glossier trampled snow in prints and tracks looks. 0 turns it off.", 12,
                true, new AcceptableValueRange<float>(0f, 1f), oldSection);
            _printRims = BindSetting(SnowSection, "Print Rims", 1f,
                "The lip of snow pushed up around prints, tracks and craters. 0 turns it off.", 13,
                true, new AcceptableValueRange<float>(0f, 2f), oldSection);
        }

        private void UpdateSnowSurfaceShading()
        {
            UpdateSnowCoatsSafely();
            float dt = Time.deltaTime;
            if (_stormWind != null)
            {
                // Ripples follow the prevailing wind and build with the storm
                var prevailing = new Vector2(_stormWind.Prevailing.x, _stormWind.Prevailing.z);
                if (prevailing.sqrMagnitude > 0.01f)
                {
                    _rippleDirection = Vector2.Lerp(_rippleDirection, prevailing.normalized, Mathf.Clamp01(dt * 0.05f))
                        .normalized;
                }

                _rippleStrength = Mathf.MoveTowards(_rippleStrength, Mathf.Clamp01(_stormWind.Severity * 1.2f), dt * 0.01f);
                _rippleTravel = _stormWind.Advected;
            }
            else
            {
                // Old snow keeps a trace of earlier wind even without blowing snow
                _rippleStrength = Mathf.MoveTowards(_rippleStrength, 0.35f, dt * 0.01f);
            }

            bool low = _snowQuality.Value == SnowShaderQuality.Low;
            if (_lowQualityApplied != low)
            {
                _lowQualityApplied = low;
                if (low)
                {
                    Shader.EnableKeyword("WS_SNOW_LOW");
                }
                else
                {
                    Shader.DisableKeyword("WS_SNOW_LOW");
                }
            }

            Shader.SetGlobalVector(WindId, new Vector4(_rippleDirection.x, _rippleDirection.y, _rippleStrength, _rippleTravel));
            Shader.SetGlobalVector(SurfaceId, new Vector4(_windRipples.Value, _snowSparkle.Value, _packedSnow.Value, 0f));
            Shader.SetGlobalVector(RimParamsId, new Vector4(SnowPrintTexture.RimHeightScale, _printRims.Value, 0f, 0f));

            var sky = TOD_Sky.Instance;
            Vector4 sun = Vector4.zero;
            if (sky != null && sky.IsDay)
            {
                Vector3 direction = sky.SunDirection.normalized;
                float brightness = Mathf.Clamp01(sky.SunVisibility) * Mathf.Clamp01(sky.LightIntensity)
                    * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.02f, 0.18f, direction.y));
                sun = new Vector4(direction.x, direction.y, direction.z, brightness);
            }

            Shader.SetGlobalVector(SunId, sun);
            // Game time, matching the refined-at times in the snow meshes' UV3.z
            Shader.SetGlobalVector(ClockId, new Vector4(Time.time, 0f, 0f, 0f));
        }
    }
}
