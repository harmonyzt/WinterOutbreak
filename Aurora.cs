using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private const string AuroraPrefabPath = "assets/aurora/atmosphere.prefab";
        private const string AuroraGreenMaterialPath = "assets/aurora/aurora2.mat";

        private static readonly int StepsId = Shader.PropertyToID("_Steps");

        private ConfigEntry<bool> _auroraEnabled;
        private ConfigEntry<int> _auroraSteps;

        // The bundle's dome is a unit sphere scaled to 1000 m, drawn from the inside by an opaque shader that writes
        // depth
        private const float AuroraMaxRadius = 1000f;
        private const float AuroraFarClipShare = 0.95f;

        private AssetBundle _auroraBundle;
        private GameObject _auroraPrefab;
        private Material _auroraGreenMaterial;
        private Renderer _auroraRenderer;
        private Material _auroraMaterial;
        private bool _auroraMaterialGreen;
        private int _auroraStepsApplied;
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

        private void BindAuroraSettings()
        {
            _auroraEnabled = BindSetting(AuroraSection, "Enabled", true,
                "A winter raid that starts between 22:00 and 04:00 has a 25 % chance of northern lights.", 1,
                advanced: true, oldSection: "Aurora");
            _auroraSteps = BindSetting(AuroraSection, "Quality Steps", 70,
                "Drawing detail of the northern lights. Lower is faster.", 2,
                true, new AcceptableValueRange<int>(5, 150), "Aurora");
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

        // Called once per raid, as soon as the sky exists during the countdown
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
            
            // A night raid has a one-in-four chance of an aurora, violet or green
            _auroraThisRaid = _auroraEnabled.Value && night && Random.value < AuroraChance;
            _auroraGreenThisRaid = Random.value < 0.5f;
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
                // Each switch starts from a fresh copy of the bundle material
                var source = green ? _auroraGreenMaterial : _auroraPrefabMaterial;
                if (_auroraMaterial != null)
                {
                    Destroy(_auroraMaterial);
                }

                _auroraMaterial = new Material(source);
                _auroraRenderer.sharedMaterial = _auroraMaterial;
                _auroraMaterialGreen = green;
                _auroraStepsApplied = -1;
            }

            if (_auroraStepsApplied != _auroraSteps.Value)
            {
                _auroraStepsApplied = _auroraSteps.Value;
                _auroraMaterial.SetFloat(StepsId, _auroraStepsApplied);
            }
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
            // hidden behind it
            float radius = Mathf.Min(AuroraMaxRadius, camera.farClipPlane * AuroraFarClipShare);
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
