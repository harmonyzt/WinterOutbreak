using System.Collections.Generic;
using UnityEngine;

namespace WinterOutbreak
{
    internal sealed partial class VoxelSnowArea
    {
        private const int MaxPropModels = 48;
        private readonly Dictionary<Collider, MeshFilter[]> _propModels = new Dictionary<Collider, MeshFilter[]>();
        private readonly Dictionary<MeshFilter, PropSnowSurface> _propSurfaces = new Dictionary<MeshFilter, PropSnowSurface>();
        private readonly Queue<MeshFilter> _propSurfaceOrder = new Queue<MeshFilter>();
        private readonly List<Transform> _propNodes = new List<Transform>(96);
        private readonly List<MeshFilter> _propFilters = new List<MeshFilter>();

        private bool TrySampleProp(Collider collider, Vector3 ground, float scanHeight,
            out PropSnowSurface surface, out SnowCoverSample sample)
        {
            surface = null;
            sample = default;
            float low = ground.y + 0.07f;
            float high = ground.y + scanHeight;
            bool anyModel = false;
            MeshFilter unusable = null;
            var filters = GetPropModels(collider);
            foreach (var filter in filters)
            {
                if (filter == null)
                {
                    continue;
                }

                if (!_propSurfaces.TryGetValue(filter, out var model))
                {
                    Bounds bounds = filter.GetComponent<MeshRenderer>().bounds;
                    if (ground.x < bounds.min.x || ground.x > bounds.max.x
                        || ground.z < bounds.min.z || ground.z > bounds.max.z)
                    {
                        continue;
                    }

                    model = PropSnowSurface.Create(filter);
                    if (_propSurfaces.Count >= MaxPropModels)
                    {
                        _propSurfaces.Remove(_propSurfaceOrder.Dequeue());
                    }

                    _propSurfaces.Add(filter, model);
                    _propSurfaceOrder.Enqueue(filter);
                }

                if (model == null)
                {
                    unusable ??= filter;
                    continue;
                }

                anyModel = true;
                if (model.TrySample(ground.x, ground.z, low, high, 0.32f, out var hit))
                {
                    low = hit.Position.y;
                    surface = model;
                    sample = hit;
                }
            }

            // A prop with no usable render mesh gets no cap at all and shows only EFT's own snow
            if (surface == null && !anyModel && (filters.Length == 0 || unusable != null))
            {
                string reason = filters.Length == 0 ? "no matching render mesh"
                    : PropSnowSurface.DescribeUnusable(unusable);
                CountCapSkip(collider, reason);
            }

            return surface != null;
        }

        // Props with no usable mesh, handed once each to the snow coats (SnowCoats.cs), which draw the game's own mesh
        // again with snow on its upward faces instead
        private const int MaxUncappedProps = 4096;
        private readonly HashSet<Collider> _uncappedProps = new HashSet<Collider>();
        private readonly Queue<Collider> _newUncappedProps = new Queue<Collider>();

        internal void TakeUncappedProps(List<Collider> into)
        {
            while (_newUncappedProps.Count > 0)
            {
                into.Add(_newUncappedProps.Dequeue());
            }
        }

        private void CountCapSkip(Collider collider, string reason)
        {
            if (_uncappedProps.Count < MaxUncappedProps && _uncappedProps.Add(collider))
            {
                _newUncappedProps.Enqueue(collider);
            }
        }

        private MeshFilter[] GetPropModels(Collider collider)
        {
            if (!_propModels.TryGetValue(collider, out var filters))
            {
                filters = FindPropModels(collider);
                _propModels.Add(collider, filters);
            }

            return filters;
        }

