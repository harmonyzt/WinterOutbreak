using EFT.Weather;
using UnityEngine;

namespace WinterOutbreak
{
    internal sealed class StormPreset
    {
        public readonly string Name;
        public readonly float Snowfall;
        public readonly float Fog;
        public readonly float Clouds;
        public readonly float Wind;
        public readonly float StormFactor;
        public readonly float SideSpeed;

        public StormPreset(string name, float snowfall, float fog, float clouds, float wind, float stormFactor, float sideSpeed)
        {
            Name = name;
            Snowfall = snowfall;
            Fog = fog;
            Clouds = clouds;
            Wind = wind;
            StormFactor = stormFactor;
            SideSpeed = sideSpeed;
        }
    }

    internal sealed class StormWeatherCurve : IWeatherCurve
    {
        public IWeatherCurve Source { get; }
        public StormPreset Preset { get; set; }

        public StormWeatherCurve(IWeatherCurve source, StormPreset preset)
        {
            Source = source;
            Preset = preset;
        }

        public Vector2 Wind
        {
            get
            {
                Vector2 wind = Source.Wind;
                if (Preset == null)
                {
                    return wind;
                }

                Vector2 direction = wind.sqrMagnitude > 0.0001f ? wind.normalized : Vector2.right;
                return direction * Preset.Wind;
            }
        }

        public Vector2 TopWind => Source.TopWind;
        public float Rain => Preset?.Snowfall ?? Source.Rain;
        public float Fog => Preset?.Fog ?? Source.Fog;
        public float Cloudiness => Preset?.Clouds ?? Source.Cloudiness;
        public float Temperature => Source.Temperature;
        public float LightningThunderProbability => Source.LightningThunderProbability;
        public float CurrentNormalizedTimeValue => Source.CurrentNormalizedTimeValue;
    }
}
