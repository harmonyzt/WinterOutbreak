using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WinterOutbreak
{
    internal static class BonfirePrefab
    {
        private const string PrefabPath = "assets/bonfire/winterstormsbonfire.prefab";
        private const string TablePath = "assets/bonfire/data/bonfire_materials.json";
        private const string TextureFolder = "assets/bonfire/textures/";

        private const float Scale = 0.8f;

        private static AssetBundle _bundle;
        private static GameObject _prefab;
        private static JObject _table;
        private static bool _loadTried;

        private static readonly Dictionary<string, Material> GameMaterials = new Dictionary<string, Material>();
        private static readonly List<Material> CreatedMaterials = new List<Material>();
        private static bool _materialsLogged;

        internal static bool IsAvailable
        {
            get
            {
                Load();
                return _prefab != null;
            }
        }

        // Ambient light colour of the original fire, brightened so its strongest channel is 1.
        internal static Color LightColor
        {
            get
            {
                var color = _table?["light Small_Fire_Ambient"]?["color"] as JArray;
                if (color == null)
                {
                    return new Color(1f, 0.56f, 0.24f);
                }

                var light = new Color((float)color[0], (float)color[1], (float)color[2]);
                float strongest = Mathf.Max(light.r, Mathf.Max(light.g, light.b));
                return strongest > 0f ? light / strongest : light;
            }
        }

        // The bundle stays loaded for the whole game session; only the materials made from it are per raid.
        private static void Load()
        {
            if (_loadTried)
            {
                return;
            }

            _loadTried = true;
            string path = Path.Combine(Path.GetDirectoryName(typeof(BonfirePrefab).Assembly.Location), "Resources", "bonfire.bundle");
            if (!File.Exists(path))
            {
                BarrelSettings.Log.LogWarning("Bonfire: Resources/bonfire.bundle is missing, using the built-in bonfire.");
                return;
            }

            _bundle = AssetBundle.LoadFromFile(path);
            if (_bundle == null)
            {
                BarrelSettings.Log.LogWarning("Bonfire: bonfire.bundle could not be loaded, using the built-in bonfire.");
                return;
            }

            _prefab = _bundle.LoadAsset<GameObject>(PrefabPath);
            var table = _bundle.LoadAsset<TextAsset>(TablePath);
            if (table != null)
            {
                _table = JObject.Parse(table.text);
            }

            if (_prefab == null)
            {
                BarrelSettings.Log.LogWarning("Bonfire: the bonfire prefab is missing from bonfire.bundle, using the built-in bonfire.");
            }
        }

        internal static GameObject CreateGhost()
        {
            var ghost = Object.Instantiate(_prefab);
            ghost.name = "WinterOutbreak bonfire preview";
            ghost.transform.localScale = Vector3.one * Scale;

            var flames = ghost.transform.Find("Model/Small_Fire");
            if (flames != null)
            {
                Object.DestroyImmediate(flames.gameObject);
            }

            foreach (var renderer in ghost.GetComponentsInChildren<MeshRenderer>())
            {
                // Only the first submesh: the scorch decal would otherwise turn into a big green square.
                renderer.sharedMaterials = new[] { renderer.sharedMaterials[0] };
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            return ghost;
        }

        internal static void AddTo(Transform bonfire)
        {
            var copy = Object.Instantiate(_prefab, bonfire, false);
            copy.name = "Customs bonfire";
            copy.transform.localScale = Vector3.one * Scale;

            foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    materials[i] = GameMaterial(materials[i]);
                }

                renderer.sharedMaterials = materials;
            }

            foreach (var particles in copy.GetComponentsInChildren<ParticleSystem>(true))
            {
                particles.Play(false);
            }

            if (!_materialsLogged)
            {
                _materialsLogged = true;
                var names = new List<string>();
                foreach (var pair in GameMaterials)
                {
                    names.Add(pair.Key + (pair.Value.shader.name == (string)_table?[pair.Key]?["shader"] ? "" : " (stand-in)"));
                }
            }
        }

        // Rebuilds a stand-in material on EFT's shader. Kept as the stand-in when the shader is not loaded.
        private static Material GameMaterial(Material standIn)
        {
            if (standIn == null)
            {
                return null;
            }

            if (GameMaterials.TryGetValue(standIn.name, out var cached))
            {
                return cached;
            }

            var entry = _table?[standIn.name] as JObject;
            Shader shader = entry != null ? ShadersFinder.Find((string)entry["shader"]) : null;
            if (shader == null || !shader.isSupported)
            {
                GameMaterials[standIn.name] = standIn;
                return standIn;
            }

            var material = new Material(shader)
            {
                name = standIn.name
            };

            foreach (var texture in (JObject)entry["textures"])
            {
                var image = _bundle.LoadAsset<Texture2D>(TextureFolder + ((string)texture.Value).ToLowerInvariant() + ".png");
                if (image != null && material.HasProperty(texture.Key))
                {
                    material.SetTexture(texture.Key, image);
                }
            }

            foreach (var value in (JObject)entry["floats"])
            {
                if (material.HasProperty(value.Key))
                {
                    material.SetFloat(value.Key, (float)value.Value);
                }
            }

            foreach (var value in (JObject)entry["colors"])
            {
                if (material.HasProperty(value.Key))
                {
                    var c = (JArray)value.Value;
                    material.SetColor(value.Key, new Color((float)c[0], (float)c[1], (float)c[2], (float)c[3]));
                }
            }

            if (material.HasProperty("_Cube") && material.GetTexture("_Cube") == null && material.HasProperty("_ReflectColor"))
            {
                material.SetColor("_ReflectColor", Color.clear);
            }

            GameMaterials[standIn.name] = material;
            CreatedMaterials.Add(material);
            return material;
        }

        internal static void Clear()
        {
            foreach (var material in CreatedMaterials)
            {
                if (material != null)
                {
                    Object.Destroy(material);
                }
            }

            CreatedMaterials.Clear();
            GameMaterials.Clear();
            _materialsLogged = false;
        }
    }
}
