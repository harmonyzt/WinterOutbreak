using BepInEx.Configuration;
using EFT;
using EFT.Vehicle;
using EFT.Vehicle.Vehicles;
using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    /// <summary>
    /// BTR in snow
    /// </summary>
    public sealed partial class WinterOutbreakPlugin
    {
        private const float TrackHalfWidth = 0.25f;
        private const float TrackMaxDepth = 0.17f;
        private const float TrackSegment = 0.9f;
        private const float TrackMaxRange = 200f;
        private const float SprayRange = 60f;

        private ConfigEntry<bool> _btrTracks;
        private ConfigEntry<float> _btrSpray;
        private ConfigEntry<float> _btrTrackRefillSeconds;
        private readonly Vector3[] _btrLastWheel = new Vector3[4];
        private readonly bool[] _btrHasWheel = new bool[4];
        private readonly VehicleWheelsBase[] _btrWheels = new VehicleWheelsBase[4];
        private BTRView _btrView;
        private Vector3 _btrLastPosition;
        private float _btrSpeed;
        private float _btrSprayCarry;
        private BtrSnowSpray _btrSnowSpray;

        private void BindBtrSnowSettings()
        {
            const string oldSection = "BTR Snow";

            _btrTracks = BindSetting(SnowSection, "BTR Tracks", true,
                "The BTR leaves tyre tracks in the snow.", 5,
                oldSection: oldSection, oldKey: "Tracks");
            _btrSpray = BindSetting(SnowSection, "BTR Spray", 1f,
                "How much snow the BTR's wheels throw up. 0 turns it off.", 20,
                true, new AcceptableValueRange<float>(0f, 2f), oldSection, "Spray");
            _btrTrackRefillSeconds = BindSetting(SnowSection, "BTR Track Refill Seconds", 240f,
                "How long a BTR track takes to fill back in.", 21,
                true, new AcceptableValueRange<float>(30f, 900f), oldSection, "Track Refill Seconds");
        }

        private void UpdateBtrSnow(Player player)
        {
            var view = _voxelSnow != null && player != null ? BtrController.Instance?.BtrView : null;
            if (view == null)
            {
                ResetBtrWheels();
                _btrView = null;
                return;
            }

            if (!ReferenceEquals(view, _btrView))
            {
                _btrView = view;
                // Front and rear wheel on each side
                _btrWheels[0] = view.leftWheel1;
                _btrWheels[1] = view.leftWheel4;
                _btrWheels[2] = view.rightWheel1;
                _btrWheels[3] = view.rightWheel4;
                ResetBtrWheels();
                _btrLastPosition = view.transform.position;
                _btrSpeed = 0f;
            }

            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            Vector3 body = view.transform.position;
            float measured = Vector3.Distance(body, _btrLastPosition) / dt;
            _btrLastPosition = body;
            // A jump (respawn or sync correction) is not driving
            _btrSpeed = measured > 30f ? 0f : Mathf.Lerp(_btrSpeed, measured, Mathf.Clamp01(dt * 4f));

            float distance = Vector3.Distance(player.Position, body);
            if (_btrTracks.Value && distance <= TrackMaxRange)
            {
                PressBtrTracks();
            }
            else
            {
                ResetBtrWheels();
            }

            if (_btrSpray.Value > 0f && distance <= SprayRange && _btrSpeed > 1.2f)
            {
                SprayBtrSnow(view, dt, distance);
            }
        }

        private void PressBtrTracks()
        {
            float refill = _btrTrackRefillSeconds.Value;
            for (int i = 0; i < _btrWheels.Length; i++)
            {
                var wheel = _btrWheels[i];
                if (wheel == null)
                {
                    _btrHasWheel[i] = false;
                    continue;
                }

                Vector3 position = wheel.transform.position;
                if (!_btrHasWheel[i])
                {
                    _btrLastWheel[i] = position;
                    _btrHasWheel[i] = true;
                    continue;
                }

                Vector3 travel = position - _btrLastWheel[i];
                travel.y = 0f;
                float length = travel.magnitude;
                if (length > 6f)
                {
                    _btrLastWheel[i] = position;
                    continue;
                }

                if (length < TrackSegment)
                {
                    continue;
                }

                // Overlap each segment slightly with the last so the groove has no seams
                Vector3 start = _btrLastWheel[i] - travel / length * 0.1f;
                _voxelSnow.StampTrack(start, position, TrackHalfWidth, TrackMaxDepth, refill);
                _btrLastWheel[i] = position;
            }
        }

        private void SprayBtrSnow(BTRView view, float dt, float distance)
        {
            _btrSnowSpray ??= new BtrSnowSpray();
            if (!_btrSnowSpray.IsAvailable)
            {
                return;
            }

            // Particles per wheel per second grow with speed
            float rate = Mathf.Clamp(_btrSpeed * 22f, 0f, 220f) * _btrSpray.Value
                * Mathf.Lerp(1f, 0.35f, Mathf.InverseLerp(20f, SprayRange, distance));
            _btrSprayCarry += rate * dt;
            int count = Mathf.FloorToInt(_btrSprayCarry);
            if (count <= 0)
            {
                return;
            }

            _btrSprayCarry -= count;
            Vector3 forward = view.transform.forward;
            Vector3 right = view.transform.right;
            for (int i = 0; i < _btrWheels.Length; i++)
            {
                var wheel = _btrWheels[i];
                if (wheel == null)
                {
                    continue;
                }

                Vector3 contact = wheel.transform.position - Vector3.up * 0.5f;
                if (!_voxelSnow.TryGetDepth(contact, out float depth) || depth < 0.03f)
                {
                    continue;
                }

                float side = i < 2 ? -1f : 1f;
                _btrSnowSpray.Emit(contact, forward, right * side, _btrSpeed, Mathf.Clamp01(depth / 0.15f), count);
            }
        }

        private void ResetBtrWheels()
        {
            for (int i = 0; i < _btrHasWheel.Length; i++)
            {
                _btrHasWheel[i] = false;
            }
        }

        private void ClearBtrSnow()
        {
            ResetBtrWheels();
            _btrView = null;
            for (int i = 0; i < _btrWheels.Length; i++)
            {
                _btrWheels[i] = null;
            }

            _btrSnowSpray?.Dispose();
            _btrSnowSpray = null;
            // Raid-end cleanup for the other burst effects that share this hook
            ClearBulletSnow();
            ClearSnowCoats();
            _btrSprayCarry = 0f;
        }
    }

    /// <summary>
    /// Snow thrown up behind and to the side of the BTR's wheels: grains on ballistic arcs and billowing powder
    /// (SnowPowder) that hangs in the air behind it.
    /// </summary>
    internal sealed class BtrSnowSpray
    {
        private readonly System.Random _random = new System.Random();
        private readonly Material _material;
        private readonly Texture2D _texture;
        private readonly ParticleSystem _particles;
        private readonly SnowPowder _powder = new SnowPowder("Winter Outbreak BTR Snow Powder", 5000, 0.06f, 1.4f, 2.6f, 0.5f);
        private Color _light = Color.white;
        private float _nextLight;

        public bool IsAvailable => _particles != null;

        public BtrSnowSpray()
        {
            Shader shader = GroundSnowDrift.FindSnowParticleShader();
            if (shader == null)
            {
                return;
            }

            _texture = CreatePuffTexture();
            _material = new Material(shader) { mainTexture = _texture };
            var emitter = new GameObject("Winter Outbreak BTR Snow Spray");
            emitter.SetActive(false);
            _particles = emitter.AddComponent<ParticleSystem>();
            var main = _particles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 5000;
            main.gravityModifier = 0.75f;

            var emission = _particles.emission;
            emission.enabled = false;
            var shape = _particles.shape;
            shape.enabled = false;

            var color = _particles.colorOverLifetime;
            color.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.65f, 0.6f), new GradientAlphaKey(0f, 1f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            var size = _particles.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.8f, 1f, 1.6f));

            var renderer = emitter.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.sortMode = ParticleSystemSortMode.Distance;
            emitter.SetActive(true);
            _particles.Play();
        }

        public void Emit(Vector3 contact, Vector3 forward, Vector3 outward, float speed, float snow, int count)
        {
            if (Time.time >= _nextLight)
            {
                _nextLight = Time.time + 0.5f;
                _light = SnowPowder.Tint(1f, 1f);
            }

            float lift = Mathf.Lerp(0.7f, 1.25f, snow);
            // Bursty, not a steady stream
            float burst = Mathf.PerlinNoise(Time.time * 2.3f, contact.x * 0.37f + contact.z * 0.21f);
            count = Mathf.RoundToInt(count * Mathf.Lerp(0.25f, 1.6f, burst));
            Color dirt = new Color(_light.r * 0.60f, _light.g * 0.50f, _light.b * 0.42f, 1f);
            for (int i = 0; i < count; i++)
            {
                // Thrown back off the tyre (a rooster tail at speed) and out to the side
                Vector3 velocity = -forward * (speed * Range(0.25f, 0.7f))
                    + outward * Range(0.6f, 3.0f) + Vector3.up * Range(1.4f, 4.6f) * lift
                    + Random3() * 0.8f;
                Vector3 position = contact + forward * Range(-0.6f, 0.2f) + outward * Range(0f, 0.3f)
                    + Vector3.up * Range(0.02f, 0.2f);
                // Road snow is churned with dirt and slush: some powder is tinted and some grains are dark
                if (_random.NextDouble() < 0.45)
                {
                    float shade = Range(0.78f, 1.0f);
                    // Mottled
                    float share = _random.NextDouble() < 0.4 ? Range(0.25f, 0.7f) : Range(0f, 0.2f);
                    Color puff = Color.Lerp(_light, dirt, share) * shade;
                    float hue = Range(-0.03f, 0.03f);
                    puff.r += hue;
                    puff.b -= hue;
                    puff.a = Range(0.10f, 0.22f);
                    _powder.Emit(position, velocity * Range(0.4f, 0.7f), Range(0.35f, 1.3f), Range(1.2f, 3.0f),
                        puff, Range(0f, 360f));
                    continue;
                }

                float brightness = Range(0.75f, 1.0f);
                Color grain = _random.NextDouble() < 0.32 ? dirt * Range(0.7f, 1.1f)
                    : Color.Lerp(_light, dirt, Range(0f, 0.25f)) * brightness;
                grain.a = Range(0.6f, 0.85f);
                var particle = new ParticleSystem.EmitParams
                {
                    position = position,
                    velocity = velocity * Range(0.8f, 1.15f),
                    startLifetime = Range(0.5f, 1.4f),
                    startSize = Range(0.025f, 0.11f),
                    startColor = grain,
                    rotation = Range(0f, 360f)
                };
                _particles.Emit(particle, 1);
            }
        }

        private Vector3 Random3()
        {
            return new Vector3(Range(-1f, 1f), Range(-1f, 1f), Range(-1f, 1f));
        }

        private static Texture2D CreatePuffTexture()
        {
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "Winter Outbreak BTR Snow Puff",
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = (x + 0.5f) / size * 2f - 1f;
                    float py = (y + 0.5f) / size * 2f - 1f;
                    float radius = Mathf.Sqrt(px * px + py * py);
                    byte alpha = (byte)Mathf.RoundToInt(255f * Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(0.35f, 1f, radius)));
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        private float Range(float min, float max)
        {
            return Mathf.Lerp(min, max, (float)_random.NextDouble());
        }

        public void Dispose()
        {
            if (_particles != null)
            {
                Object.Destroy(_particles.gameObject);
            }

            Object.Destroy(_material);
            Object.Destroy(_texture);
            _powder.Dispose();
        }
    }
}
