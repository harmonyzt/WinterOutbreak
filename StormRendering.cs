using System;
using System.Collections.Generic;
using EFT.Weather;
using HarmonyLib;
using UnityEngine;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private const int FlakesPerLayer = 16383;
        private const float BlizzardFogFloor = 0.025f;
        private static readonly int FlakeSizeId = Shader.PropertyToID("_FlakesSize");
        private static readonly int SideSpeedId = Shader.PropertyToID("_SideSpeed");

        private readonly List<(MeshRenderer Renderer, Mesh Mesh)> _blizzardFlakes = new List<(MeshRenderer, Mesh)>();
        private bool _blizzardFlakesFailed;

        private bool IsBlizzard => _weather != null && _preset == Blizzard;

        private const float MaxExtraFlakesPercent = 35f;
        private object _extraFlakesRaid;
        private float _extraFlakesPercent;

        // Rolled once per raid; a Fika client takes the host's amount.
        private void DecideExtraFlakes()
        {
            if (ReferenceEquals(_extraFlakesRaid, _raid) || _raid == null)
            {
                return;
            }

            if (CoopSupport.IsClient)
            {
                if (TryGetHostSky(out _, out _, out float hostFlakes))
                {
                    _extraFlakesRaid = _raid;
                    _extraFlakesPercent = hostFlakes;
                    return;
                }

                if (WaitingForHostSky || _weatherWaitStarted < 0f)
                {
                    return;
                }
            }

            _extraFlakesRaid = _raid;
            float roll = (float)_random.NextDouble();
            _extraFlakesPercent = MaxExtraFlakesPercent * roll * roll;
        }

        private float ExtraFlakesPercentThisRaid()
        {
            if (!_extraBlizzardFlakes.Value)
            {
                return 0f;
            }

            DecideExtraFlakes();
            return _extraFlakesPercent;
        }

        private void UpdateBlizzardFlakes(SnowFlakes flakes, MeshRenderer[] stormLayers)
        {
            if (_blizzardFlakesFailed || stormLayers == null)
            {
                return;
            }

            int flakesPerLayer = Mathf.RoundToInt(FlakesPerLayer * ExtraFlakesPercentThisRaid() / 100f);
            if (_blizzardFlakes.Count == 0 && flakesPerLayer < 256)
            {
                // Wind-driven snow carries storm density now; the extra flake layers are optional.
                return;
            }

            if (_blizzardFlakes.Count == 0)
            {
                // The native mesh generator randomizes each layer. Preserve the raid's random stream.
                var randomState = UnityEngine.Random.state;
                try
                {
                    var properties = new MaterialPropertyBlock();
                    float sideSpeed = Mathf.LerpUnclamped(flakes.MinMaxSideSpeed.x, flakes.MinMaxSideSpeed.y, Blizzard.Snowfall) + Blizzard.StormFactor * Blizzard.SideSpeed;

                    for (int pair = 0; pair < 2; pair++)
                    {
                        for (int layerIndex = 0; layerIndex < stormLayers.Length; layerIndex++)
                        {
                            var layer = stormLayers[layerIndex];
                            var renderer = flakes.AddObject(flakesPerLayer, layer.sharedMaterial, "blizzard");
                            var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                            _blizzardFlakes.Add((renderer, mesh));

                            Vector2 size = Vector2.LerpUnclamped(flakes.FlakesSizeMin, flakes.FlakesSizeMax, Blizzard.Snowfall);
                            if (layerIndex == 1)
                            {
                                size *= flakes.FlakesFarSizeFactor;
                            }

                            // Mix finer snow with wider flakes and break up synchronized sideways motion.
                            float sizeScale = pair == 0 ? 0.68f : 1.18f;
                            float width = size.x * sizeScale * (0.8f + (float)_random.NextDouble() * 0.4f);
                            float length = size.y * sizeScale * (0.8f + (float)_random.NextDouble() * 0.4f);
                            float drift = sideSpeed * (0.75f + (float)_random.NextDouble() * 0.5f);

                            properties.SetVector(FlakeSizeId, new Vector4(width, length, 0f, 0f));
                            properties.SetFloat(SideSpeedId, drift);
                            renderer.SetPropertyBlock(properties);
                        }
                    }
                }
                catch (Exception exception)
                {
                    ClearBlizzardFlakes();
                    _blizzardFlakesFailed = true;
                    Logger.LogWarning($"Could not create extra blizzard flakes: {exception.Message}");
                }
                finally
                {
                    UnityEngine.Random.state = randomState;
                }
            }

            bool visible = flakes.Intensity > 0.001f;
            foreach (var layer in _blizzardFlakes)
            {
                if (layer.Renderer != null && layer.Renderer.enabled != visible)
                {
                    layer.Renderer.enabled = visible;
                }
            }
        }

        private void ClearBlizzardFlakes()
        {
            foreach (var layer in _blizzardFlakes)
            {
                if (layer.Renderer != null)
                {
                    layer.Renderer.enabled = false;
                    Destroy(layer.Renderer.gameObject);
                }

                Destroy(layer.Mesh);
            }

            _blizzardFlakes.Clear();
        }

        [HarmonyPatch(typeof(WeatherController), nameof(WeatherController.ControlScattering))]
        private static class ScatteringPatch
        {
            private static void Prefix(WeatherController __instance, ref float fog)
            {
                var plugin = _plugin;
                if (plugin != null && plugin._preset != null && ReferenceEquals(__instance, plugin._weather))
                {
                    fog = plugin.StormFog(fog);
                }
            }
        }

        [HarmonyPatch(typeof(TOD_Scattering), "OnRenderImageNormalMode")]
        private static class NormalScatteringPatch
        {
            private static void Prefix(TOD_Scattering __instance, out float? __state)
            {
                __state = null;
                var plugin = _plugin;
                if (plugin == null || !plugin.IsBlizzard)
                {
                    return;
                }

                // The normal renderer can keep its own density when weather debug is off.
                __state = __instance.GlobalDensity;
                __instance.GlobalDensity = Mathf.Max(__instance.GlobalDensity, BlizzardFogFloor);
            }

            private static void Finalizer(TOD_Scattering __instance, float? __state)
            {
                if (__state.HasValue)
                {
                    __instance.GlobalDensity = __state.Value;
                }
            }
        }

        [HarmonyPatch(typeof(SnowFlakes), nameof(SnowFlakes.Update))]
        private static class SnowFlakesPatch
        {
            private static void Prefix(SnowFlakes __instance, MeshRenderer[] ____flakesInStorm, out (bool Applied, float Factor, float Speed, Vector2 SizeMax) __state)
            {
                __state = default;
                var plugin = _plugin;
                if (plugin == null || !ReferenceEquals(__instance, plugin._flakes))
                {
                    return;
                }

                __state = (true, __instance.StormFactor, __instance.StormSideSpeed, __instance.FlakesSizeMax);
                __instance.StormFactor = plugin._preset.StormFactor;
                __instance.StormSideSpeed = plugin._preset.SideSpeed;

                if (plugin.IsBlizzard)
                {
                    // Stock flake size; wind-driven snow carries the storm's density now.
                    plugin.UpdateBlizzardFlakes(__instance, ____flakesInStorm);
                }
            }

            private static void Finalizer(SnowFlakes __instance, (bool Applied, float Factor, float Speed, Vector2 SizeMax) __state)
            {
                if (__state.Applied)
                {
                    __instance.StormFactor = __state.Factor;
                    __instance.StormSideSpeed = __state.Speed;
                    __instance.FlakesSizeMax = __state.SizeMax;
                }
            }
        }
    }
}
