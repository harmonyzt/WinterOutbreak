using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private Fixed<bool> _treeSnowEnabled;
        private Fixed<int> _treeSnowCoverage;
        private Fixed<float> _treeSnowRefill;
        private Fixed<int> _treeSnowShower;
        private TreeSnowArea _treeSnow;

        private void BindTreeSnowSettings()
        {
            _treeSnowEnabled = new Fixed<bool>(true);
            _treeSnowCoverage = new Fixed<int>(35);
            _treeSnowRefill = new Fixed<float>(55f);
            _treeSnowShower = new Fixed<int>(105);
        }

        private void UpdateTreeSnow(Player player)
        {
            if (player == null || !_treeSnowEnabled.Value || _treeSnowCoverage.Value == 0)
            {
                ClearTreeSnow();
                return;
            }

            if (player.Environment != EnvironmentType.Outdoor)
            {
                _treeSnow?.ClearContact();
                return;
            }

            if (_treeSnow == null)
            {
                _treeSnow = new TreeSnowArea(_treeSnowCoverage.Value);
                if (!_treeSnow.CanEmit)
                {
                    Logger.LogWarning("Branch snow could not find a supported particle shader.");
                }
            }

            _treeSnow.Update(player.Position, player.Velocity, _treeSnowRefill.Value, _treeSnowShower.Value);
        }

        private void ClearTreeSnow()
        {
            _treeSnow?.Dispose();
            _treeSnow = null;
        }

        [HarmonyPatch(typeof(AIData), nameof(AIData.StayInTree))]
        private static class TreeContactPatch
        {
            private static void Postfix(AIData __instance, TreeInteractive treeInteractive, Collider triggerCollider)
            {
                var plugin = _plugin;
                if (plugin?._treeSnow != null && plugin._raid != null && ReferenceEquals(__instance.Player, plugin._raid.MainPlayer))
                {
                    plugin._treeSnow.NoteContact(treeInteractive, triggerCollider);
                }
            }
        }
    }

    internal sealed class TreeSnowArea
    {
        private const float ScanRadius = 15f;
        private const float ReleaseRadius = 22f;
        private const float TouchTime = 0.30f;
        private const int MaxVisiblePlants = 28;
        private const int MaxPatchesPerPlant = 10;

        private readonly Collider[] _nearby = new Collider[96];
        private readonly Dictionary<int, Plant> _plants = new Dictionary<int, Plant>();
        private readonly Dictionary<int, float> _refillAt = new Dictionary<int, float>();
        private readonly List<int> _outOfRange = new List<int>();
        private readonly System.Random _random = new System.Random();
        private readonly int _coverage;
        private readonly int _foliageMask = LayersMaskController.Foliage | LayersMaskController.TriggersMask;
        private readonly int _supportMask = LayersMaskController.Foliage | LayersMaskController.HighPolyCollider;
        private readonly Material _branchMaterial;
        private readonly Material _particleMaterial;
        private readonly Texture2D _texture;
        private readonly ParticleSystem _particles;

        private TreeInteractive _contact;
        private Collider _contactVolume;
        private float _lastContact;
        private float _nextScan;

        public bool CanEmit => _particles != null;

        public TreeSnowArea(int coverage)
        {
            _coverage = coverage;
            _branchMaterial = SnowSurface.CreateSnowMaterial();

            Shader shader = GroundSnowDrift.FindSnowParticleShader();
            if (shader == null)
            {
                return;
            }

            _texture = GroundSnowDrift.CreateWispTexture();
            _particleMaterial = new Material(shader)
            {
                mainTexture = _texture
            };
            var emitter = new GameObject("Winter Outbreak Branch Snow");
            emitter.SetActive(false);
            _particles = emitter.AddComponent<ParticleSystem>();

            var main = _particles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 320;
            main.gravityModifier = 0.45f;

            var emission = _particles.emission;
            emission.enabled = false;
            var shape = _particles.shape;
            shape.enabled = false;

            var color = _particles.colorOverLifetime;
            color.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.75f, 0.55f), new GradientAlphaKey(0f, 1f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            var renderer = emitter.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = _particleMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.sortMode = ParticleSystemSortMode.Distance;
            emitter.SetActive(true);
            _particles.Play();
        }

        public void NoteContact(TreeInteractive tree, Collider volume)
        {
            if (tree != null && volume != null && IsCoated(tree.GetInstanceID()))
            {
                _contact = tree;
                _contactVolume = volume;
                _lastContact = Time.time;
            }
        }

        public void ClearContact()
        {
            _contact = null;
            _contactVolume = null;
        }

        public void Update(Vector3 position, Vector3 velocity, float refillSeconds, int showerParticles)
        {
            float now = Time.time;
            if (_contact != null && _contactVolume != null && now - _lastContact <= TouchTime && velocity.x * velocity.x + velocity.z * velocity.z > 0.25f * 0.25f)
            {
                ShakeTree(_contact, _contactVolume, position, now, refillSeconds, showerParticles);
            }

            if (now < _nextScan)
            {
                return;
            }

            _nextScan = now + 0.45f;
            RefreshPlants(position, now);
            DiscoverPlant(position, now);
        }

        private bool IsCoated(int id)
        {
            uint hash = unchecked((uint)id * 2654435761u);
            return hash % 100u < _coverage;
        }

        private void RefreshPlants(Vector3 position, float now)
        {
            _outOfRange.Clear();
            foreach (var entry in _plants)
            {
                Plant plant = entry.Value;
                if (plant.Tree == null || (plant.Position - position).sqrMagnitude > ReleaseRadius * ReleaseRadius)
                {
                    DestroyPlant(plant);
                    _outOfRange.Add(entry.Key);
                }
                else if (plant.Object != null && !plant.Object.activeSelf && IsReady(entry.Key, now))
                {
                    plant.Object.SetActive(true);
                }
            }

            foreach (int id in _outOfRange)
            {
                _plants.Remove(id);
            }

            _outOfRange.Clear();
            foreach (var entry in _refillAt)
            {
                if (entry.Value <= now)
                {
                    _outOfRange.Add(entry.Key);
                }
            }

            foreach (int id in _outOfRange)
            {
                _refillAt.Remove(id);
            }
        }

        private void DiscoverPlant(Vector3 position, float now)
        {
            if (_branchMaterial == null || _plants.Count >= MaxVisiblePlants)
            {
                return;
            }

            int count = Physics.OverlapSphereNonAlloc(position, ScanRadius, _nearby, _foliageMask, QueryTriggerInteraction.Collide);
            TreeInteractive nearestTree = null;
            Collider nearestVolume = null;
            float nearestDistance = float.PositiveInfinity;

            for (int i = 0; i < count; i++)
            {
                Collider volume = _nearby[i];
                _nearby[i] = null;
                if (volume == null || !volume.isTrigger)
                {
                    continue;
                }

                var tree = volume.GetComponentInParent<TreeInteractive>();
                if (tree == null)
                {
                    continue;
                }

                int id = tree.GetInstanceID();
                float distance = volume.bounds.SqrDistance(position);
                if (distance < nearestDistance && IsCoated(id) && !_plants.ContainsKey(id))
                {
                    nearestTree = tree;
                    nearestVolume = volume;
                    nearestDistance = distance;
                }
            }

            if (nearestTree != null)
            {
                CreatePlant(nearestTree, nearestVolume, now);
            }
        }

        private Plant CreatePlant(TreeInteractive tree, Collider volume, float now)
        {
            Bounds bounds = volume.bounds;
            var plant = new Plant(tree, bounds.center);
            int id = tree.GetInstanceID();
            _plants.Add(id, plant);

            if (_branchMaterial == null)
            {
                return plant;
            }

            var random = new System.Random(id);
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int i = 0; i < 36 && plant.Anchors.Count < MaxPatchesPerPlant; i++)
            {
                float x = bounds.center.x + (float)(random.NextDouble() * 2d - 1d) * Mathf.Min(2.6f, bounds.extents.x);
                float z = bounds.center.z + (float)(random.NextDouble() * 2d - 1d) * Mathf.Min(2.6f, bounds.extents.z);
                var start = new Vector3(x, bounds.max.y + 0.75f, z);

                if (!Physics.Raycast(start, Vector3.down, out var hit, bounds.size.y + 1f, _supportMask, QueryTriggerInteraction.Ignore) || hit.normal.y < 0.30f
                    || hit.point.y < bounds.min.y + 0.45f || hit.collider.GetComponentInParent<TreeInteractive>() != tree)
                {
                    continue;
                }

                Vector3 anchor = hit.point + hit.normal * 0.016f;
                if (plant.Anchors.Exists(point => (tree.transform.TransformPoint(point) - anchor).sqrMagnitude < 0.25f * 0.25f))
                {
                    continue;
                }

                plant.Anchors.Add(tree.transform.InverseTransformPoint(anchor));
                AddSnowPatch(vertices, triangles, tree.transform, anchor, hit.normal, Mathf.Lerp(0.055f, 0.13f, (float)random.NextDouble()));
            }

            if (vertices.Count == 0)
            {
                return plant;
            }

            plant.Mesh = new Mesh
            {
                name = "Winter Outbreak Branch Snow Patches"
            };
            plant.Mesh.SetVertices(vertices);
            plant.Mesh.SetTriangles(triangles, 0);
            plant.Mesh.RecalculateNormals();
            plant.Object = new GameObject("Winter Outbreak Resting Branch Snow");
            plant.Object.transform.SetParent(tree.transform, false);
            plant.Object.AddComponent<MeshFilter>().sharedMesh = plant.Mesh;
            var renderer = plant.Object.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _branchMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            plant.Object.SetActive(IsReady(id, now));
            return plant;
        }

        private static void AddSnowPatch(List<Vector3> vertices, List<int> triangles, Transform tree, Vector3 anchor, Vector3 normal, float radius)
        {
            const int sides = 8;
            Vector3 tangent = Vector3.Cross(normal, Vector3.forward).normalized;
            Vector3 across = Vector3.Cross(tangent, normal);
            int center = vertices.Count;
            vertices.Add(tree.InverseTransformPoint(anchor + normal * 0.014f));

            for (int i = 0; i < sides; i++)
            {
                float angle = i * (Mathf.PI * 2f / sides);
                Vector3 offset = tangent * (Mathf.Cos(angle) * radius) + across * (Mathf.Sin(angle) * radius * 0.7f);
                vertices.Add(tree.InverseTransformPoint(anchor + offset - normal * 0.022f));
                triangles.Add(center);
                triangles.Add(center + 1 + (i + 1) % sides);
                triangles.Add(center + 1 + i);
            }
        }

        private bool IsReady(int id, float now)
        {
            return !_refillAt.TryGetValue(id, out float readyAt) || now >= readyAt;
        }

        private void ShakeTree(TreeInteractive tree, Collider volume, Vector3 position, float now, float refillSeconds, int showerParticles)
        {
            int id = tree.GetInstanceID();
            if (!IsCoated(id) || !IsReady(id, now))
            {
                return;
            }

            if (!_plants.TryGetValue(id, out var plant) && _plants.Count < MaxVisiblePlants)
            {
                plant = CreatePlant(tree, volume, now);
            }

            _refillAt[id] = now + refillSeconds;
            if (plant?.Object != null)
            {
                plant.Object.SetActive(false);
            }

            if (_particles == null)
            {
                return;
            }

            Bounds bounds = volume.bounds;
            Vector3 contact = bounds.ClosestPoint(position + Vector3.up * 1.8f);
            int anchorCount = plant?.Anchors.Count ?? 0;
            if (anchorCount > 0)
            {
                int perAnchor = showerParticles / anchorCount;
                int remainder = showerParticles % anchorCount;
                for (int i = 0; i < anchorCount; i++)
                {
                    EmitSnow(tree.transform.TransformPoint(plant.Anchors[i]), perAnchor + (i < remainder ? 1 : 0));
                }
            }
            else
            {
                EmitSnow(contact, showerParticles);
            }
        }

        private void EmitSnow(Vector3 origin, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var offset = new Vector3(Range(-0.30f, 0.30f), Range(-0.08f, 0.23f), Range(-0.30f, 0.30f));
                var velocity = new Vector3(Range(-0.75f, 0.75f), Range(-0.62f, 0.16f), Range(-0.75f, 0.75f));
                var particle = new ParticleSystem.EmitParams
                {
                    position = origin + offset,
                    velocity = velocity,
                    startLifetime = Range(0.75f, 1.50f),
                    startSize = Range(0.045f, 0.15f),
                    startColor = new Color(0.93f, 0.96f, 1f, Range(0.58f, 0.90f))
                };
                _particles.Emit(particle, 1);
            }
        }

        private float Range(float min, float max)
        {
            return Mathf.Lerp(min, max, (float)_random.NextDouble());
        }

        private static void DestroyPlant(Plant plant)
        {
            Object.Destroy(plant.Object);
            Object.Destroy(plant.Mesh);
        }

        public void Dispose()
        {
            foreach (var plant in _plants.Values)
            {
                DestroyPlant(plant);
            }

            if (_particles != null)
            {
                Object.Destroy(_particles.gameObject);
            }

            Object.Destroy(_branchMaterial);
            Object.Destroy(_particleMaterial);
            Object.Destroy(_texture);
        }

        private sealed class Plant
        {
            public readonly TreeInteractive Tree;
            public readonly Vector3 Position;
            public readonly List<Vector3> Anchors = new List<Vector3>();
            public GameObject Object;
            public Mesh Mesh;

            public Plant(TreeInteractive tree, Vector3 position)
            {
                Tree = tree;
                Position = position;
            }
        }
    }
}
