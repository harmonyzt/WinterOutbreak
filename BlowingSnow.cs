using BepInEx.Configuration;
using EFT;
using EFT.Weather;
using UnityEngine;

namespace WinterOutbreak
{
    /// <summary>
    /// Wind-driven snow
    /// </summary>
    public sealed partial class WinterOutbreakPlugin
    {
        private ConfigEntry<bool> _blowingSnowEnabled;
        private ConfigEntry<bool> _blowingSnowCalmWinter;
        private ConfigEntry<float> _groundDriftIntensity;
        private ConfigEntry<float> _plumeIntensity;
        private ConfigEntry<float> _hazeIntensity;
        private ConfigEntry<float> _gustStrength;
        private ConfigEntry<bool> _extraBlizzardFlakes;
        private Fixed<int> _hazeCards;

        private StormWind _stormWind;
        private WindFlow _windFlow;
        private SnowSources _snowSources;
        private SnowSources _farSources;
        private ConfigEntry<float> _farIntensity;
        private bool _softParticlesLogged;
        private GroundSnowDrift _groundDrift;
        private SnowPlumes _snowPlumes;
        private SnowHaze _snowHaze;
        private bool _blowingSnowWarned;
        private float _fogPulse;
        private Color _snowLight = Color.white;

        private void BindBlowingSnowSettings()
        {
            const string oldSection = "Blowing Snow";
            var range = new AcceptableValueRange<float>(0f, 2f);

            _blowingSnowEnabled = BindSetting(BlowingSnowSection, "Enabled", true,
                "Wind blows snow along the ground, lifts it into plumes and gusts, and drives haze through the air.", 1,
                oldSection: oldSection);
            _blowingSnowCalmWinter = BindSetting(BlowingSnowSection, "In Calm Winter Weather", true,
                "Also blow a little snow in winter raids without a storm.", 2, oldSection: oldSection);
            _extraBlizzardFlakes = BindSetting(BlowingSnowSection, "Extra Blizzard Snowflakes", true,
                "Adds a random amount of extra falling snow in Blizzard, different in each raid. Applies from the next raid.", 3,
                oldSection: oldSection);

            _groundDriftIntensity = BindSetting(BlowingSnowSection, "Ground Drift", 1f,
                "Snow streaming along the ground. 0 turns it off.", 10, true, range, oldSection);
            _plumeIntensity = BindSetting(BlowingSnowSection, "Plumes", 1f,
                "Snow lifted into plumes, swirls and gust clouds. 0 turns it off.", 11, true, range, oldSection);
            _hazeIntensity = BindSetting(BlowingSnowSection, "Haze", 1f,
                "Fine snow drifting through the air. 0 turns it off.", 12, true, range, oldSection);
            _farIntensity = BindSetting(BlowingSnowSection, "Distant Snow", 1f,
                "Blowing snow far away from you (about 40 to 150 m). 0 turns it off.", 13, true, range, oldSection);
            _gustStrength = BindSetting(BlowingSnowSection, "Gust Strength", 1f,
                "How strong the gusts are. 0 keeps the wind steady.", 14, true, range, oldSection);
            _hazeCards = new Fixed<int>(18);
        }

        private float BlowingSeverity(Vector2 wind)
        {
            if (_preset == Blizzard)
            {
                return 1f;
            }

            if (_preset == Snowstorm)
            {
                return SnowstormEffectStrength;
            }

            return _blowingSnowCalmWinter.Value ? Mathf.Clamp01((wind.magnitude - 0.5f) / 4f) * 0.4f : 0f;
        }