        private MeshFilter[] FindPropModels(Collider collider)
        {
            const int maxNodes = 96;
            Bounds support = collider.bounds;
            support.Expand(0.24f);
            Transform part = collider.transform;
            for (int level = 0; level < 4 && part != null; level++, part = part.parent)
            {
                _propNodes.Clear();
                _propFilters.Clear();
                _propNodes.Add(part);
                for (int i = 0; i < _propNodes.Count; i++)
                {
                    Transform node = _propNodes[i];
                    if (node.TryGetComponent<MeshFilter>(out var filter)
                        && node.TryGetComponent<MeshRenderer>(out var renderer)
                        && renderer.enabled && renderer.gameObject.activeInHierarchy
                        && renderer.bounds.Intersects(support) && IsHighestLod(renderer, part))
                    {
                        _propFilters.Add(filter);
                    }

                    if (_propNodes.Count + node.childCount > maxNodes)
                    {
                        // Stop before a collider lookup can turn into a search of an entire map group
                        return System.Array.Empty<MeshFilter>();
                    }

                    for (int child = 0; child < node.childCount; child++)
                    {
                        _propNodes.Add(node.GetChild(child));
                    }
                }

                if (_propFilters.Count != 0)
                {
                    return _propFilters.ToArray();
                }
            }

            return System.Array.Empty<MeshFilter>();
        }

        private static bool IsHighestLod(MeshRenderer renderer, Transform root)
        {
            for (Transform node = renderer.transform; node != root.parent; node = node.parent)
            {
                if (!node.TryGetComponent<LODGroup>(out var group))
                {
                    continue;
                }

                var lods = group.GetLODs();
                for (int i = 0; i < lods.Length; i++)
                {
                    foreach (var member in lods[i].renderers)
                    {
                        if (member == renderer)
                        {
                            return i == 0;
                        }
                    }
                }
            }

            return true;
        }

        private void ClearPropSurfaceCache()
        {
            _propModels.Clear();
            _propSurfaces.Clear();
            _propSurfaceOrder.Clear();
            _propNodes.Clear();
            _propFilters.Clear();
        }
    }

    internal sealed class PropSnowSurface
    {
        private const int BinCount = 16;
        private const int MaxVertices = 24000;
        private const int MaxTriangles = 16000;
        private const int MaxBinEntries = 64000;
        private readonly Bounds _bounds;
        private readonly Vector3[] _vertices;
        private readonly List<Triangle>[] _bins = new List<Triangle>[BinCount * BinCount];
        private int _entries;

        internal string Name { get; }

        private PropSnowSurface(string name, Bounds bounds, Vector3[] vertices)
        {
            Name = name;
            _bounds = bounds;
            _vertices = vertices;
        }

        // Why Create returned null for this mesh, in the same order as its checks
        internal static string DescribeUnusable(MeshFilter filter)
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null)
            {
                return "no mesh";
            }

            if (!mesh.isReadable)
            {
                return $"unreadable mesh {mesh.name}";
            }

            if (mesh.vertexCount > MaxVertices)
            {
                return $"{mesh.name} over {MaxVertices} vertices";
            }

