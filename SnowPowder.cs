using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    /// <summary>
    /// Billowing snow powder for bursts (grenades, BTR wheels): soft puffs that grow as they spread, slow down with air
    /// drag, wander with turbulence and settle slowly. Colours are multiplied by the scene's snow light
    /// (BlowingSnow.SampleSnowLight), so the powder is grey in a dim blizzard instead of glowing white.
    /// </summary>
    internal sealed class SnowPowder
    {
        private readonly Texture2D _texture;
        private readonly Material _material;
        private readonly ParticleSystem _particles;

        public bool IsAvailable => _particles != null;

        public SnowPowder(string name, int maxParticles, float gravity, float drag, float growth, float turbulence)
        {
            Shader shader = GroundSnowDrift.FindSnowParticleShader();
            if (shader == null)
            {
                return;
            }

            _texture = CreatePuffTexture();
            _material = new Material(shader) { mainTexture = _texture };
            var emitter = new GameObject(name);
            emitter.SetActive(false);
            _particles = emitter.AddComponent<ParticleSystem>();

            var main = _particles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maxParticles;
            main.gravityModifier = gravity;

            var emission = _particles.emission;
            emission.enabled = false;
            var shape = _particles.shape;
            shape.enabled = false;

            // Air drag: fast puffs lose their speed within a second or two and hang.
            var limit = _particles.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = 100f;
            limit.drag = drag;
            limit.multiplyDragByParticleSize = false;
            limit.multiplyDragByParticleVelocity = true;

            var size = _particles.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.45f), new Keyframe(0.25f, 0.45f + (growth - 0.45f) * 0.6f), new Keyframe(1f, growth)));

            var color = _particles.colorOverLifetime;
            color.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.06f), new GradientAlphaKey(0.75f, 0.45f),
                    new GradientAlphaKey(0f, 1f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            var rotation = _particles.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-0.6f, 0.6f);

            if (turbulence > 0f)
            {
                var noise = _particles.noise;
                noise.enabled = true;
                noise.strength = turbulence;
                noise.frequency = 0.35f;
                noise.scrollSpeed = 0.25f;
                noise.quality = ParticleSystemNoiseQuality.Medium;
            }

            var renderer = emitter.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortMode = ParticleSystemSortMode.Distance;
            emitter.SetActive(true);
            _particles.Play();
        }

        public void Emit(Vector3 position, Vector3 velocity, float size, float lifetime, Color color, float rotation)
        {
            if (_particles == null)
            {
                return;
            }

            var particle = new ParticleSystem.EmitParams
            {
                position = position,
                velocity = velocity,
                startSize = size,
                startLifetime = lifetime,
                startColor = color,
                rotation = rotation
            };
            _particles.Emit(particle, 1);
        }

        /// <summary>
        /// Snow light as a particle tint: kept a little above the sampled level so powder stays readable.
        /// </summary>
        public static Color Tint(float brightness, float alpha)
        {
            Color light = BlowingSnowLight();
            return new Color(light.r * brightness, light.g * brightness, light.b * brightness * 1.02f, alpha);
        }

        private static Color BlowingSnowLight()
        {
            // SampleSnowLight is a luminance-led grey from ambient plus some sun. Powder is unlit, so it reads brighter
            // than the lit ground snow beside it: take it greyer and a little darker.
            Color light = WinterOutbreakPlugin.SampleSnowLight();
            float luminance = light.r * 0.3f + light.g * 0.59f + light.b * 0.11f;
            return Color.Lerp(light, new Color(luminance, luminance, luminance), 0.4f) * 0.86f;
        }

        internal static Texture2D CreatePuffTexture()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "Winter Outbreak Snow Powder",
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = (x + 0.5f) / size * 2f - 1f;
                    float py = (y + 0.5f) / size * 2f - 1f;
                    float angle = Mathf.Atan2(py, px);
                    // A lumpy edge and some inner density variation, so overlapping puffs read as cloud, not discs.
                    float edge = 0.82f + 0.07f * Mathf.Sin(3f * angle + 0.6f) + 0.05f * Mathf.Sin(5f * angle + 2.1f);
                    float radius = Mathf.Sqrt(px * px + py * py) / edge;
                    float body = Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(0.15f, 1f, radius));
                    float grain = 0.85f + 0.15f * Mathf.PerlinNoise(x * 0.21f + 3.3f, y * 0.21f + 7.1f);
                    byte alpha = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(body * grain));
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        public void Dispose()
        {
            if (_particles != null)
            {
                Object.Destroy(_particles.gameObject);
            }

            Object.Destroy(_material);
            Object.Destroy(_texture);
        }
    }
}
