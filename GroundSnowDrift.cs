using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    /// <summary>
    /// Loose snow streaming along snow-covered ground
    /// </summary>
    internal sealed class GroundSnowDrift
    {
        private const int MaxParticles = 900;
        private readonly int _groundMask = LayersMaskController.HighPolyWithTerrainNoGrassMask;
        private readonly int _terrainMask = LayersMaskController.TerrainMask;
        private readonly ParticleSystem _particles;
        private readonly Material _material;
        private readonly Texture2D _texture;
        private readonly ParticleSteering _steering;

        public bool IsAvailable => _particles != null;

        public GroundSnowDrift()
        {
            Shader shader = FindSnowParticleShader();
            if (shader == null)
            {
                return;
            }

            _steering = new ParticleSteering(_groundMask);
            _texture = CreateWispTexture();
            _material = new Material(shader) { mainTexture = _texture };
            EnableSoftParticles(_material, 3f);

            var emitter = new GameObject("Winter Outbreak Ground Drift");
            emitter.SetActive(false);
            _particles = emitter.AddComponent<ParticleSystem>();

            var main = _particles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = MaxParticles;
            main.gravityModifier = 0f;

            var emission = _particles.emission;
            emission.enabled = false;
            var shape = _particles.shape;
            shape.enabled = false;

            // Irregular sideways wander keeps streaks from moving in perfect parallel lines
            var noise = _particles.noise;
            noise.enabled = true;
            noise.separateAxes = true;
            noise.strengthX = 0.9f;
            noise.strengthY = 0.12f;
            noise.strengthZ = 0.9f;
            noise.frequency = 0.45f;
            noise.scrollSpeed = 0.7f;
            noise.octaveCount = 2;
            noise.quality = ParticleSystemNoiseQuality.Medium;

            var size = _particles.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.7f, 1f, 1.4f));

            var color = _particles.colorOverLifetime;
            color.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f),
                    new GradientAlphaKey(0.7f, 0.65f), new GradientAlphaKey(0f, 1f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            var renderer = emitter.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = _material;
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 1.8f;
            renderer.velocityScale = 0.1f;
            renderer.cameraVelocityScale = 0f;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortMode = ParticleSystemSortMode.None;
            renderer.maxParticleSize = 0.9f;

            emitter.SetActive(true);
            _particles.Play();
        }

        /// <summary>
        /// Per-frame upkeep: turbulence and wall steering.
        /// </summary>
        public void Update(StormWind wind)
        {
            if (_particles == null)
            {
                return;
            }

            // Steer every other frame, twice as many particles, to halve the particle copies
            if ((Time.frameCount & 1) == 0)
            {
                _steering.Steer(_particles, 140, 0.4f, 0.08f);
            }

            var noise = _particles.noise;
            noise.strengthMultiplier = 0.4f + wind.Turbulence * 1.1f;
        }

        /// <summary>
        /// One fine streak skimming the ground somewhere inside the source's cell.
        /// </summary>
        public void EmitStreak(SnowSource source, StormWind wind, float gust, float intensity, Color light)
        {
            if (_particles == null)
            {
                return;
            }

            Vector3 forward = source.Flow;
            Vector3 side = Vector3.Cross(Vector3.up, forward);
            Vector3 velocity = Velocity(source, wind, gust);
            float density = Density(wind, intensity);
            Vector3 offset = forward * wind.Range(-3f, 3f) + side * wind.Range(-3f, 3f);
            Emit(source.OnGround(offset) + Vector3.up * wind.Range(0.03f, 0.15f + gust * 0.35f),
                velocity * wind.Range(0.85f, 1.12f), wind.Range(1.1f, 2.6f),
                wind.Range(0.2f, 0.5f) * (1f + gust * 0.7f), wind.Range(0.05f, 0.14f) * density,
                Tint(light));
        }

        /// <summary>
        /// One translucent sheet of moving snow.
        /// </summary>
        public void EmitSheet(SnowSource source, StormWind wind, float gust, float intensity, Color light,
            float spread = 1f)
        {
            if (_particles == null)
            {
                return;
            }

            Vector3 forward = source.Flow;
            Vector3 side = Vector3.Cross(Vector3.up, forward);
            // Far sheets are larger, live longer and are a little denser so they read at a distance
            float scale = spread > 1f ? Mathf.Sqrt(spread) : 1f;
            float size = Mathf.Exp(wind.Range(0f, 2.1f)) * (1f + gust * 0.5f) * scale;
            Vector3 offset = (forward * wind.Range(-2.5f, 2.5f) + side * wind.Range(-2.5f, 2.5f)) * spread;
            Emit(source.OnGround(offset) + Vector3.up * (size * 0.12f + wind.Range(0.05f, 0.25f + gust * 0.5f)),
                Velocity(source, wind, gust) * wind.Range(0.8f, 1f), wind.Range(1.6f, 3.6f) * scale, size,
                wind.Range(0.06f, 0.14f) * scale * Density(wind, intensity) / Mathf.Sqrt(Mathf.Max(1f, size * 0.6f)),
                Tint(light));
        }

        private static Vector3 Velocity(SnowSource source, StormWind wind, float gust)
        {
            Vector3 side = Vector3.Cross(Vector3.up, source.Flow);
            float speed = wind.SnowSpeed * source.FlowSpeed * wind.Range(0.8f, 1.15f);
            return source.Flow * speed + side * wind.Range(-0.6f, 0.6f) * wind.Turbulence
                + Vector3.up * wind.Range(0f, 0.18f) * (1f + gust * 3f);
        }

        private static float Density(StormWind wind, float intensity)
        {
            return Mathf.Clamp01(0.5f + wind.Strength * 0.6f) * Mathf.Clamp(intensity, 0.3f, 2f);
        }

        private static Color Tint(Color light)
        {
            return light * new Color(0.95f, 0.95f, 0.96f);
        }

        private void Emit(Vector3 position, Vector3 velocity, float lifetime, float size, float alpha, Color tint)
        {
            var particle = new ParticleSystem.EmitParams
            {
                position = position,
                velocity = velocity,
                startLifetime = lifetime,
                startSize = size,
                startColor = new Color(tint.r, tint.g, tint.b, Mathf.Clamp01(alpha))
            };
            _particles.Emit(particle, 1);
        }

        /// <summary>
        /// Fades a particle where it meets geometry (the legacy particle shader's soft-particle variant reads the
        /// camera depth texture), so large sheets do not cut hard lines into the ground or walls.
        /// </summary>
        internal static void EnableSoftParticles(Material material, float invFade)
        {
            if (material.HasProperty("_InvFade"))
            {
                material.SetFloat("_InvFade", invFade);
                material.EnableKeyword("SOFTPARTICLES_ON");
            }
        }

        internal static Shader FindSnowParticleShader()
        {
            Shader shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (shader == null || !shader.isSupported)
            {
                shader = Shader.Find("Sprites/Default");
            }

            return shader != null && shader.isSupported ? shader : null;
        }

        internal static Texture2D CreateWispTexture()
        {
            const int size = 64;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f;
                    float v = (y + 0.5f) / size * 2f - 1f;
                    float falloff = Mathf.Clamp01(1f - u * u - v * v);
                    float grain = Mathf.Lerp(0.35f, 1f, Mathf.PerlinNoise(x * 0.18f, y * 0.13f));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(255f * falloff * falloff * grain));
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Winter Outbreak Wisp",
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
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