        private void UpdateBlowingSnow(Player player, WeatherController weather)
        {
            var curve = weather != null ? weather.WeatherCurve : null;
            if (player == null || curve == null || !_blowingSnowEnabled.Value)
            {
                ClearBlowingSnow();
                return;
            }

            Vector2 weatherWind = curve.Wind;
            float severity = BlowingSeverity(weatherWind);
            if (_stormWind == null)
            {
                if (severity <= 0f)
                {
                    return;
                }

                _stormWind = new StormWind();
            }

            float dt = Time.deltaTime;
            float gustRate = _preset == Snowstorm ? SnowstormGustRate : 1f;
            _stormWind.Update(weatherWind, severity, gustRate, _gustStrength.Value, dt);
            if (!EnsureBlowingSnowEffects())
            {
                return;
            }

            var camera = Camera.main;
            Vector3 eye = camera != null ? camera.transform.position : player.Position + Vector3.up * 1.6f;
            bool outdoors = player.Environment == EnvironmentType.Outdoor;
            _snowLight = Color.Lerp(_snowLight, SampleSnowLight(), Mathf.Clamp01(dt * 2f));

            // Effects centre on the player's feet so they hug the local ground
            Vector3 center = player.Position;
            Vector3 look = camera != null ? camera.transform.forward : _stormWind.Direction;
            _groundDrift.Update(_stormWind);
            _snowPlumes.Update(_stormWind, dt);
            _snowSources.Update(center, eye, look, _stormWind, _windFlow, _groundDrift, _snowPlumes,
                _groundDriftIntensity.Value, _plumeIntensity.Value, _snowLight, outdoors, dt);
            float far = _farIntensity.Value;
            _farSources.Zoom = OpticZoom(player);
            _farSources.Update(center, eye, look, _stormWind, _windFlow, _groundDrift, _snowPlumes,
                _groundDriftIntensity.Value * far, _plumeIntensity.Value * far, _snowLight, outdoors && far > 0.001f, dt);
            _snowHaze.Update(eye, center.y, _stormWind, _hazeIntensity.Value, _snowLight, outdoors, dt);

            float pulse = outdoors
                ? _hazeIntensity.Value * _stormWind.Severity
                    * (_stormWind.Pocket(eye) * 0.8f + _stormWind.FrontPassing * 1.1f + _stormWind.GustAt(eye, eye) * 0.4f)
                : 0f;
            _fogPulse = Mathf.MoveTowards(_fogPulse, pulse, dt * 0.4f);
        }

        private static float OpticZoom(Player player)
        {
            try
            {
                var animation = player.ProceduralWeaponAnimation;
                var scope = animation != null && animation.IsAiming ? animation.CurrentScope : null;
                if (scope != null && scope.IsOptic && scope.Mod != null)
                {
                    return Mathf.Max(1f, scope.Mod.GetCurrentOpticZoom());
                }
            }
            catch
            {
                // Weapon state can be mid-swap
            }

            return 1f;
        }

        private bool EnsureBlowingSnowEffects()
        {
            if (_groundDrift == null)
            {
                _windFlow = new WindFlow(LayersMaskController.HighPolyWithTerrainNoGrassMask);
                _snowSources = new SnowSources();
                _farSources = new SnowSources(20f, 42f, 150f, true);
                _groundDrift = new GroundSnowDrift();
                _snowPlumes = new SnowPlumes();
                _snowHaze = new SnowHaze(_hazeCards.Value);
            }

            bool available = _groundDrift.IsAvailable && _snowPlumes.IsAvailable && _snowHaze.IsAvailable;
            if (!available && !_blowingSnowWarned)
            {
                _blowingSnowWarned = true;
                Logger.LogWarning("Blowing snow could not find a supported transparent particle shader.");
            }

            if (available && !_softParticlesLogged)
            {
                _softParticlesLogged = true;
                var camera = Camera.main;
            }

            return available;
        }

        /// <summary>
        /// Unlit particles would glow at night, so tint them by the sky's ambient and key light.
        /// </summary>
        internal static Color SampleSnowLight()
        {
            var sky = TOD_Sky.Instance;
            if (sky == null)
            {
                return Color.white;
            }

            Color light = sky.AmbientColor + sky.LightColor * (Mathf.Clamp01(sky.LightIntensity) * 0.35f);

            // Keep the brightness but only a trace of the sky's hue
            float luminance = Mathf.Clamp(light.r * 0.3f + light.g * 0.59f + light.b * 0.11f, 0.04f, 1f);
            var grey = new Color(luminance, luminance, luminance, 1f);
            Color tinted = Color.Lerp(grey, light, 0.15f);
            float peak = Mathf.Max(tinted.r, Mathf.Max(tinted.g, tinted.b));
            if (peak > 1f)
            {
                tinted /= peak;
            }

            tinted.a = 1f;
            return tinted;
        }

        /// <summary>
        /// Storm fog rises and falls with passing pockets and gust clouds rather than sitting at one high density.
        /// </summary>
        private float StormFog(float density)
        {
            float pulse = Mathf.Clamp(_fogPulse, 0f, 1.5f);
            if (IsBlizzard)
            {
                float steady = Mathf.Max(density, Mathf.Clamp(density * 2f, BlizzardFogFloor, 0.12f));
                return Mathf.Min(steady * (1f + pulse * 1.5f), 0.22f);
            }

            return density * (1.2f + pulse * 1.1f);
        }

        private void ClearBlowingSnow()
        {
            _groundDrift?.Dispose();
            _groundDrift = null;
            _snowPlumes?.Dispose();
            _snowPlumes = null;
            _snowHaze?.Dispose();
            _snowHaze = null;
            _stormWind = null;
            _windFlow = null;
            _snowSources = null;
            _farSources = null;
            _fogPulse = 0f;
        }
    }
}
