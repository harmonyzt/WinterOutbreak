using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    /// <summary>
    /// Snow thrown up by a grenade
    /// </summary>
    internal sealed class GrenadeSnowBurst
    {
        private const int GrainsPerParticle = 3;
        private readonly SnowPowder _plume = new SnowPowder("Winter Outbreak Grenade Snow Plume", 1200, 0.03f, 1.1f, 3.0f, 0.6f);
        private readonly SnowPowder _ring = new SnowPowder("Winter Outbreak Grenade Snow Ring", 800, 0.0f, 1.8f, 3.2f, 0.4f);
        private readonly System.Random _random = new System.Random();
        private readonly Texture2D _texture;
        private readonly Material _material;
        private readonly ParticleSystem _particles;

        public bool IsAvailable => _particles != null;

        public GrenadeSnowBurst()
        {
            Shader shader = GroundSnowDrift.FindSnowParticleShader();
            if (shader == null)
            {
                return;
            }

            _texture = CreateGrainTexture();
            _material = new Material(shader) { mainTexture = _texture };
            var emitter = new GameObject("Winter Outbreak Grenade Snow Burst");
            emitter.SetActive(false);
            _particles = emitter.AddComponent<ParticleSystem>();

            var main = _particles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 2160;
            main.gravityModifier = 0.9f;

            var emission = _particles.emission;
            emission.enabled = false;
            var shape = _particles.shape;
            shape.enabled = false;

            var color = _particles.colorOverLifetime;
            color.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.84f, 0.70f),
                    new GradientAlphaKey(0f, 1f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            var renderer = emitter.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.sortMode = ParticleSystemSortMode.Distance;
            emitter.SetActive(true);
            _particles.Play();
        }

        public void Emit(Vector3 origin, float radius, int count, float power, float soilMix)
        {
            if (_particles == null)
            {
                return;
            }

            float impulse = Mathf.Lerp(0.65f, 1.15f, power);
            float phase = Range(0f, Mathf.PI * 2f);
            Color light = SnowPowder.Tint(1f, 1f);
            EmitPowder(origin, radius, power, soilMix, light, phase);
            int grainCount = count * GrainsPerParticle;
            for (int i = 0; i < grainCount; i++)
            {
                // Distribute the spray around the entire blast with a random rotation per grenade
                float angle = phase + i * 2.399963f;
                var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                bool lofted = _random.NextDouble() < 0.24;
                float radialSpeed = lofted ? Range(2.4f, 7.5f) : Range(4.0f, 12f);
                float riseSpeed = lofted ? Range(4.0f, 8.8f) : Range(2.4f, 8.4f);
                float brightness = Range(0.78f, 1.0f);
                float alpha = Range(0.74f, 0.94f);
                var tint = new Color(light.r * brightness, light.g * brightness, light.b * brightness, alpha);
                float grainSize = Range(0.04f, 0.11f);
                float lifetime = Range(0.65f, 1.35f);
                if (soilMix > 0f)
                {
                    float material = Range(0f, 1f);
                    var dirt = new Color(brightness * 0.65f, brightness * 0.48f, brightness * 0.34f, alpha);
                    if (material < soilMix * 0.10f)
                    {
                        tint = dirt;
                        grainSize *= 0.65f;
                        riseSpeed *= 0.68f;
                        lifetime *= 0.82f;
                    }
                    else if (material < soilMix * 0.30f)
                    {
                        tint = Color.Lerp(tint, dirt, Range(0.35f, 0.68f));
                        riseSpeed *= 0.88f;
                    }
                }

                var particle = new ParticleSystem.EmitParams
                {
                    position = origin + outward * Range(0.025f, radius * 0.30f) + Vector3.up * Range(0.035f, 0.12f),
                    velocity = (outward * radialSpeed + Vector3.up * riseSpeed) * impulse,
                    startLifetime = lifetime,
                    startSize = grainSize,
                    startColor = tint,
                    rotation = Range(0f, 360f)
                };
                _particles.Emit(particle, 1);
            }
        }

        private readonly System.Collections.Generic.List<DelayedBillow> _delayed =
            new System.Collections.Generic.List<DelayedBillow>();

        private struct DelayedBillow
        {
            public float Time;
            public Vector3 Origin;
            public float Radius;
            public float Power;
            public float SoilMix;
            public Vector3 Lean;
        }

        /// <summary>
        /// Emits second billows that rise a moment after the blast.
        /// </summary>
        public void Update(float now)
        {
            for (int i = _delayed.Count - 1; i >= 0; i--)
            {
                var billow = _delayed[i];
                if (now < billow.Time)
                {
                    continue;
                }

                _delayed.RemoveAt(i);
                EmitPlume(billow.Origin, billow.Radius, billow.Power * Range(0.35f, 0.6f), billow.SoilMix,
                    SnowPowder.Tint(1f, 1f), billow.Lean, Range(0.55f, 0.85f));
            }
        }

        private void EmitPowder(Vector3 origin, float radius, float power, float soilMix, Color light, float phase)
        {
            if (!_plume.IsAvailable)
            {
                return;
            }

            // Every blast differs
            float scale = Range(0.75f, 1.25f);
            var lean = new Vector3(Range(-1f, 1f), 0f, Range(-1f, 1f)) * Range(0.4f, 2.2f);
            EmitPlume(origin, radius, power * scale, soilMix, light, lean, 1f);
            EmitRing(origin, radius, power * Range(0.7f, 1.2f), light, phase);
            EmitClods(origin, radius, power * scale, soilMix, light, lean);
            if (_random.NextDouble() < 0.8)
            {
                _delayed.Add(new DelayedBillow
                {
                    Time = Time.time + Range(0.12f, 0.4f),
                    Origin = origin + new Vector3(Range(-0.4f, 0.4f), 0f, Range(-0.4f, 0.4f)) * radius,
                    Radius = radius,
                    Power = power,
                    SoilMix = soilMix,
                    Lean = lean
                });
            }
        }

        private Vector3 Random3()
        {
            return new Vector3(Range(-1f, 1f), Range(-1f, 1f), Range(-1f, 1f));
        }

        private Color Dirt(Color light)
        {
            return new Color(light.r * 0.62f, light.g * 0.50f, light.b * 0.40f, 1f);
        }

        /// <summary>
        /// One puff's colour
        /// </summary>
        private Color MixedPowder(Color light, float soilMix)
        {
            Color dirt = Dirt(light);
            float share = _random.NextDouble() < 0.25 ? Range(0.15f, 0.42f) : Range(0f, 0.12f);
            share = Mathf.Max(share, soilMix * Range(0.2f, 0.65f));
            Color color = Color.Lerp(light, dirt, share) * Range(0.72f, 1.0f);
            float shift = Range(-0.035f, 0.035f);
            color.r += shift;
            color.b -= shift;
            return color;
        }

        // Plume: rises fast, hangs, spreads and drifts for a few seconds
        private void EmitPlume(Vector3 origin, float radius, float power, float soilMix, Color light, Vector3 lean,
            float alphaScale)
        {
            // Snow is thrown up and out in a few random jets (where the blast found least resistance), plus a looser
            // cloud, so every blast has its own shape
            int jets = _random.Next(3, 7);
            var jetDirections = new Vector3[jets];
            for (int j = 0; j < jets; j++)
            {
                var flat = Random.insideUnitCircle.normalized;
                jetDirections[j] = new Vector3(flat.x * Range(0.4f, 1.2f), Range(1f, 2.2f), flat.y * Range(0.4f, 1.2f))
                    .normalized;
            }

            int plumes = Mathf.RoundToInt(Mathf.Lerp(10f, 28f, Mathf.Clamp01(power)) * Range(0.75f, 1.25f));
            for (int i = 0; i < plumes; i++)
            {
                var outward = Random.insideUnitCircle;
                var position = origin + new Vector3(outward.x, 0f, outward.y) * radius * Range(0.2f, 0.5f)
                    + Vector3.up * Range(0.1f, 0.7f);
                float strength = Mathf.Lerp(0.75f, 1.15f, Mathf.Clamp01(power));
                Vector3 velocity;
                if (_random.NextDouble() < 0.65)
                {
                    Vector3 jet = jetDirections[_random.Next(jets)];
                    velocity = (jet + Random3() * 0.18f) * Range(4f, 11f) * strength;
                }
                else
                {
                    velocity = new Vector3(outward.x * Range(1.5f, 4.5f), Range(3f, 7.5f), outward.y * Range(1.5f, 4.5f))
                        * strength;
                }

                velocity += lean * Range(0.5f, 1.2f);
                float shade = Range(0.70f, 0.95f);
                Color color = MixedPowder(light, soilMix) * shade;
                color.a = Range(0.18f, 0.34f) * alphaScale;
                _plume.Emit(position, velocity, Range(0.8f, 2.4f) * Mathf.Lerp(0.8f, 1.2f, Mathf.Clamp01(power)),
                    Range(2.6f, 5.8f), color, Range(0f, 360f));
            }
        }

        // Ring
        private void EmitRing(Vector3 origin, float radius, float power, Color light, float phase)
        {
            float lobeA = Range(0f, 6.28f), lobeB = Range(0f, 6.28f);
            int ringCount = Mathf.RoundToInt(Mathf.Lerp(12f, 30f, Mathf.Clamp01(power)) * Range(0.8f, 1.2f));
            for (int i = 0; i < ringCount; i++)
            {
                if (_random.NextDouble() < 0.18)
                {
                    continue;
                }

                float angle = phase + i * Mathf.PI * 2f / ringCount + Range(-0.18f, 0.18f);
                // Two random lobes: some sectors throw far, others barely lift
                float lobe = 0.55f + 0.3f * Mathf.Sin(angle * 2f + lobeA) + 0.25f * Mathf.Sin(angle * 3f + lobeB);
                var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                var position = origin + outward * radius * Range(0.25f, 0.6f) + Vector3.up * Range(0.05f, 0.35f);
                var velocity = outward * Range(4.5f, 11f) * Mathf.Lerp(0.7f, 1.1f, Mathf.Clamp01(power)) * Mathf.Max(0.3f, lobe)
                    + Vector3.up * Range(0.8f, 3.0f);
                Color color = MixedPowder(light, 0f) * Range(0.8f, 0.95f);
                color.a = Range(0.16f, 0.32f);
                _ring.Emit(position, velocity, Range(0.5f, 1.4f), Range(1.4f, 3.0f), color, Range(0f, 360f));
            }
        }

        // Clods: compacted chunks thrown high on arcs, readable against the sky
        private void EmitClods(Vector3 origin, float radius, float power, float soilMix, Color light, Vector3 lean)
        {
            Color dirt = Dirt(light);
            int clods = Mathf.RoundToInt(Mathf.Lerp(10f, 40f, Mathf.Clamp01(power)) * Range(0.7f, 1.3f));
            for (int i = 0; i < clods; i++)
            {
                float angle = Range(0f, Mathf.PI * 2f);
                var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                Color color = Color.Lerp(light, dirt, Mathf.Max(soilMix * Range(0f, 0.8f),
                    _random.NextDouble() < 0.3 ? Range(0.3f, 0.7f) : 0f)) * Range(0.75f, 1.0f);
                color.a = 0.92f;
                var particle = new ParticleSystem.EmitParams
                {
                    position = origin + outward * Range(0.05f, radius * 0.3f) + Vector3.up * 0.1f,
                    velocity = outward * Range(2f, 8f) + Vector3.up * Range(4.5f, 12f) + lean * 0.4f,
                    startLifetime = Range(1.1f, 2.1f),
                    startSize = Range(0.07f, 0.22f),
                    startColor = color,
                    rotation = Range(0f, 360f)
                };
                _particles.Emit(particle, 1);
            }
        }

        private static Texture2D CreateGrainTexture()
        {
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "Winter Outbreak Blast Snow Grain",
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
                    float edge = 0.80f + 0.08f * Mathf.Cos(5f * angle) + 0.05f * Mathf.Sin(3f * angle);
                    float radius = Mathf.Sqrt(px * px + py * py * 1.28f);
                    float feather = Mathf.InverseLerp(edge - 0.23f, edge, radius);
                    byte alpha = (byte)Mathf.RoundToInt(255f * Mathf.SmoothStep(1f, 0f, feather));
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
            _plume.Dispose();
            _ring.Dispose();
        }
    }
}
