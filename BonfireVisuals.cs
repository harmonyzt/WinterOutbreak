using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace WinterOutbreak
{
    internal sealed class PlacedBonfire
    {
        public GameObject Root;
        public Light Light;
        public Vector3 Heat;
        public float Seed;
        public string FlameSource;

        /// <summary>
        /// Built by another player in a Fika raid.
        /// </summary>
        public bool Remote;
    }

    /// <summary>
    /// Builds the bonfire: the Customs bonfire from bonfire.bundle (see BonfirePrefab), plus a flickering light and the
    /// crackle. Without the bundle it falls back to a ring of stones around a teepee of logs made from Unity
    /// primitives, with flames copied from a burning barrel on the map or simple built-in flames.
    /// </summary>
    internal static class BonfireVisuals
    {
        private static Material _bark;
        private static Material _charred;
        private static Material _stone;
        private static Material _ash;
        private static Texture2D _grain;
        private static Texture2D _glow;
        private static Material _flameMaterial;
        private static Material _smokeMaterial;

        private static bool _searchedMap;
        private static Transform _mapFlames;
        private static AudioClip _mapCrackle;

        internal static GameObject CreateGhost()
        {
            GameObject root;
            if (BonfirePrefab.IsAvailable)
            {
                root = BonfirePrefab.CreateGhost();
            }
            else
            {
                root = new GameObject("WinterOutbreak bonfire preview");
                BuildPile(root.transform, 1);
            }

            // EFT's own placing look (the one beacons and tripwires use): green when it fits, red when not.
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            var look = root.AddComponent<PreviewMaterialSetter>();
            look.SetAvailable(false);
            return root;
        }

        internal static PlacedBonfire CreateBonfire(Vector3 position, Quaternion rotation, Vector3 heat, AudioClip crackle)
        {
            var root = new GameObject("WinterOutbreak bonfire");
            root.transform.SetPositionAndRotation(position, rotation);
            int seed = Random.Range(1, 100000);
            FindMapFire();

            // The Customs bonfire from bonfire.bundle; the pile built from primitives only if that is missing.
            string source;
            if (BonfirePrefab.IsAvailable)
            {
                BonfirePrefab.AddTo(root.transform);
                source = "Customs bonfire";
            }
            else
            {
                BuildPile(root.transform, seed);
                Vector3 flameBase = position + Vector3.up * 0.08f;
                bool copied = CopyMapFlames(root.transform, flameBase);
                if (!copied)
                {
                    BuildFlames(root.transform, flameBase);
                }

                source = copied ? "copied from the map" : "built in";
            }

            var lightObject = new GameObject("Light");
            lightObject.transform.SetParent(root.transform, false);
            lightObject.transform.position = position + Vector3.up * 0.5f;
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = BonfirePrefab.IsAvailable ? BonfirePrefab.LightColor : new Color(1f, 0.56f, 0.24f);
            light.range = 7f;
            light.intensity = 1.6f;
            light.shadows = LightShadows.None;

            AudioClip clip = crackle != null ? crackle : _mapCrackle;
            if (clip != null && root.GetComponentInChildren<AudioSource>() == null)
            {
                AddCrackle(root.transform, clip);
            }

            return new PlacedBonfire
            {
                Root = root,
                Light = light,
                Heat = heat,
                Seed = seed * 0.013f,
                FlameSource = source
            };
        }

        internal static void Flicker(List<PlacedBonfire> bonfires)
        {
            float time = Time.time;
            foreach (var bonfire in bonfires)
            {
                if (bonfire.Light == null)
                {
                    continue;
                }

                float fast = Mathf.PerlinNoise(time * 7f, bonfire.Seed);
                float slow = Mathf.PerlinNoise(time * 1.3f, bonfire.Seed + 5f);
                bonfire.Light.intensity = 1.6f * (0.72f + 0.3f * fast + 0.15f * slow);
            }
        }

        internal static void Clear()
        {
            DestroyAsset(ref _bark);
            DestroyAsset(ref _charred);
            DestroyAsset(ref _stone);
            DestroyAsset(ref _ash);
            DestroyAsset(ref _flameMaterial);
            DestroyAsset(ref _smokeMaterial);
            DestroyAsset(ref _grain);
            DestroyAsset(ref _glow);

            BonfirePrefab.Clear();
            _searchedMap = false;
            _mapFlames = null;
            _mapCrackle = null;
        }

        private static void DestroyAsset<T>(ref T asset) where T : Object
        {
            if (asset != null)
            {
                Object.Destroy(asset);
            }

            asset = null;
        }

        private static void BuildPile(Transform root, int seed)
        {
            EnsureMaterials();
            var random = new System.Random(seed);

            AddPart(root, PrimitiveType.Cylinder, _ash, new Vector3(0f, 0.005f, 0f), Quaternion.identity,
                new Vector3(0.62f, 0.005f, 0.62f));

            // Ring of stones.
            const int stones = 9;
            for (int i = 0; i < stones; i++)
            {
                float angle = (i + (float)random.NextDouble() * 0.3f) * Mathf.PI * 2f / stones;
                float radius = 0.4f + (float)random.NextDouble() * 0.04f;
                var place = new Vector3(Mathf.Cos(angle) * radius, 0.045f, Mathf.Sin(angle) * radius);
                var size = new Vector3(Range(random, 0.17f, 0.22f), Range(random, 0.1f, 0.14f), Range(random, 0.14f, 0.18f));
                var turn = Quaternion.Euler(Range(random, -10f, 10f), -angle * Mathf.Rad2Deg, Range(random, -10f, 10f));
                AddPart(root, PrimitiveType.Sphere, _stone, place, turn, size);
            }

            // Two charred logs lying across the fire bed.
            for (int i = 0; i < 2; i++)
            {
                float yaw = i * 90f + Range(random, -20f, 20f);
                AddPart(root, PrimitiveType.Cylinder, _charred, new Vector3(0f, 0.05f + i * 0.05f, 0f),
                    Quaternion.Euler(0f, yaw, 90f), new Vector3(0.09f, 0.27f, 0.09f));
            }

            // Teepee of logs leaning in to a point above the middle.
            const int logs = 5;
            for (int i = 0; i < logs; i++)
            {
                float angle = (i + (float)random.NextDouble() * 0.25f) * Mathf.PI * 2f / logs;
                var foot = new Vector3(Mathf.Cos(angle) * 0.26f, 0.02f, Mathf.Sin(angle) * 0.26f);
                var tip = new Vector3(Mathf.Cos(angle) * 0.03f, Range(random, 0.38f, 0.44f), Mathf.Sin(angle) * 0.03f);
                Vector3 along = tip - foot;
                float thickness = Range(random, 0.075f, 0.1f);
                AddPart(root, PrimitiveType.Cylinder, i % 2 == 0 ? _bark : _charred, (foot + tip) * 0.5f,
                    Quaternion.FromToRotation(Vector3.up, along), new Vector3(thickness, along.magnitude * 0.55f, thickness));
            }
        }

        private static float Range(System.Random random, float min, float max)
        {
            return min + (float)random.NextDouble() * (max - min);
        }

        private static void AddPart(Transform root, PrimitiveType shape, Material material, Vector3 position,
            Quaternion rotation, Vector3 scale)
        {
            var part = GameObject.CreatePrimitive(shape);

            // No colliders: the bonfire must not block the placing rays or end up as ground under the snow.
            Object.DestroyImmediate(part.GetComponent<Collider>());

            part.transform.SetParent(root, false);
            part.transform.localPosition = position;
            part.transform.localRotation = rotation;
            part.transform.localScale = scale;

            var renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        private static void EnsureMaterials()
        {
            if (_bark != null)
            {
                return;
            }

            _grain = CreateGrainTexture();
            _bark = CreateSolid(new Color(0.3f, 0.21f, 0.14f), 0.08f);
            _charred = CreateSolid(new Color(0.09f, 0.075f, 0.065f), 0.05f);
            _stone = CreateSolid(new Color(0.46f, 0.45f, 0.43f), 0.15f);
            _ash = CreateSolid(new Color(0.16f, 0.15f, 0.14f), 0.02f);
        }

        private static Material CreateSolid(Color color, float gloss)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null || !shader.isSupported)
            {
                shader = Shader.Find("Legacy Shaders/Diffuse");
            }

            var material = new Material(shader) { color = color, mainTexture = _grain };
            if (material.HasProperty("_Glossiness"))
            {
                material.SetFloat("_Glossiness", gloss);
            }

            return material;
        }

        private static Texture2D CreateGrainTexture()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "Winter Outbreak bonfire grain" };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Streaks along the length of a log, blotches across it.
                    float streak = Mathf.PerlinNoise(x * 0.35f, y * 0.04f);
                    float blotch = Mathf.PerlinNoise(x * 0.12f + 9f, y * 0.12f + 3f);
                    byte value = (byte)(Mathf.Lerp(0.62f, 1f, streak * 0.6f + blotch * 0.4f) * 255f);
                    pixels[y * size + x] = new Color32(value, value, value, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true);
            return texture;
        }

        // Once per raid: a burning barrel to copy flames from, and a looping fire sound to borrow.
        private static void FindMapFire()
        {
            if (_searchedMap)
            {
                return;
            }

            _searchedMap = true;
            foreach (var flame in Object.FindObjectsOfType<FlameDamageTrigger>())
            {
                Transform root = flame.transform.parent != null ? flame.transform.parent : flame.transform;
                if (_mapFlames == null && root.GetComponentInChildren<ParticleSystem>(true) != null)
                {
                    _mapFlames = flame.transform;
                }

                if (_mapCrackle == null)
                {
                    _mapCrackle = FindLoopingClip(root);
                }
            }

            if (_mapCrackle == null)
            {
                foreach (var source in Object.FindObjectsOfType<AudioSource>())
                {
                    if (source.clip != null && source.loop && LooksLikeFire(source.clip.name))
                    {
                        _mapCrackle = source.clip;
                        break;
                    }
                }
            }
        }

        private static AudioClip FindLoopingClip(Transform root)
        {
            foreach (var source in root.GetComponentsInChildren<AudioSource>(true))
            {
                if (source.clip != null && source.loop)
                {
                    return source.clip;
                }
            }

            return null;
        }

        private static bool LooksLikeFire(string name)
        {
            name = name.ToLowerInvariant();
            return name.Contains("fire") || name.Contains("flame") || name.Contains("bonfire") || name.Contains("burn")
                || name.Contains("crackl") || name.Contains("campfire");
        }

        private static bool CopyMapFlames(Transform bonfire, Vector3 flameBase)
        {
            if (_mapFlames == null)
            {
                return false;
            }

            Transform root = _mapFlames.parent != null ? _mapFlames.parent : _mapFlames;
            Vector3 anchor = _mapFlames.position;
            bool copied = false;

            foreach (var system in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                // Only the outermost systems; their children come along with them.
                if (HasParticleAncestor(system.transform, root))
                {
                    continue;
                }

                Vector3 offset = system.transform.position - anchor;
                if (offset.sqrMagnitude > 1.2f * 1.2f)
                {
                    continue;
                }

                offset.y = Mathf.Clamp(offset.y, -0.1f, 0.5f);
                var copy = Object.Instantiate(system.gameObject, bonfire);
                copy.transform.SetPositionAndRotation(flameBase + offset, system.transform.rotation);
                StripCopy(copy);
                copy.SetActive(true);

                foreach (var particles in copy.GetComponentsInChildren<ParticleSystem>(true))
                {
                    particles.gameObject.SetActive(true);
                    particles.Play(false);
                }

                copied = true;
            }

            return copied;
        }

        private static bool HasParticleAncestor(Transform transform, Transform root)
        {
            for (var parent = transform.parent; parent != null && parent != root.parent; parent = parent.parent)
            {
                if (parent.GetComponent<ParticleSystem>() != null)
                {
                    return true;
                }
            }

            return false;
        }

        // The copy keeps its particles and sounds, nothing else: no damage triggers, colliders, lights or map scripts.
        private static void StripCopy(GameObject copy)
        {
            foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                Object.DestroyImmediate(behaviour);
            }

            foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
            {
                Object.DestroyImmediate(collider);
            }

            foreach (var light in copy.GetComponentsInChildren<Light>(true))
            {
                Object.DestroyImmediate(light);
            }

            foreach (var source in copy.GetComponentsInChildren<AudioSource>(true))
            {
                source.gameObject.SetActive(true);
                source.enabled = true;
                if (source.clip != null && source.loop && !source.isPlaying)
                {
                    source.Play();
                }
            }
        }

        private static void BuildFlames(Transform bonfire, Vector3 flameBase)
        {
            EnsureParticleMaterials();
            if (_flameMaterial == null)
            {
                return;
            }

            var flames = CreateEmitter(bonfire, "Flames", flameBase, _flameMaterial, 120);
            var main = flames.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.85f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 0.95f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.22f, 0.4f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.62f, 0.22f, 0.9f), new Color(1f, 0.82f, 0.4f, 0.9f));
            var emission = flames.emission;
            emission.rateOverTime = 45f;
            var shape = flames.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 8f;
            shape.radius = 0.14f;
            FadeAndShrink(flames, new Color(1f, 0.85f, 0.55f), new Color(0.9f, 0.25f, 0.05f), 0.25f);

            var embers = CreateEmitter(bonfire, "Embers", flameBase + Vector3.up * 0.15f, _flameMaterial, 40);
            main = embers.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.8f, 1.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.015f, 0.03f);
            main.startColor = new Color(1f, 0.6f, 0.2f, 1f);
            emission = embers.emission;
            emission.rateOverTime = 6f;
            shape = embers.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 20f;
            shape.radius = 0.1f;
            var noise = embers.noise;
            noise.enabled = true;
            noise.strength = 0.6f;
            noise.frequency = 0.8f;
            FadeAndShrink(embers, new Color(1f, 0.7f, 0.3f), new Color(0.8f, 0.2f, 0.05f), 0.6f);

            if (_smokeMaterial == null)
            {
                return;
            }

            var smoke = CreateEmitter(bonfire, "Smoke", flameBase + Vector3.up * 0.45f, _smokeMaterial, 40);
            main = smoke.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 0.7f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.35f, 0.55f);
            main.startColor = new Color(0.32f, 0.31f, 0.3f, 0.22f);
            emission = smoke.emission;
            emission.rateOverTime = 4f;
            shape = smoke.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 6f;
            shape.radius = 0.12f;
            var size = smoke.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 3.2f));
            var color = smoke.colorOverLifetime;
            color.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);
        }

        private static ParticleSystem CreateEmitter(Transform parent, string name, Vector3 position, Material material, int max)
        {
            var emitter = new GameObject(name);
            emitter.SetActive(false);
            emitter.transform.SetParent(parent, false);
            emitter.transform.SetPositionAndRotation(position, Quaternion.Euler(-90f, 0f, 0f));

            var particles = emitter.AddComponent<ParticleSystem>();
            var main = particles.main;
            main.loop = true;
            main.playOnAwake = true;
            main.maxParticles = max;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var renderer = emitter.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            emitter.SetActive(true);
            return particles;
        }

        private static void FadeAndShrink(ParticleSystem particles, Color start, Color end, float endSize)
        {
            var color = particles.colorOverLifetime;
            color.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(start, 0f), new GradientColorKey(end, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.1f), new GradientAlphaKey(0f, 1f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            var size = particles.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, endSize));
        }

        private static void EnsureParticleMaterials()
        {
            if (_flameMaterial != null)
            {
                return;
            }

            _glow = CreateGlowTexture();
            Shader additive = Shader.Find("Legacy Shaders/Particles/Additive");
            Shader blended = GroundSnowDrift.FindSnowParticleShader();
            Shader flameShader = additive != null && additive.isSupported ? additive : blended;
            if (flameShader != null)
            {
                _flameMaterial = new Material(flameShader) { mainTexture = _glow };
            }

            if (blended != null)
            {
                _smokeMaterial = new Material(blended) { mainTexture = _glow };
            }
        }

        private static Texture2D CreateGlowTexture()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Winter Outbreak bonfire glow" };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float falloff = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    byte alpha = (byte)(falloff * falloff * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        private static void AddCrackle(Transform bonfire, AudioClip clip)
        {
            var sound = new GameObject("Crackle");
            sound.transform.SetParent(bonfire, false);
            sound.transform.localPosition = Vector3.up * 0.3f;

            var source = sound.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 2f;
            source.maxDistance = 24f;
            source.volume = 1f;
            source.dopplerLevel = 0f;

            var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
            if (audio != null && audio.VeryStandartMixerGroup != null)
            {
                source.outputAudioMixerGroup = audio.VeryStandartMixerGroup;
            }

            source.time = Random.Range(0f, clip.length);
            source.Play();
        }
    }
}
