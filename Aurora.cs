using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private const string AuroraPrefabPath = "assets/aurora/atmosphere.prefab";
        private const string AuroraGreenMaterialPath = "assets/aurora/aurora2.mat";
        private const string AuroraSection = "Aurora";

        private static readonly int ColFactorId = Shader.PropertyToID("_ColFactor");
        private static readonly int ColPowId = Shader.PropertyToID("_ColPow");
        private static readonly int CloudDensityId = Shader.PropertyToID("_CloudDensity");
        private static readonly int SpeedId = Shader.PropertyToID("_Speed");
        private static readonly int FlowSpeedId = Shader.PropertyToID("_FlowSpeed");
        private static readonly int WaveAmountId = Shader.PropertyToID("_WaveAmount");
        private static readonly int StepsId = Shader.PropertyToID("_Steps");
        private static readonly int AuroraColorId = Shader.PropertyToID("_Color");
        private static readonly int AuroraColor2Id = Shader.PropertyToID("_Color2");
        private static readonly int AuroraFogColorId = Shader.PropertyToID("_FogColor");

        private ConfigEntry<bool> _auroraEnabled;
        private ConfigEntry<bool> _auroraForce;
        private ConfigEntry<AuroraColour> _auroraColour;
        private ConfigEntry<float> _auroraRadius;
        private ConfigEntry<bool> _auroraOverride;
        private ConfigEntry<float> _auroraBrightness;
        private ConfigEntry<float> _auroraColorPower;
        private ConfigEntry<float> _auroraCloudDensity;
        private ConfigEntry<float> _auroraSpeed;
        private ConfigEntry<float> _auroraFlowSpeed;
        private ConfigEntry<float> _auroraWaveAmount;
        private ConfigEntry<int> _auroraSteps;
        private ConfigEntry<Color> _auroraColor;
        private ConfigEntry<Color> _auroraColor2;
        private ConfigEntry<Color> _auroraFogColor;

        // The bundle's dome is a unit sphere scaled to 1000 m, drawn from the inside by an opaque shader that writes
        // depth. It replaces the sky behind it, so it only runs at night.
        private const float AuroraMaxRadius = 1000f;
        private const float AuroraFarClipShare = 0.95f;

        private AssetBundle _auroraBundle;
        private GameObject _auroraPrefab;
        private Material _auroraGreenMaterial;
        private Renderer _auroraRenderer;
        private Material _auroraMaterial;
        private Material _auroraBaseMaterial;
        private bool _auroraMaterialGreen;
        private bool _auroraOverrideApplied;
        private GameObject _aurora;
        private bool _auroraLoadAttempted;
        private bool _auroraVisible;
        private Material _auroraPrefabMaterial;

        private object _auroraDecidedFor;
        private bool _auroraThisRaid;
        private bool _auroraGreenThisRaid;
        private const float AuroraFromHour = 22f;
        private const float AuroraUntilHour = 4f;
        private const float AuroraChance = 0.25f;

        internal enum AuroraColour
        {
            Random,
            Violet,
            Green
        }

        private void BindAuroraSettings()
        {
            _auroraEnabled = Config.Bind(AuroraSection, "Enabled", true,
                AuroraDescription("A winter raid that starts between 22:00 and 04:00 has a 25 % chance of northern lights.", 20));
            _auroraForce = Config.Bind(AuroraSection, "Force Show", false,
                AuroraDescription("Show the northern lights in every winter raid, at any time of day. Applies from the next raid.", 19));
            _auroraColour = Config.Bind(AuroraSection, "Colour", AuroraColour.Random,
                AuroraDescription("Colour of the northern lights. Random picks violet or green for each raid.", 18));
            _auroraRadius = Config.Bind(AuroraSection, "Dome Radius (m)", AuroraMaxRadius,
                AuroraDescription("How far away the northern lights are drawn.", 17, new AcceptableValueRange<float>(50f, 3000f)));
            _auroraOverride = Config.Bind(AuroraSection, "Custom Look", false, AuroraDescription("Use the values below instead of the built-in look.", 16));
            _auroraBrightness = Config.Bind(AuroraSection, "Brightness", 13.35f, AuroraDescription("Brightness.", 15, new AcceptableValueRange<float>(0f, 60f)));
            _auroraColorPower = Config.Bind(AuroraSection, "Band Sharpness", 7.71f,
                AuroraDescription("Higher values give thinner, sharper bands.", 14, new AcceptableValueRange<float>(0.5f, 20f)));
            _auroraCloudDensity = Config.Bind(AuroraSection, "Density", 0.3f, AuroraDescription("How thick the lights are.", 13, new AcceptableValueRange<float>(0f, 3f)));
            _auroraSpeed = Config.Bind(AuroraSection, "Drift Speed", 0.02f, AuroraDescription("How fast the lights drift.", 12, new AcceptableValueRange<float>(0f, 0.3f)));
            _auroraFlowSpeed = Config.Bind(AuroraSection, "Ripple Speed", -1.5f, AuroraDescription("How fast the lights ripple.", 11, new AcceptableValueRange<float>(-15f, 15f)));
            _auroraWaveAmount = Config.Bind(AuroraSection, "Waviness", 0.78f, AuroraDescription("How wavy the curtains are.", 10, new AcceptableValueRange<float>(0f, 3f)));
            _auroraSteps = Config.Bind(AuroraSection, "Quality Steps", 70, AuroraDescription("Drawing detail. Lower is faster.", 9, new AcceptableValueRange<int>(5, 150)));
            _auroraColor = Config.Bind(AuroraSection, "Colour 1", new Color(0.868f, 0f, 1f, 1f), AuroraDescription("First colour.", 8));
            _auroraColor2 = Config.Bind(AuroraSection, "Colour 2", new Color(0f, 0.461f, 0.846f, 1f), AuroraDescription("Second colour.", 7));
            _auroraFogColor = Config.Bind(AuroraSection, "Horizon Colour", new Color(0.087f, 0.173f, 0.257f, 1f), AuroraDescription("Sky colour near the horizon.", 6));
        }

        private static ConfigDescription AuroraDescription(string text, int order, AcceptableValueBase range = null)
        {
            return new ConfigDescription(text, range, new ConfigurationManagerOrder
            {
                Order = order,
                IsAdvanced = true
            });
        }

        private sealed class ConfigurationManagerOrder
        {
            public int? Order;
            public bool? IsAdvanced;
        }

        private void UpdateAurora(bool raidStarted, bool raidPreparing)
        {
            if ((!raidStarted && !raidPreparing) || _raid == null)
            {
                _auroraDecidedFor = null;
                _auroraThisRaid = false;
            }
            else
            {
                if (!ReferenceEquals(_auroraDecidedFor, _raid))
                {
                    DecideAurora();
                }

                DecideExtraFlakes();
            }

            bool show = _auroraThisRaid && EnsureAurora();
            if (_aurora != null && _aurora.activeSelf != show)
            {
                _aurora.SetActive(show);
            }

            if (show)
            {
                ApplyAuroraMaterial();
            }

            if (show != _auroraVisible)
            {
                _auroraVisible = show;
            }
        }

        // Called once per raid, as soon as the sky exists during the countdown.
        private void DecideAurora()
        {
            var sky = TOD_Sky.Instance;
            if (sky == null)
            {
                return;
            }

            if (CoopSupport.IsClient)
            {
                if (TryGetHostSky(out bool hostAurora, out bool hostGreen, out _))
                {
                    _auroraDecidedFor = _raid;
                    _auroraThisRaid = _auroraEnabled.Value && hostAurora;
                    _auroraGreenThisRaid = hostGreen;
                    return;
                }

                if (WaitingForHostSky || _weatherWaitStarted < 0f)
                {
                    return;
                }
            }

            _auroraDecidedFor = _raid;
            float hour = sky.Cycle.Hour;
            bool night = hour >= AuroraFromHour || hour < AuroraUntilHour;
            // A night raid has a one-in-four chance of an aurora; Force Show always has one.
            _auroraThisRaid = _auroraEnabled.Value && (_auroraForce.Value || (night && Random.value < AuroraChance));
            _auroraGreenThisRaid = _auroraColour.Value == AuroraColour.Green || (_auroraColour.Value == AuroraColour.Random && Random.value < 0.5f);
        }

        private void ApplyAuroraMaterial()
        {
            if (_auroraRenderer == null)
            {
                return;
            }

            bool green = _auroraGreenThisRaid && _auroraGreenMaterial != null;
            if (_auroraMaterial == null || green != _auroraMaterialGreen)
            {
                // Each switch starts from the chosen bundle material, so turning the override off restores the bundle's own values.
                var source = green ? _auroraGreenMaterial : _auroraPrefabMaterial;
                if (_auroraMaterial != null)
                {
                    Destroy(_auroraMaterial);
                }

                _auroraMaterial = new Material(source);
                _auroraRenderer.sharedMaterial = _auroraMaterial;
                _auroraBaseMaterial = source;
                _auroraMaterialGreen = green;
                _auroraOverrideApplied = false;
            }

            if (!_auroraOverride.Value)
            {
                if (_auroraOverrideApplied)
                {
                    _auroraMaterial.CopyPropertiesFromMaterial(_auroraBaseMaterial);
                    _auroraOverrideApplied = false;
                }

                return;
            }

            _auroraOverrideApplied = true;
            _auroraMaterial.SetFloat(ColFactorId, _auroraBrightness.Value);
            _auroraMaterial.SetFloat(ColPowId, _auroraColorPower.Value);
            _auroraMaterial.SetFloat(CloudDensityId, _auroraCloudDensity.Value);
            _auroraMaterial.SetFloat(SpeedId, _auroraSpeed.Value);
            _auroraMaterial.SetFloat(FlowSpeedId, _auroraFlowSpeed.Value);
            _auroraMaterial.SetFloat(WaveAmountId, _auroraWaveAmount.Value);
            _auroraMaterial.SetFloat(StepsId, _auroraSteps.Value);
            _auroraMaterial.SetColor(AuroraColorId, _auroraColor.Value);
            _auroraMaterial.SetColor(AuroraColor2Id, _auroraColor2.Value);
            _auroraMaterial.SetColor(AuroraFogColorId, _auroraFogColor.Value);
        }

        private bool EnsureAurora()
        {
            if (_aurora != null)
            {
                return true;
            }

            if (!_auroraLoadAttempted)
            {
                _auroraLoadAttempted = true;
                _auroraBundle = AssetBundle.LoadFromFile(ResourcePath("aurora.bundle"));
                if (_auroraBundle != null)
                {
                    _auroraPrefab = _auroraBundle.LoadAsset<GameObject>(AuroraPrefabPath);
                    _auroraGreenMaterial = _auroraBundle.LoadAsset<Material>(AuroraGreenMaterialPath);
                }

                if (_auroraPrefab == null)
                {
                    Logger.LogWarning("Could not load the aurora prefab from aurora.bundle.");
                }
            }

            if (_auroraPrefab == null)
            {
                return false;
            }

            _aurora = Instantiate(_auroraPrefab);
            _aurora.name = "WinterOutbreak Aurora";
            _aurora.SetActive(false);
            foreach (var renderer in _aurora.GetComponentsInChildren<Renderer>(true))
            {
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                if (_auroraRenderer == null)
                {
                    _auroraRenderer = renderer;
                    _auroraPrefabMaterial = renderer.sharedMaterial;
                }
            }

            Camera.onPreCull -= FollowAuroraCamera;
            Camera.onPreCull += FollowAuroraCamera;
            return true;
        }

        private static void FollowAuroraCamera(Camera camera)
        {
            var aurora = _plugin?._aurora;
            if (aurora == null || !aurora.activeSelf || camera == null || camera.cameraType != CameraType.Game)
            {
                return;
            }

            // Keep the dome centred on whichever camera draws, just inside its far clip, so distant terrain is not
            // hidden behind it.
            float radius = Mathf.Min(_plugin._auroraRadius.Value, camera.farClipPlane * AuroraFarClipShare);
            var transform = aurora.transform;
            transform.position = camera.transform.position;
            transform.localScale = new Vector3(radius, radius, radius);
        }

        private void ClearAurora()
        {
            Camera.onPreCull -= FollowAuroraCamera;
            if (_aurora != null)
            {
                _aurora.SetActive(false);
                Destroy(_aurora);
                _aurora = null;
            }

            if (_auroraMaterial != null)
            {
                Destroy(_auroraMaterial);
                _auroraMaterial = null;
            }

            _auroraRenderer = null;
            _auroraPrefabMaterial = null;
            _auroraBaseMaterial = null;
            _auroraOverrideApplied = false;
            _auroraGreenMaterial = null;
            _auroraPrefab = null;
            if (_auroraBundle != null)
            {
                _auroraBundle.Unload(true);
                _auroraBundle = null;
            }

            _auroraLoadAttempted = false;
            _auroraVisible = false;
        }
    }
}
