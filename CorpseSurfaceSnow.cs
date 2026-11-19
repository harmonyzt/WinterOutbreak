using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    internal sealed class CorpseSurfaceSnow
    {
        private const int MaxGrainsPerRenderer = 140;
        private const int MaxCoatedRenderers = 8;
        private const float GrainsPerSquareMeter = 460f;
        private readonly PlayerBody.SlotView _slot;
        private readonly GameObject _model;
        private readonly Renderer _source;
        private readonly MeshRenderer _renderer;
        private readonly Mesh _mesh;
        private readonly int[] _indices;
        private int _visibleGrains = -1;

        private CorpseSurfaceSnow(PlayerBody.SlotView slot, Renderer source, Material material,
            List<Vector3> vertices, List<Vector3> normals, List<int> triangles)
        {
            _slot = slot;
            _model = slot?.Model;
            _source = source;
            _indices = triangles.ToArray();
            _mesh = new Mesh { name = "Winter Outbreak corpse snow dust" };
            _mesh.MarkDynamic();
            _mesh.SetVertices(vertices);
            _mesh.SetNormals(normals);
            _mesh.SetTriangles(triangles, 0);

            var dust = new GameObject("Winter Outbreak corpse snow dust");
            dust.transform.SetParent(source.transform, false);
            dust.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = dust.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = material;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.enabled = false;
        }

        public static List<CorpseSurfaceSnow> Create(PlayerBody body, Material material, int roofMask,
            bool suppressIndoorSnow)
        {
            var coats = new List<CorpseSurfaceSnow>();
            if (body == null)
            {
                return coats;
            }

            var seen = new HashSet<Renderer>();
            if (body.BodySkins != null)
            {
                foreach (var part in body.BodySkins.Values)
                {
                    if (part == null)
                    {
                        continue;
                    }

                    foreach (var source in part.GetRenderers())
                    {
                        if (source == null || !source.enabled || !source.gameObject.activeInHierarchy
                            || !seen.Add(source))
                        {
                            continue;
                        }

                        var coat = SampleRenderer(null, source, material, roofMask, suppressIndoorSnow);
                        if (coat != null)
                        {
                            coats.Add(coat);
                            break;
                        }
                    }
                }
            }

            if (body.SlotViews == null)
            {
                return coats;
            }

            int gearLimit = coats.Count + MaxCoatedRenderers;
            for (int i = 0; i < body.SlotViews.Count && coats.Count < gearLimit; i++)
            {
                var slot = body.SlotViews.GetByIndex(i);
                if (slot?.Model == null || !CanDust(slot.EquipmentSlot))
                {
                    continue;
                }

                foreach (var source in slot.Renderers ?? slot.Model.GetComponentsInChildren<Renderer>())
                {
                    if (coats.Count == gearLimit)
                    {
                        break;
                    }

                    if (source == null || !source.enabled || !source.gameObject.activeInHierarchy
                        || !seen.Add(source))
                    {
                        continue;
                    }

                    var coat = SampleRenderer(slot, source, material, roofMask, suppressIndoorSnow);
                    if (coat != null)
                    {
                        coats.Add(coat);
                    }
                }
            }

            return coats;
        }

        private static bool CanDust(EquipmentSlot slot)
        {
            return slot == EquipmentSlot.Backpack || slot == EquipmentSlot.TacticalVest
                || slot == EquipmentSlot.ArmorVest || slot == EquipmentSlot.Headwear;
        }

        private static CorpseSurfaceSnow SampleRenderer(PlayerBody.SlotView slot, Renderer source, Material material,
            int roofMask, bool suppressIndoorSnow)
        {
            Mesh temporary = null;
            try
            {
                Mesh mesh;
                if (source is SkinnedMeshRenderer skin && skin.sharedMesh != null)
                {
                    temporary = new Mesh();
                    skin.BakeMesh(temporary);
                    mesh = temporary;
                }
                else
                {
                    mesh = source.GetComponent<MeshFilter>()?.sharedMesh;
                }

                if (mesh == null || !mesh.isReadable)
                {
                    return null;
                }

                var vertices = new List<Vector3>();
                var normals = new List<Vector3>();
                var triangles = new List<int>();
                SampleGrains(mesh, source, roofMask, slot == null, suppressIndoorSnow, vertices, normals, triangles);
                return triangles.Count == 0 ? null
                    : new CorpseSurfaceSnow(slot, source, material, vertices, normals, triangles);
            }
            finally
            {
                UnityEngine.Object.Destroy(temporary);
            }
        }

        private static void SampleGrains(Mesh mesh, Renderer source, int roofMask, bool clothing, bool suppressIndoorSnow,
            List<Vector3> vertices, List<Vector3> normals, List<int> triangles)
        {
            Vector3[] points = mesh.vertices;
            int[] indices = mesh.triangles;
            Transform transform = source.transform;
            var random = new System.Random(source.GetInstanceID());
            int grainLimit = clothing ? 240 : MaxGrainsPerRenderer;
            float density = clothing ? 370f : GrainsPerSquareMeter;
            int grains = 0;
            int first = random.Next(Mathf.Max(1, indices.Length / 3)) * 3;

            for (int i = 0; i < indices.Length && grains < grainLimit; i += 3)
            {
                int offset = (first + i) % indices.Length;
                Vector3 a = transform.TransformPoint(points[indices[offset]]);
                Vector3 b = transform.TransformPoint(points[indices[offset + 1]]);
                Vector3 c = transform.TransformPoint(points[indices[offset + 2]]);
                Vector3 cross = Vector3.Cross(b - a, c - a);
                float doubleArea = cross.magnitude;
                if (doubleArea <= 0.000001f || cross.y < doubleArea * 0.42f)
                {
                    continue;
                }

                Vector3 normal = cross / doubleArea;
                int count = Mathf.Min(16, Mathf.FloorToInt(doubleArea * 0.5f * density
                    + (float)random.NextDouble()));
                for (int j = 0; j < count && grains < grainLimit; j++)
                {
                    float u = Mathf.Sqrt((float)random.NextDouble());
                    float v = (float)random.NextDouble();
                    Vector3 point = a * (1f - u) + b * (u * (1f - v)) + c * (u * v);
                    Vector3 origin = point + Vector3.up * 0.04f;
                    if ((suppressIndoorSnow && SnowSurface.IsInIndoorVolume(origin))
                        || Physics.Raycast(origin, Vector3.up, 30f, roofMask, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }

                    float radius = Mathf.Lerp(0.0025f, clothing ? 0.005f : 0.006f, (float)random.NextDouble());
                    radius = Mathf.Min(radius, DistanceToEdge(point, a, b, c) * 0.9f);
                    if (radius < 0.0014f)
                    {
                        continue;
                    }

                    SnowGrain.Add(point, normal, radius, vertices, normals, triangles);
                    grains++;
                }
            }

            Matrix4x4 normalToLocal = transform.localToWorldMatrix.transpose;
            for (int i = 0; i < vertices.Count; i++)
            {
                vertices[i] = transform.InverseTransformPoint(vertices[i]);
                normals[i] = normalToLocal.MultiplyVector(normals[i]).normalized;
            }
        }

        private static float DistanceToEdge(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
        {
            float ab = Vector3.Cross(b - a, point - a).magnitude / (b - a).magnitude;
            float bc = Vector3.Cross(c - b, point - b).magnitude / (c - b).magnitude;
            float ca = Vector3.Cross(a - c, point - c).magnitude / (a - c).magnitude;
            return Mathf.Min(ab, Mathf.Min(bc, ca));
        }

        public bool Update(int layers, int maxLayers)
        {
            if (_source == null || _renderer == null || (_slot != null && _slot.Model != _model))
            {
                return false;
            }

            int grains = _indices.Length / SnowGrain.IndicesPerGrain * layers / maxLayers;
            if (grains != _visibleGrains)
            {
                _visibleGrains = grains;
                _mesh.SetIndices(_indices, 0, grains * SnowGrain.IndicesPerGrain,
                    MeshTopology.Triangles, 0, false);
            }

            _renderer.enabled = grains > 0 && _source.enabled && !_source.forceRenderingOff;
            return true;
        }

        public void Dispose()
        {
            if (_renderer != null)
            {
                UnityEngine.Object.Destroy(_renderer.gameObject);
            }

            UnityEngine.Object.Destroy(_mesh);
        }
    }
}
