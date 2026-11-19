using System;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.UI.Matchmaker;
using EFT.Weather;
using HarmonyLib;

namespace WinterOutbreak
{
    [BepInPlugin(PluginId, "Winter Outbreak", PluginVersion)]
    public sealed partial class WinterOutbreakPlugin : BaseUnityPlugin
    {
        private const string PluginId = "com.harmonyzt.winteroutbreak";
        internal const string PluginVersion = "1.0.1";

        // Snowstorm weather (snowfall, fog, clouds, wind) is 40 % of Blizzard
        private const float SnowstormShare = 0.4f;

        // Snowstorm wind effects (drift, plumes, gusts, gust clouds) are about 62 % as strong as in a Blizzard and come about 40 % as often
        private const float SnowstormEffectStrength = 0.62f;
        private const float SnowstormGustRate = 0.45f;

        private static readonly StormPreset Blizzard = new StormPreset(
            "Blizzard", snowfall: 1f, fog: 0.12f, clouds: 0.9f,
            wind: 4f, stormFactor: 1f, sideSpeed: 0.25f);

        private static readonly StormPreset Snowstorm = new StormPreset(
            "Snowstorm", snowfall: Blizzard.Snowfall * SnowstormShare, fog: Blizzard.Fog * SnowstormShare,
            clouds: Blizzard.Clouds * SnowstormShare, wind: Blizzard.Wind * SnowstormShare,
            stormFactor: Blizzard.StormFactor * SnowstormShare, sideSpeed: Blizzard.SideSpeed * SnowstormShare);

        private static WinterOutbreakPlugin _plugin;

        private readonly System.Random _random = new System.Random();
        private ConfigEntry<int> _stormChance;
        private ConfigEntry<int> _snowstormWeight;
        private ConfigEntry<int> _blizzardWeight;
        private ConfigEntry<string> _disabledMaps;
        private Harmony _harmony;

        private GameWorld _raid;
        private GameWorld _countdownWorld;
        private MatchmakerFinalCountdown _countdownScreen;
        private bool _rolled;
        private bool _announced;
        private StormPreset _preset;
        private WeatherController _weather;
        private SnowFlakes _flakes;
        private StormWeatherCurve _curve;

        private void Awake()
        {
            _stormChance = BindSetting(StormsSection, "Storm Chance", 100,
                "Chance, in percent, that a winter raid gets a storm.", 1,
                range: new AcceptableValueRange<int>(0, 100), oldSection: "Storm Chances");

            _snowstormWeight = BindSetting(StormsSection, "Snowstorm Weight", 50,
                "How often a storm is a Snowstorm, compared with Blizzard Weight.", 2,
                range: new AcceptableValueRange<int>(0, 100), oldSection: "Storm Chances");

            _blizzardWeight = BindSetting(StormsSection, "Blizzard Weight", 50,
                "How often a storm is a Blizzard, compared with Snowstorm Weight.", 3,
                range: new AcceptableValueRange<int>(0, 100), oldSection: "Storm Chances");

            _disabledMaps = BindSetting(StormsSection, "Disabled Maps", "laboratory,labyrinth,terminal,icebreaker",
                "Maps that never get winter storms, separated by commas.", 4,
                advanced: true, oldSection: "Storm Chances");

            BindFrostbiteSettings();
            BindFrozenFoodSettings();
            BarrelSettings.Bind(Logger);
            BindWindSettings();
            BindBlizzardWindSettings();
            BindSnowStepSettings();
            BindBreathSettings();
            BindStormVisionSettings();
            BindSnowDepthSettings();
            BindSnowInteractionSettings();
            BindBlowingSnowSettings();
            BindTreeSnowSettings();
            BindGrenadeSnowSettings();
            BindAiSnowSettings();
            BindSnowSurfaceSettings();
            BindBtrSnowSettings();
            BindAuroraSettings();
            BindFrozenLakeSettings();
            BindBonfireSettings();
            SortSettingsBySection();

            _plugin = this;
            _harmony = new Harmony(PluginId);

            try
            {
                RegisterWinterEffects();
                _harmony.PatchAll(typeof(WinterOutbreakPlugin).Assembly);
            }
            catch (Exception exception)
            {
                RemoveWinterPenalties();
                _harmony.UnpatchSelf();
                _plugin = null;
                Logger.LogError($"Could not install Winter Outbreak patches: {exception}");
            }
        }

