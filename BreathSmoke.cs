using BepInEx.Configuration;
using EFT;
using UnityEngine;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private const string BreathPrefabPath =
            "assets/raivos prefabs/updated basic breathsmoke by raivo/updated basic breath smoke by raivo.prefab";

        private Fixed<float> _breathAlpha;
        private Fixed<float> _firstPersonBreathX;
        private Fixed<float> _firstPersonBreathY;
        private Fixed<float> _firstPersonBreathZ;

        private AssetBundle _breathBundle;
        private GameObject _breathPrefab;
        private GameObject _breathSmoke;
        private bool _breathLoadAttempted;
        private bool _breathFirstPerson;
        private ParticleSystem _breathParticles;
        private ParticleSystem[] _breathSystems;
        private Color _breathColor;
        private float _appliedBreathAlpha;

        private void BindBreathSettings()
        {
            _breathAlpha = new Fixed<float>(20f);
            _firstPersonBreathX = new Fixed<float>(0f);
            _firstPersonBreathY = new Fixed<float>(-0.1f);
            _firstPersonBreathZ = new Fixed<float>(0.1f);
        }

        private void UpdateBreathSmoke(Player player)
        {
            float alpha = _breathAlpha.Value / 100f;
            bool outdoors = player != null && player.IsYourPlayer && player.HealthController?.IsAlive == true
                && player.Environment == EnvironmentType.Outdoor;

            bool firstPerson = outdoors && player.FirstPersonPointOfView;
            Transform anchor = null;
            if (outdoors && alpha > 0f)
            {
                if (firstPerson)
                {
                    var camera = Camera.main;
                    anchor = camera != null ? camera.transform : null;
                }
                else
                {
                    anchor = player.PlayerBones?.Head?.Original;
                }
            }

            if (_breathSmoke != null && (anchor == null || _breathSmoke.transform.parent != anchor
                || _breathFirstPerson != firstPerson))
            {
                DestroyBreathInstance();
            }

            if (anchor == null)
            {
                return;
            }

            if (!_breathLoadAttempted)
            {
                _breathLoadAttempted = true;
                _breathBundle = AssetBundle.LoadFromFile(ResourcePath("breathsmoke.bundle"));
                if (_breathBundle != null)
                {
                    _breathPrefab = _breathBundle.LoadAsset<GameObject>(BreathPrefabPath);
                }

                if (_breathPrefab == null)
                {
                    Logger.LogWarning("Could not load the breath smoke prefab from breathsmoke.bundle.");
                }
            }

            if (_breathPrefab == null)
            {
                return;
            }

            if (_breathSmoke == null)
            {
                _breathFirstPerson = firstPerson;
                if (firstPerson)
                {
                    _breathSmoke = Instantiate(_breathPrefab, anchor, false);
                    _breathSmoke.transform.localRotation = Quaternion.identity;
                }
                else
                {
                    Vector3 forward = player.Transform.forward;
                    Vector3 mouth = anchor.position + forward * 0.10f - Vector3.up * 0.08f;
                    _breathSmoke = Instantiate(_breathPrefab, mouth, Quaternion.LookRotation(forward), anchor);
                }

                _breathParticles = _breathSmoke.GetComponentInChildren<ParticleSystem>(true);
                _breathSystems = _breathSmoke.GetComponentsInChildren<ParticleSystem>(true);
                foreach (var system in _breathSystems)
                {
                    // Carry released breath along with the player's movement instead of leaving it behind.
                    var main = system.main;
                    main.emitterVelocityMode = ParticleSystemEmitterVelocityMode.Custom;
                    var inherit = system.inheritVelocity;
                    inherit.enabled = true;
                    inherit.mode = ParticleSystemInheritVelocityMode.Current;
                    inherit.curveMultiplier = 1f;
                }
                if (_breathParticles != null)
                {
                    _breathColor = _breathParticles.main.startColor.color;
                }

                _appliedBreathAlpha = -1f;
            }

            if (firstPerson)
            {
                _breathSmoke.transform.localPosition = new Vector3(
                    _firstPersonBreathX.Value, _firstPersonBreathY.Value, _firstPersonBreathZ.Value);
            }

            Vector3 walking = player.Velocity;
            walking.y = 0f;
            if (_breathSystems != null)
            {
                foreach (var system in _breathSystems)
                {
                    if (system != null)
                    {
                        var main = system.main;
                        main.emitterVelocity = walking;
                    }
                }
            }

            if (_breathParticles != null && alpha != _appliedBreathAlpha)
            {
                var main = _breathParticles.main;
                Color color = _breathColor;
                color.a *= alpha;
                main.startColor = color;
                _appliedBreathAlpha = alpha;
            }
        }

        private void DestroyBreathInstance()
        {
            if (_breathSmoke != null)
            {
                _breathSmoke.SetActive(false);
                Destroy(_breathSmoke);
                _breathSmoke = null;
            }

            _breathParticles = null;
            _breathSystems = null;
        }

        private void ClearBreathSmoke()
        {
            DestroyBreathInstance();
            _breathPrefab = null;
            if (_breathBundle != null)
            {
                _breathBundle.Unload(true);
                _breathBundle = null;
            }

            _breathLoadAttempted = false;
        }
    }
}
