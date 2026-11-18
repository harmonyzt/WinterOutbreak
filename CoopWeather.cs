using System.Collections.Generic;
using UnityEngine;

namespace WinterOutbreak
{
    /// <summary>
    /// Fika: the host rolls the storm and runs the snow depth timeline; clients take both from it, so bots (which live
    /// on the host) and every player share one raid. Bonfires are passed along the same way.
    /// </summary>
    public sealed partial class WinterOutbreakPlugin
    {
        private const float HostWeatherTimeout = 30f;
        private const float WeatherShareSeconds = 10f;

        private SharedWeather? _sharedWeather;
        private float _sharedWeatherAt;
        private bool _presetFromHost;
        private float _weatherWaitStarted = -1f;
        private float _nextWeatherShare;

        internal static WinterOutbreakPlugin Instance => _plugin;

        /// <summary>
        /// Picks this raid's storm. A Fika client waits for the host's and returns false until it arrives, rolling its
        /// own only if the host never sends one (no Winter Outbreak there).
        /// </summary>
        private bool SelectRaidWeather(bool raidPreparing)
        {
            if (CoopSupport.IsClient)
            {
                if (_sharedWeather.HasValue)
                {
                    _rolled = true;
                    _presetFromHost = true;
                    _preset = FindPreset(_sharedWeather.Value.Preset);
                    return true;
                }

                if (_weatherWaitStarted < 0f)
                {
                    _weatherWaitStarted = Time.time;
                }

                if (Time.time - _weatherWaitStarted < HostWeatherTimeout)
                {
                    return false;
                }

                Logger.LogWarning("The Fika host sent no winter weather (is Winter Outbreak installed there?). Rolling locally.");
            }

            _rolled = true;
            _preset = RollPreset();
            StartSnowTimeline(raidPreparing);
            return true;
        }

        private static StormPreset FindPreset(string name)
        {
            if (name == Snowstorm.Name)
            {
                return Snowstorm;
            }

            return name == Blizzard.Name ? Blizzard : null;
        }

        /// <summary>
        /// Host: repeats the weather every few seconds, so late joiners and clock drift are covered.
        /// </summary>
        private void ShareWeather()
        {
            var network = CoopSupport.Network;
            if (network == null || !network.IsHost || !_rolled || Time.time < _nextWeatherShare)
            {
                return;
            }

            _nextWeatherShare = Time.time + WeatherShareSeconds;
            if (TryGetSharedWeather(out var weather))
            {
                network.SendWeather(weather);
            }
        }

        internal bool TryGetSharedWeather(out SharedWeather weather)
        {
            weather = default;
            if (!_rolled || _presetFromHost)
            {
                return false;
            }

            weather.Preset = _preset?.Name ?? "Normal";
            weather.Ice = IceSettingsText();
            weather.Sky = SkyText();
            if (_preset == null)
            {
                return true;
            }

            weather.MaxDepth = _snowDepthCentimeters.Value * 0.01f;
            weather.Depth = GetStormSnowDepth(out bool full);
            weather.DepthRate = full ? 0f : GetSnowDepthRate();
            return true;
        }

        // The sky goes along with the weather once the host has decided it, so every player sees the same aurora and
        // the same amount of extra Blizzard snow.
        private string SkyText()
        {
            if (!ReferenceEquals(_auroraDecidedFor, _raid) || !ReferenceEquals(_extraFlakesRaid, _raid))
            {
                return string.Empty;
            }

            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2:0.##}",
                _auroraThisRaid ? 1 : 0, _auroraGreenThisRaid ? 1 : 0, _extraFlakesPercent);
        }

        // A client takes the host's sky when it has arrived. False while it is still on its way.
        private bool TryGetHostSky(out bool aurora, out bool green, out float flakes)
        {
            aurora = false;
            green = false;
            flakes = 0f;
            string sky = _sharedWeather?.Sky;
            if (string.IsNullOrEmpty(sky))
            {
                return false;
            }

            string[] parts = sky.Split(',');
            if (parts.Length != 3
                || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out flakes))
            {
                return false;
            }

            aurora = parts[0] == "1";
            green = parts[1] == "1";
            return true;
        }

        // A client waits for the host's sky until the same timeout as the weather, then decides its own.
        private bool WaitingForHostSky => CoopSupport.IsClient && _weatherWaitStarted >= 0f
            && Time.time - _weatherWaitStarted < HostWeatherTimeout;

        internal bool NeedsHostWeather => _raid != null && !_rolled && !_sharedWeather.HasValue;

        internal void ReceiveSharedWeather(SharedWeather weather)
        {
            ReceiveHostIce(weather.Ice);
            if (_rolled && !_presetFromHost)
            {
                // Rolled locally after the timeout; keep it rather than switching storms mid-raid.
                return;
            }

            _sharedWeather = weather;
            _sharedWeatherAt = Time.time;
        }

        internal List<SharedBonfire> GetSharedBonfires()
        {
            var list = new List<SharedBonfire>();
            foreach (var bonfire in _bonfires)
            {
                if (bonfire.Root != null)
                {
                    list.Add(new SharedBonfire
                    {
                        Position = bonfire.Root.transform.position,
                        Yaw = bonfire.Root.transform.eulerAngles.y
                    });
                }
            }

            return list;
        }

        internal void ReceiveSharedBonfire(SharedBonfire shared)
        {
            if (_raid == null || IsNearOtherBonfire(shared.Position))
            {
                return;
            }

            EnsureBonfireAudio();
            var rotation = Quaternion.Euler(0f, shared.Yaw, 0f);
            var bonfire = BonfireVisuals.CreateBonfire(shared.Position, rotation,
                shared.Position + Vector3.up * BonfireHeatHeight, _bonfireLoopClip);
            bonfire.Remote = true;
            _bonfires.Add(bonfire);
            _fires.AddPlaced(bonfire.Heat);
        }

        private void ClearCoopWeather()
        {
            _sharedWeather = null;
            _presetFromHost = false;
            _weatherWaitStarted = -1f;
            _nextWeatherShare = 0f;
            ClearCoopIce();
        }
    }
}