        private void OnDestroy()
        {
            if (_plugin == this)
            {
                EndRaid();
                RemoveWinterPenalties();
                _harmony.UnpatchSelf();
                _plugin = null;
            }
        }

        private void UpdateRaidWeather(WeatherController weather)
        {
            UpdateRaidWeatherCore(weather);
        }

        private void UpdateRaidWeatherCore(WeatherController weather)
        {
            if (WeatherController.Instance != weather)
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            if (world is HideoutGameWorld || world is NarrateGameWorld)
            {
                world = null;
            }

            TrackRaidWorld(world);

            var seasons = _raid != null ? _raid.SeasonsController : null;
            var rain = weather.RainController;
            var flakes = rain != null ? rain._snowFlakes : null;

            if (seasons?.Season != ESeason.Winter || seasons.Status != ESeasonStatus.Winter)
            {
                ClearWeather(world == null ? "no raid world" : "season is not winter");
                return;
            }

            if (IsDisabledMap(_raid?.LocationId))
            {
                ClearWeather("storms are off on this map");
                return;
            }

            var status = Singleton<AbstractGame>.Instance?.Status;
            bool raidStarted = status == GameStatus.Started;
            var raidPlayer = raidStarted ? _raid?.MainPlayer : null;
            UpdateWinterExposure(raidPlayer);
            UpdateBreathSmoke(raidPlayer);
            UpdateBonfires(raidPlayer);
            bool raidPreparing = _raid != null && ReferenceEquals(_countdownWorld, _raid);
            UpdateAurora(raidStarted, raidPreparing);
            UpdateFrozenLake(raidStarted || raidPreparing);
            if (raidStarted)
            {
                EnsureSnowStepAudio();
                _countdownWorld = null;
                _countdownScreen = null;
            }

            if (!_rolled)
            {
                // A Fika host rolls once the raid starts even without falling flakes, since a headless host draws none
                // but runs the bots that see through the storm
                bool hostInRaid = CoopSupport.IsHost && raidStarted;
                if (!raidPreparing && !hostInRaid && (flakes == null || !flakes.isActiveAndEnabled))
                {
                    return;
                }

                if (!SelectRaidWeather(raidPreparing))
                {
                    return;
                }
            }

            ShareWeather();

            if (_preset != null)
            {
                if (_flakes != flakes)
                {
                    ClearBlizzardFlakes();
                }

                _weather = weather;
                _flakes = flakes;
            }

            var stormPlayer = raidStarted && _preset != null ? _raid.MainPlayer : null;
            if (raidStarted || !raidPreparing)
            {
                UpdateSnowDepth(stormPlayer, false);
            }

            UpdateFarSnow(stormPlayer);
            UpdateGrenadeSnow(stormPlayer);
            UpdateSnowInteraction(stormPlayer, weather);
            UpdateAiSnow(stormPlayer);
            UpdateTreeSnow(stormPlayer);
            UpdateBlowingSnow(raidPlayer, weather);
            UpdateBlizzardWind(raidPlayer);
            UpdateSnowSurfaceShading();
            UpdateBtrSnow(stormPlayer);

            if (!_announced && raidStarted && Singleton<NotificationManager>.Instantiated)
            {
                _announced = true;
                NotificationManager.DisplayMessageNotification(
                    $"Winter weather: {_preset?.Name ?? "Normal"}.", ENotificationDurationType.Long);
            }
        }

        private void TrackRaidWorld(GameWorld world)
        {
            if (world == _raid)
            {
                return;
            }

            EndRaid();
            _raid = world;
            if (_raid != null)
            {
                GameWorld.OnDispose += EndRaid;
            }
        }

        private void PreloadCountdownSnow(MatchmakerFinalCountdown screen)
        {
            if (!ReferenceEquals(screen, _countdownScreen))
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            if (world == null || (_countdownWorld != null && !ReferenceEquals(world, _countdownWorld))
                || world is HideoutGameWorld || world is NarrateGameWorld
                || Singleton<AbstractGame>.Instance?.Status == GameStatus.Started)
            {
                return;
            }

            TrackRaidWorld(world);
            _countdownWorld = world;
            var seasons = world.SeasonsController;
            if (seasons?.Season != ESeason.Winter || seasons.Status != ESeasonStatus.Winter || world.MainPlayer == null)
            {
                return;
            }

            if (!_rolled && !SelectRaidWeather(true))
            {
                return;
            }

            if (_preset != null)
            {
                UpdateSnowDepth(world.MainPlayer, true);
            }
        }