            return $"{mesh.name} over {MaxTriangles} triangles or flat";
        }

        internal static PropSnowSurface Create(MeshFilter filter)
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null || !mesh.isReadable || mesh.vertexCount > MaxVertices)
            {
                return null;
            }

            var renderer = filter.GetComponent<MeshRenderer>();
            var materials = renderer.sharedMaterials;
            int indexCount = 0;
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                uint count = mesh.GetIndexCount(sub);
                if (count > MaxTriangles * 3 - indexCount)
                {
                    return null;
                }

                indexCount += (int)count;
            }

            Bounds bounds = renderer.bounds;
            if (bounds.size.x < 0.001f || bounds.size.z < 0.001f)
            {
                return null;
            }

            Vector3[] vertices = mesh.vertices;
            var toWorld = renderer.localToWorldMatrix;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = toWorld.MultiplyPoint3x4(vertices[i]);
            }

            bool mirrored = toWorld.determinant < 0f;
            var surface = new PropSnowSurface(mesh.name, bounds, vertices);
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles || materials.Length == 0
                    || !IsOpaque(materials[Mathf.Min(sub, materials.Length - 1)]))
                {
                    continue;
                }

                int[] indices = mesh.GetTriangles(sub);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int b = mirrored ? indices[i + 2] : indices[i + 1];
                    int c = mirrored ? indices[i + 1] : indices[i + 2];
                    if (!surface.AddTriangle(indices[i], b, c))
                    {
                        return null;
                    }
                }
            }

            return surface._entries == 0 ? null : surface;
        }

        private static bool IsOpaque(Material material)
        {
            return material != null && material.renderQueue < 2450
                && !material.IsKeywordEnabled("_ALPHATEST_ON")
                && material.GetTag("RenderType", false, "Opaque") == "Opaque";
        }

        private bool AddTriangle(int a, int b, int c)
        {
            Vector3 p = _vertices[a];
            Vector3 q = _vertices[b];
            Vector3 r = _vertices[c];
            Vector3 normal = Vector3.Cross(q - p, r - p).normalized;
            if (normal.y < 0.32f)
            {
                return true;
            }

            Vector3 min = _bounds.min;
            Vector3 size = _bounds.size;
            int left = Bin(Mathf.Min(p.x, Mathf.Min(q.x, r.x)), min.x, size.x);
            int right = Bin(Mathf.Max(p.x, Mathf.Max(q.x, r.x)), min.x, size.x);
            int near = Bin(Mathf.Min(p.z, Mathf.Min(q.z, r.z)), min.z, size.z);
            int far = Bin(Mathf.Max(p.z, Mathf.Max(q.z, r.z)), min.z, size.z);
            _entries += (right - left + 1) * (far - near + 1);
            if (_entries > MaxBinEntries)
            {
                return false;
            }

            var triangle = new Triangle(a, b, c, normal);
            for (int z = near; z <= far; z++)
            {
                for (int x = left; x <= right; x++)
                {
                    (_bins[z * BinCount + x] ??= new List<Triangle>()).Add(triangle);
                }
            }

            return true;
        }

        internal bool TrySample(float x, float z, float low, float high, float minimumNormal, out SnowCoverSample sample)
        {
            sample = default;
            Vector3 min = _bounds.min;
            Vector3 max = _bounds.max;
            if (x < min.x || x > max.x || z < min.z || z > max.z)
            {
                return false;
            }

            int row = Bin(z, min.z, max.z - min.z);
            int column = Bin(x, min.x, max.x - min.x);
            var triangles = _bins[row * BinCount + column];
            if (triangles == null)
            {
                return false;
            }

            foreach (var triangle in triangles)
            {
                if (triangle.Normal.y >= minimumNormal
                    && TryProject(x, z, _vertices[triangle.A], _vertices[triangle.B], _vertices[triangle.C], out float y)
                    && y >= low && y <= high && y >= min.y - 0.01f && y <= max.y + 0.01f)
                {
                    low = y;
                    sample = new SnowCoverSample(new Vector3(x, y, z), triangle.Normal);
                }
            }

            return sample.Valid;
        }

        internal static bool TryProject(float x, float z, Vector3 a, Vector3 b, Vector3 c, out float y)
        {
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            float det = ab.x * ac.z - ab.z * ac.x;
            y = 0f;
            if (Mathf.Abs(det) < 0.00000001f)
            {
                return false;
            }

            float dx = x - a.x;
            float dz = z - a.z;
            float u = (dx * ac.z - dz * ac.x) / det;
            float v = (ab.x * dz - ab.z * dx) / det;
            const float edgeTolerance = 0.0001f;
            if (u < -edgeTolerance || v < -edgeTolerance || u + v > 1f + edgeTolerance)
            {
                return false;
            }

            y = a.y + u * ab.y + v * ac.y;
            return true;
        }

        private static int Bin(float coordinate, float min, float size)
        {
            return Mathf.Clamp((int)((coordinate - min) / size * BinCount), 0, BinCount - 1);
        }

        private readonly struct Triangle
        {
            public readonly int A;
            public readonly int B;
            public readonly int C;
            public readonly Vector3 Normal;

            public Triangle(int a, int b, int c, Vector3 normal)
            {
                A = a;
                B = b;
                C = c;
                Normal = normal;
            }
        }
    }
}
