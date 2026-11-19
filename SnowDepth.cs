using System;
using BepInEx.Configuration;
using EFT;
using EFT.EnvironmentEffect;
using UnityEngine;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private ConfigEntry<bool> _snowDepthEnabled;
        private ConfigEntry<float> _snowDepthCentimeters;
        private ConfigEntry<float> _snowDepthSeconds;
        private ConfigEntry<float> _snowStreamRadius;
        private ConfigEntry<float> _snowFrameBudget;

        private VoxelSnowArea _voxelSnow;
        private float _snowDepthStarted;
        private float _snowStartingDepth;
        private bool _snowTimelineStarted;
        private int _lastSnowFrame = -1;
        private bool _preloadedSnow;
        private float _nextSnowGapLog;
        private string _snowMapName;

        private void BindSnowDepthSettings()
        {
            const string oldSection = "Snow Depth";

            _snowDepthEnabled = BindSetting(SnowSection, "Snow Depth", true,
                "Snow builds up on the ground during winter storms.", 1,
                oldSection: oldSection, oldKey: "Enabled");

            _snowDepthCentimeters = BindSetting(SnowSection, "Max Snow Depth (cm)", 16f,
                "Deepest the snow gets, in centimetres.", 2,
                false, new AcceptableValueRange<float>(2f, 30f), oldSection, "Max Depth (cm)");

            _snowDepthSeconds = BindSetting(SnowSection, "Seconds To Full Depth", 180f,
                "How long a Blizzard takes to build the snow up to full depth.", 3,
                true, new AcceptableValueRange<float>(15f, 600f), oldSection);

            _snowStreamRadius = BindSetting(PerformanceSection, "Snow Detail Radius (m)", 75f,
                "How far around you the snow is drawn in full detail; the far snow covers the rest. "
                + "Lower is a little faster, but the change to full detail shows closer to you. "
                + "Applies from the next raid.", 2,
                false, new AcceptableValueRange<float>(33f, 100f), oldSection, "Detail Radius (m)");

            _snowFrameBudget = BindSetting(PerformanceSection, "Snow CPU Budget (ms)", 1.5f,
                "Most time the snow may spend building each frame, in milliseconds. Lower keeps "
                + "the frame rate higher; the detailed snow then fills in more slowly while you run, and the far snow "
                + "is shown until it catches up.", 3,
                false, new AcceptableValueRange<float>(0.5f, 4f), oldSection, "CPU Budget (ms)");

            BindFarSnowSettings();
        }

        private void UpdateSnowDepth(Player player, bool raidPreparing)
        {
            if (!_snowDepthEnabled.Value)
            {
                ClearSnowDepth("disabled in settings");
                return;
            }

            if (player == null)
            {
                // Raid end clears the snow through EndRaid
                if (_voxelSnow != null && Time.time >= _nextSnowGapLog)
                {
                    _nextSnowGapLog = Time.time + 10f;
                }

                return;
            }

            bool suppressIndoorSnow = IsInterchange(_raid?.LocationId) || IsInterchange(player.Location);
            if (_voxelSnow != null && _voxelSnow.SuppressIndoorSnow != suppressIndoorSnow)
            {
                ClearSnowDepth("Interchange indoor rule changed");
            }

            if (_voxelSnow == null)
            {
                if (!_snowTimelineStarted)
                {
                    StartSnowTimeline(raidPreparing);
                }

                _voxelSnow = new VoxelSnowArea(player.Position, _snowStreamRadius.Value, suppressIndoorSnow,
                    _maxFootprintsPerChunk.Value, _gpuSnowPrints.Value);
                _snowMapName = _raid?.LocationId;
                LoadStairCache();
                AddKnownFiresToSnow();
                _preloadedSnow = raidPreparing;
            }

            if (_lastSnowFrame == Time.frameCount)
            {
                return;
            }

            _lastSnowFrame = Time.frameCount;
            if (!raidPreparing && _preloadedSnow)
            {
                _preloadedSnow = false;
            }

            float depth = GetStormSnowDepth(out bool full);
            _voxelSnow.FrameBudgetMs = _snowFrameBudget.Value;
            _voxelSnow.Update(player.Position, depth, full, raidPreparing);
            if (!raidPreparing)
            {
                _voxelSnow.HideDecalsUnderSnow(depth);
                _voxelSnow.UpdateStairSnow(depth);
            }
        }

        private void AddKnownFiresToSnow()
        {
            int count = 0;
            foreach (var flame in FindObjectsOfType<EFT.Interactive.FlameDamageTrigger>())
            {
                _voxelSnow.AddFire(flame.transform.position, true);
                count++;
            }

            foreach (var fire in _fires.Lit)
            {
                _voxelSnow.AddFire(fire, true);
            }
        }

        // Stair flights measured on a map are kept in BepInEx/cache/WinterOutbreak between raids (VoxelStairCache.cs)
        private string StairCachePath()
        {
            return string.IsNullOrEmpty(_snowMapName)
                ? null
                : System.IO.Path.Combine(BepInEx.Paths.CachePath, "WinterOutbreak", $"stairs_{_snowMapName}.txt");
        }

        private void LoadStairCache()
        {
            string path = StairCachePath();
            if (path == null)
            {
                return;
            }

            try
            {
                // Load first
                string result = _voxelSnow.LoadStairCache(path, VoxelSnowArea.StairCacheHeader(_snowMapName));
            }
            catch (Exception error)
            {
                Logger.LogWarning($"Stair cache could not be read: {error.Message}");
            }
        }

        private void SaveStairCache()
        {
            string path = StairCachePath();
            if (path == null)
            {
                return;
            }

            try
            {
                string result = _voxelSnow.SaveStairCache(path, VoxelSnowArea.StairCacheHeader(_snowMapName));
            }
            catch (Exception error)
            {
                Logger.LogWarning($"Stair cache could not be written: {error.Message}");
            }
        }

        private static bool IsInterchange(string location)
        {
            return string.Equals(location, "interchange", StringComparison.OrdinalIgnoreCase);
        }

        private void ClearSnowDepth(string reason)
        {
            if (_voxelSnow != null)
            {
                SaveStairCache();
            }

            _voxelSnow?.Dispose();
            _voxelSnow = null;
            _lastSnowFrame = -1;
            _preloadedSnow = false;
        }

        /// <summary>
        /// Starts the storm's depth timeline.
        /// </summary>
        private void StartSnowTimeline(bool raidPreparing)
        {
            float maxDepth = _snowDepthCentimeters.Value * 0.01f;
            _snowTimelineStarted = true;
            _snowDepthStarted = Time.time;
            _snowStartingDepth = raidPreparing ? Mathf.Min(VoxelSnowArea.CountdownDepth, maxDepth) : 0f;
        }

        /// <summary>
        /// The storm's snow depth now.
        /// </summary>
        private float GetStormSnowDepth(out bool full)
        {
            if (_presetFromHost && _sharedWeather.HasValue)
            {
                var shared = _sharedWeather.Value;
                float grown = shared.Depth + shared.DepthRate * (Time.time - _sharedWeatherAt);
                full = shared.DepthRate <= 0f || grown >= shared.MaxDepth;
                return Mathf.Min(grown, shared.MaxDepth);
            }

            if (!_snowTimelineStarted || _preset == null)
            {
                full = false;
                return 0f;
            }

            float maxDepth = _snowDepthCentimeters.Value * 0.01f;
            float progress = (Time.time - _snowDepthStarted) * _preset.Snowfall / _snowDepthSeconds.Value;
            full = progress >= 1f;
            return Mathf.Lerp(Mathf.Min(_snowStartingDepth, maxDepth), maxDepth, progress);
        }

        /// <summary>
        /// How fast the depth grows while the storm is still filling in, in metres per second.
        /// </summary>
        private float GetSnowDepthRate()
        {
            float maxDepth = _snowDepthCentimeters.Value * 0.01f;
            float range = maxDepth - Mathf.Min(_snowStartingDepth, maxDepth);
            return range * _preset.Snowfall / _snowDepthSeconds.Value;
        }

        internal bool TryGetVoxelSnowDepth(Vector3 feetPosition, out float depth)
        {
            depth = 0f;
            return _voxelSnow != null && _voxelSnow.TryGetDepth(feetPosition, out depth);
        }
    }

    internal static class SnowSurface
    {
        internal static bool IsInIndoorVolume(Vector3 position)
        {
            var environment = EnvironmentManager.Instance;
            return environment != null && environment.GetEnvironmentByPos(position) == EnvironmentType.Indoor;
        }

        internal static Material CreateSnowMaterial()
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null || !shader.isSupported)
            {
                shader = Shader.Find("Legacy Shaders/Diffuse");
            }

            if (shader == null || !shader.isSupported)
            {
                return null;
            }

            var material = new Material(shader) { color = new Color(0.89f, 0.90f, 0.91f) };
            if (material.HasProperty("_Glossiness"))
            {
                material.SetFloat("_Glossiness", 0.12f);
            }

            return material;
        }

        internal static bool TrySampleGround(Vector3 position, int terrainMask, int groundMask, out RaycastHit ground)
        {
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            bool hasTerrain = Physics.Raycast(position + Vector3.up * 160f, Vector3.down,
                out var terrain, 320f, terrainMask, triggers);
            FrozenIce.RaiseTerrain(position, ref terrain, ref hasTerrain);

            // Use the terrain as a local elevation reference across the wider area
            float height = hasTerrain ? terrain.point.y + 0.65f : position.y + 2f;
            var origin = new Vector3(position.x, height, position.z);
            float reach = hasTerrain ? 1.3f : 9f;

            if (!Physics.Raycast(origin, Vector3.down, out ground, reach, groundMask, triggers))
            {
                if (!hasTerrain)
                {
                    return false;
                }

                ground = terrain;
            }

            return ground.normal.y >= 0.65f
                && !Physics.Raycast(ground.point + Vector3.up * 0.08f, Vector3.up, 60f, groundMask, triggers);
        }
    }
}