        private StormPreset RollPreset()
        {
            int snowstorm = _snowstormWeight.Value;
            int total = snowstorm + _blizzardWeight.Value;

            if (total == 0 || _random.Next(100) >= _stormChance.Value)
            {
                return null;
            }

            return _random.Next(total) < snowstorm ? Snowstorm : Blizzard;
        }

        private void EndRaid()
        {
            GameWorld.OnDispose -= EndRaid;
            if (ReferenceEquals(_countdownWorld, _raid))
            {
                _countdownWorld = null;
                if (_raid != null)
                {
                    _countdownScreen = null;
                }
            }

            ClearFrozenFood();
            _temperatureHealth = null;
            _environmentTemperatureChange = 0f;
            ClearSnowStepAudio();
            ClearBonfireAudio();
            ClearBlizzardWind();
            _raid = null;
            _rolled = false;
            _snowTimelineStarted = false;
            ClearCoopWeather();
            _announced = false;
            _blizzardFlakesFailed = false;
            _preset = null;
            ClearWeather("raid ended");
        }

        [HarmonyPatch(typeof(MatchmakerFinalCountdown), "Show",
            new[] { typeof(Profile), typeof(DateTime) })]
        private static class RaidCountdownPatch
        {
            private static void Postfix(MatchmakerFinalCountdown __instance)
            {
                if (_plugin != null)
                {
                    _plugin._countdownWorld = Singleton<GameWorld>.Instance;
                    _plugin._countdownScreen = __instance;
                    _plugin.PreloadCountdownSnow(__instance);
                }
            }
        }

        [HarmonyPatch(typeof(MatchmakerFinalCountdown), "Update")]
        private static class RaidCountdownUpdatePatch
        {
            private static void Postfix(MatchmakerFinalCountdown __instance)
            {
                _plugin?.PreloadCountdownSnow(__instance);
            }
        }

        // Indoor and enclosed maps (Labs, Labyrinth, Terminal, Icebreaker) get no storm
        private bool IsDisabledMap(string location)
        {
            if (string.IsNullOrEmpty(location))
            {
                return false;
            }

            // This runs every frame, so the answer is kept until the map or the setting changes
            if (location == _disabledMapLocation && _disabledMaps.Value == _disabledMapSetting)
            {
                return _disabledMapResult;
            }

            _disabledMapLocation = location;
            _disabledMapSetting = _disabledMaps.Value;
            _disabledMapResult = false;
            foreach (string part in _disabledMapSetting.Split(','))
            {
                string name = part.Trim();
                if (name.Length > 0 && location.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _disabledMapResult = true;
                    break;
                }
            }

            return _disabledMapResult;
        }

        private string _disabledMapLocation;
        private string _disabledMapSetting;
        private bool _disabledMapResult;

        private void ClearWeather(string reason)
        {
            _weather = null;
            _flakes = null;
            ResetExposure();
            ClearBreathSmoke();
            ClearAurora();
            ClearFarSnow();
            ClearSnowDepth(reason);
            ClearSnowInteraction();
            ClearTreeSnow();
            ClearGrenadeSnow();
            ClearBtrSnow();
            ClearAiSnow();
            ClearBlizzardFlakes();
            ClearBlowingSnow();
            StopBlizzardWind();
            ClearFrozenLake();
            ClearBonfires();
            ClearCurve();
        }

        private void ClearCurve()
        {
            if (_curve != null)
            {
                _curve.Preset = null;
                _curve = null;
            }
        }

        [HarmonyPatch(typeof(WeatherController), nameof(WeatherController.LateUpdate))]
        private static class WeatherUpdatePatch
        {
            private static void Prefix(WeatherController __instance)
            {
                _plugin?.UpdateRaidWeather(__instance);
            }
        }

        [HarmonyPatch(typeof(WeatherController), nameof(WeatherController.WeatherCurve), MethodType.Getter)]
        private static class WeatherCurvePatch
        {
            private static void Postfix(WeatherController __instance, ref IWeatherCurve __result)
            {
                var plugin = _plugin;
                if (plugin == null || !ReferenceEquals(__instance, plugin._weather) || __result == null)
                {
                    return;
                }

                if (plugin._curve == null || !ReferenceEquals(plugin._curve.Source, __result))
                {
                    plugin.ClearCurve();
                    plugin._curve = new StormWeatherCurve(__result, plugin._preset);
                }

                __result = plugin._curve;
            }
        }
    }
}
