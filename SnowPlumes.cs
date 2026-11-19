using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    /// <summary>
    /// Snow lifted off the ground like smoke: billowing plumes that favour windward slopes, ridge crests and obstacles,
    /// small rotating vortices, and gust-front clouds that roll through the player's position during large gusts.
    /// </summary>
    internal sealed class SnowPlumes
    {
        private const int MaxPuffs = 420;
        private const int MaxVortex = 48;
        private readonly int _groundMask = LayersMaskController.HighPolyWithTerrainNoGrassMask;
        private readonly int _terrainMask = LayersMaskController.TerrainMask;
        private readonly ParticleSystem _puffs;
        private readonly ParticleSystem _vortex;
        private readonly Material _material;
        private readonly Texture2D _texture;
        private readonly ParticleSteering _puffSteering;
        private Vector3 _vortexCenter;
        private int _vortexRemaining;
        private float _vortexInterval;
        private float _vortexNext;
        private Color _vortexLight = Color.white;

        public bool IsAvailable => _puffs != null;

        public SnowPlumes()
        {
            Shader shader = GroundSnowDrift.FindSnowParticleShader();
            if (shader == null)
            {
                return;
            }

            _puffSteering = new ParticleSteering(_groundMask);
            _texture = SnowTextures.CreatePuffTexture();
            _material = new Material(shader) { mainTexture = _texture };
            GroundSnowDrift.EnableSoftParticles(_material, 0.45f);
            _puffs = CreateSystem("Winter Outbreak Snow Plumes", MaxPuffs, false);
            _vortex = CreateSystem("Winter Outbreak Snow Vortex", MaxVortex, true);
        }

        private ParticleSystem CreateSystem(string name, int maxParticles, bool orbit)
        {
            var emitter = new GameObject(name);
            emitter.SetActive(false);
            var particles = emitter.AddComponent<ParticleSystem>();

            var main = particles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maxParticles;
            main.gravityModifier = 0f;

            var emission = particles.emission;
            emission.enabled = false;
            var shape = particles.shape;
            shape.enabled = false;

            // Rise quickly, then lose lift and settle as the snow disperses
            var velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            var flat = AnimationCurve.Constant(0f, 1f, 0f);
            var rise = new AnimationCurve(new Keyframe(0f, 0.9f), new Keyframe(0.4f, 0.25f), new Keyframe(1f, -0.25f));
            velocity.x = new ParticleSystem.MinMaxCurve(1f, flat);
            velocity.y = new ParticleSystem.MinMaxCurve(orbit ? 1.6f : 1f, rise);
            velocity.z = new ParticleSystem.MinMaxCurve(1f, flat);
            if (orbit)
            {
                // Spin up, hold, then unwind and drift outward, so pieces never orbit in place forever
                var spin = new AnimationCurve(new Keyframe(0f, 0.2f), new Keyframe(0.3f, 1f),
                    new Keyframe(0.7f, 0.75f), new Keyframe(1f, 0.05f));
                var spread = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.6f, 0.2f), new Keyframe(1f, 1f));
                velocity.orbitalX = new ParticleSystem.MinMaxCurve(1f, flat);
                velocity.orbitalY = new ParticleSystem.MinMaxCurve(2.6f, spin);
                velocity.orbitalZ = new ParticleSystem.MinMaxCurve(1f, flat);
                velocity.radial = new ParticleSystem.MinMaxCurve(1.2f, spread);
            }

            var noise = particles.noise;
            noise.enabled = true;
            noise.strength = orbit ? 0.8f : 1.3f;
            noise.frequency = 0.22f;
            noise.scrollSpeed = 0.35f;
            noise.octaveCount = 2;
            noise.quality = ParticleSystemNoiseQuality.Medium;

            var rotation = particles.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-0.5f, 0.5f);

            var size = particles.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.35f), new Keyframe(0.35f, 0.9f), new Keyframe(1f, 1.35f)));

            var color = particles.colorOverLifetime;
            color.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.18f),
                    new GradientAlphaKey(0.75f, 0.55f), new GradientAlphaKey(0f, 1f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            // Low-quality world collision stops puffs at walls and slopes so they spread along the surface and fade
            // instead of passing through
            var collision = particles.collision;
            collision.enabled = !orbit;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.quality = ParticleSystemCollisionQuality.Low;
            collision.collidesWith = _groundMask;
            collision.enableDynamicColliders = false;
            collision.dampen = 0.85f;
            collision.bounce = 0f;
            collision.lifetimeLoss = 0.2f;
            collision.radiusScale = 0.12f;

            var renderer = emitter.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = _material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortMode = ParticleSystemSortMode.Distance;
            renderer.maxParticleSize = 1.2f;

            emitter.SetActive(true);
            particles.Play();
            return particles;
        }

        /// <summary>
        /// Per-frame upkeep: turbulence, wall steering and a vortex that is spinning up.
        /// </summary>
        public void Update(StormWind wind, float deltaTime)
        {
            if (_puffs == null)
            {
                return;
            }

            // Steer every third frame, three times as many particles, to cut particle copies
            if (Time.frameCount % 3 == 0)
            {
                _puffSteering.Steer(_puffs, 120, 0.7f, 0.35f);
            }

            var noise = _puffs.noise;
            noise.strengthMultiplier = 0.7f + wind.Turbulence * 1.4f;
            FeedVortex(wind);
        }

        /// <summary>
        /// One puff of lifted snow from a source.
        /// </summary>
        public void EmitPuff(SnowSource source, StormWind wind, float gust, float intensity, Color light,
            float spread = 1f)
        {
            if (_puffs == null)
            {
                return;
            }

            float s = Mathf.Clamp01(wind.Strength);
            Vector3 dir = source.Flow;
            float lift = 0.5f + source.Crest * 1.4f + source.Windward * 0.6f;
            bool large = wind.Chance(0.08f + s * 0.15f + gust * 0.25f);
            float rise = wind.Range(0f, 1f);
            float scale = spread > 1f ? Mathf.Sqrt(spread) : 1f;
            Vector3 offset = (dir * wind.Range(-2.5f, 2.5f) + wind.Side * wind.Range(-3f, 3f)) * spread;
            Vector3 position = source.OnGround(offset) + Vector3.up * (0.2f + rise * wind.Range(0.4f, 1.6f) * lift);
            Vector3 velocity = dir * wind.SnowSpeed * source.FlowSpeed * wind.Range(0.45f, 0.95f)
                + Vector3.up * wind.Range(0.3f, 1.4f) * lift
                + wind.Side * wind.Range(-0.7f, 0.7f) * wind.Turbulence;

            // Long-tailed sizes: mostly 1.5-5 m, with large billows up to about 12 m
            float size = Mathf.Exp(wind.Range(0.4f, large ? 2.5f : 1.6f)) * (1f + rise * 0.6f) * scale;
            float alpha = wind.Range(0.06f, 0.17f) * (0.65f + s * 0.6f) / Mathf.Sqrt(Mathf.Max(1f, size * 0.35f));
            Emit(_puffs, position + Vector3.up * size * 0.15f, velocity, wind.Range(3f, 6.5f) * (large ? 1.3f : 1f) * scale,
                size, alpha * scale * Mathf.Clamp(intensity, 0.4f, 1.5f), wind, light);
        }

        /// <summary>
        /// One puff rolling up the upwind face of the wall or rock in front of a source.
        /// </summary>
        public void EmitEddyPuff(SnowSource source, StormWind wind, float intensity, Color light)
        {
            if (_puffs == null || !source.Obstacle)
            {
                return;
            }

            Vector3 face = Vector3.ProjectOnPlane(source.ObstacleNormal, Vector3.up).normalized;
            Vector3 along = Vector3.Cross(Vector3.up, face);
            Vector3 velocity = Vector3.up * wind.Range(1.2f, 2.8f) + face * wind.Range(0.3f, 1.2f)
                + along * wind.Range(-1.2f, 1.2f);
            Emit(_puffs, source.ObstaclePoint + Vector3.up * wind.Range(0.1f, 1.2f) + along * wind.Range(-2f, 2f),
                velocity, wind.Range(1.8f, 3.4f), wind.Range(0.9f, 2.2f),
                wind.Range(0.08f, 0.18f) * Mathf.Clamp(intensity, 0.5f, 1.5f), wind,
                light);
        }

        /// <summary>
        /// Starts a small snow devil.
        /// </summary>
        public void StartVortex(Vector3 ground, StormWind wind, float intensity, Color light)
        {
            if (_vortex == null || _vortex.particleCount > 0 || _vortexRemaining > 0)
            {
                return;
            }

            _vortex.transform.position = ground;
            var velocity = _vortex.velocityOverLifetime;
            velocity.orbitalYMultiplier = wind.Chance(0.5f) ? 2.6f : -2.6f;
            _vortexCenter = ground;
            _vortexRemaining = Mathf.RoundToInt(wind.Range(12f, 20f) * Mathf.Clamp(intensity, 0.5f, 1.3f));
            _vortexInterval = 2.5f / Mathf.Max(1, _vortexRemaining);
            _vortexNext = Time.time;
            _vortexLight = light;
        }

        private void FeedVortex(StormWind wind)
        {
            if (_vortexRemaining <= 0 || Time.time < _vortexNext)
            {
                return;
            }

            _vortexNext = Time.time + _vortexInterval;
            _vortexRemaining--;
            float angle = wind.Range(0f, Mathf.PI * 2f);
            float radius = wind.Range(0.4f, 1.4f);
            var offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            Emit(_vortex, _vortexCenter + offset + Vector3.up * wind.Range(0.1f, 0.6f),
                wind.Direction * wind.SnowSpeed * 0.2f, wind.Range(2.4f, 3.6f),
                wind.Range(0.6f, 1.5f), wind.Range(0.1f, 0.22f), wind,
                _vortexLight);
        }

        /// <summary>
        /// A wall of dense snow that starts at an upwind source during a large gust and travels downwind.
        /// </summary>
        public void EmitGustFront(Vector3 origin, Vector3 center, StormWind wind, WindFlow flow, float intensity,
            Color light)
        {
            if (_puffs == null)
            {
                return;
            }

            Vector3 dir = wind.Prevailing;
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            float depth = wind.Range(10f, 16f);
            float halfWidth = wind.Range(14f, 22f);
            int count = Mathf.RoundToInt(wind.Range(14f, 24f) * Mathf.Clamp(intensity, 0.4f, 2f));
            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 sample = origin - dir * wind.Range(-depth, depth) * 0.5f + side * wind.Range(-halfWidth, halfWidth);
                if (!SnowSurface.TrySampleGround(sample, _terrainMask, _groundMask, out var ground)
                    || SnowSurface.IsInIndoorVolume(ground.point))
                {
                    continue;
                }

                flow.Sample(ground.point, wind, out Vector3 local, out float flowSpeed, out float shelter);
                if (shelter > 0.6f)
                {
                    continue;
                }

                float speed = wind.SnowSpeed * flowSpeed * wind.Range(0.95f, 1.15f);
                float size = wind.Range(5f, 12f);
                Emit(_puffs, ground.point + Vector3.up * (size * 0.25f + wind.Range(0f, 2f)),
                    local * speed + Vector3.up * wind.Range(0f, 0.4f), wind.Range(6f, 9f),
                    size, wind.Range(0.14f, 0.28f) * Mathf.Clamp(intensity, 0.4f, 1.6f), wind,
                    light);
                placed++;
            }

            float distance = Vector3.Dot(center - origin, dir);
            float lateral = Mathf.Abs(Vector3.Dot(center - origin, side));
            if (placed > count / 3 && distance > 0f && lateral < halfWidth)
            {
                wind.ScheduleFront(distance, depth);
            }
        }

        private float SampleHeight(Vector3 position, float fallback)
        {
            return SnowSurface.TrySampleGround(position, _terrainMask, _groundMask, out var hit) ? hit.point.y : fallback;
        }

        private static void Emit(ParticleSystem system, Vector3 position, Vector3 velocity, float lifetime,
            float size, float alpha, StormWind wind, Color light)
        {
            Color tint = light * new Color(0.95f, 0.95f, 0.96f);
            var particle = new ParticleSystem.EmitParams
            {
                position = position,
                velocity = velocity,
                startLifetime = lifetime,
                startSize = size,
                rotation = wind.Range(0f, 360f),
                startColor = new Color(tint.r, tint.g, tint.b, Mathf.Clamp01(alpha))
            };
            system.Emit(particle, 1);
        }

        public void Dispose()
        {
            if (_puffs != null)
            {
                Object.Destroy(_puffs.gameObject);
            }

            if (_vortex != null)
            {
                Object.Destroy(_vortex.gameObject);
            }

            Object.Destroy(_material);
            Object.Destroy(_texture);
        }
    }
}
