using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    internal sealed partial class VoxelSnowArea
    {
        private const float PreviewHalfWidth = 165f;
        private const int ChunksPerPreview = 3;
        private const int PreviewStepsPerChunk = 3;
        private const int PreviewSteps = ChunksPerPreview * PreviewStepsPerChunk;
        private const int PreviewWidth = PreviewSteps + 1;
        private const int PreviewPointCount = PreviewWidth * PreviewWidth;
        private const float PreviewSize = ChunkSize * ChunksPerPreview;
        private const float PreviewSpacing = PreviewSize / PreviewSteps;
        private const float PreviewNearRadius = PreviewSize * 2f;
        private const float PreviewRouteHalfWidth = PreviewSize * 2f;
        private const int PreviewSampleCount = PreviewPointCount + PreviewSteps * PreviewSteps;
        private const int PreviewColumnBudget = 256;
        private const double PreviewSampleSeconds = 0.002;
        private const int CountdownPreviewBudget = 1536;
        private const double CountdownPreviewSeconds = 0.008;
        private const float PreviewRecheckSeconds = 0.15f;
        private const byte SurfaceNorth = 1;
        private const byte SurfaceEast = 2;
        private const byte SurfaceSouth = 4;
        private const byte SurfaceWest = 8;

        private readonly Dictionary<Vector2Int, GroundPreview> _groundPreviews =
            new Dictionary<Vector2Int, GroundPreview>();
        private readonly List<Vector2Int> _expiredPreviews = new List<Vector2Int>();
        private GroundPreview _previewSampling;
        private float _nextPreviewSearch;
        private float _nextPreviewSweep;

        private Material _distantMaterial;

        private void UpdateGroundPreview(Vector3 local, Vector3 ahead, Vector3 direction, float lead,
            float depth, bool fullyAccumulated, float now, bool preparing)
        {
            if (now >= _nextPreviewSweep)
            {
                _nextPreviewSweep = now + 1f;
                TrimGroundPreview(local, ahead);
            }

            if (now >= _nextPreviewSearch)
            {
                _previewSampling = FindGroundPreview(local, ahead, direction, lead);
                _nextPreviewSearch = now + PreviewRecheckSeconds;
            }

            int remaining = preparing ? CountdownPreviewBudget : PreviewColumnBudget;
            double seconds = preparing ? CountdownPreviewSeconds : PreviewSampleSeconds;
            long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * seconds);
            int finished = 0;
            int maxTiles = preparing ? 8 : 2;
            while (_previewSampling != null && finished < maxTiles && remaining > 0)
            {
                if (!SampleGroundPreview(_previewSampling, ref remaining, deadline))
                {
                    break;
                }

                var ready = _previewSampling;
                _previewSampling = null;
                MarkPreviewNeighborsDirty(ready.Key);
                RebuildGroundPreview(ready, depth);
                finished++;

                _nextPreviewSearch = now;
                if (finished < maxTiles && remaining > 0 && Stopwatch.GetTimestamp() < deadline)
                {
                    _previewSampling = FindGroundPreview(local, ahead, direction, lead);
                    _nextPreviewSearch = now + PreviewRecheckSeconds;
                }
            }

            if (finished >= maxTiles)
            {
                return;
            }

            GroundPreview refresh = null;
            float priority = float.PositiveInfinity;
            foreach (var tile in _groundPreviews.Values)
            {
                if (!tile.Ready || (!tile.Dirty && !NeedsDepthRefresh(tile.Depth, depth, DistantDepthStep,
                    fullyAccumulated)))
                {
                    continue;
                }

                float distance = (PreviewCenter(tile.Key) - new Vector2(local.x, local.z)).sqrMagnitude;
                float score = tile.Dirty ? distance : distance + 4f * PreviewHalfWidth * PreviewHalfWidth;
                if (score < priority)
                {
                    refresh = tile;
                    priority = score;
                }
            }

            if (refresh != null)
            {
                RebuildGroundPreview(refresh, depth);
            }
        }

        private GroundPreview FindGroundPreview(Vector3 local, Vector3 ahead, Vector3 direction, float lead)
        {
            int minX = Mathf.CeilToInt((ahead.x - PreviewHalfWidth) / PreviewSize - 0.5f);
            int maxX = Mathf.FloorToInt((ahead.x + PreviewHalfWidth) / PreviewSize - 0.5f);
            int minZ = Mathf.CeilToInt((ahead.z - PreviewHalfWidth) / PreviewSize - 0.5f);
            int maxZ = Mathf.FloorToInt((ahead.z + PreviewHalfWidth) / PreviewSize - 0.5f);
            Vector2 position = new Vector2(local.x, local.z);
            Vector2Int best = default;
            float priority = float.PositiveInfinity;

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var key = new Vector2Int(x, z);
                    if (_groundPreviews.TryGetValue(key, out var existing) && existing.Ready)
                    {
                        continue;
                    }

                    float score = PreviewPriority(PreviewCenter(key) - position, direction, lead, _streamRadius);
                    if (score < priority)
                    {
                        priority = score;
                        best = key;
                    }
                }
            }

            if (float.IsPositiveInfinity(priority))
            {
                return null;
            }

            if (!_groundPreviews.TryGetValue(best, out var tile))
            {
                tile = new GroundPreview(best);
                _groundPreviews.Add(best, tile);
            }

            return tile;
        }

        private static Vector2 PreviewCenter(Vector2Int key)
        {
            return new Vector2((key.x + 0.5f) * PreviewSize, (key.y + 0.5f) * PreviewSize);
        }

        private static float PreviewPriority(Vector2 offset, Vector3 direction, float lead, float streamRadius)
        {
            float distance = offset.sqrMagnitude;
            if (lead < ImmediateRadius || distance <= PreviewNearRadius * PreviewNearRadius)
            {
                return distance;
            }

            float forward = offset.x * direction.x + offset.y * direction.z;
            float across = Mathf.Max(0f, distance - forward * forward);
            if (forward > 0f && forward <= streamRadius + StreamingExtension
                && across <= PreviewRouteHalfWidth * PreviewRouteHalfWidth)
            {
                return distance;
            }

            return distance + PreviewHalfWidth * PreviewHalfWidth;
        }

        private bool SampleGroundPreview(GroundPreview tile, ref int remaining, long deadline)
        {
            while (!tile.Ready && remaining > 0 && Stopwatch.GetTimestamp() < deadline)
            {
                remaining--;
                int index = tile.NextSample++;
                if (index < PreviewPointCount)
                {
                    tile.Points[index] = SamplePreviewPoint(tile.Key, index % PreviewWidth, index / PreviewWidth);
                }
                else
                {
                    int face = index - PreviewPointCount;
                    int x = face % PreviewSteps;
                    int z = face / PreviewSteps;
                    int a = z * PreviewWidth + x;
                    bool northWest = tile.Points[a].Valid;
                    bool northEast = tile.Points[a + 1].Valid;
                    bool southWest = tile.Points[a + PreviewWidth].Valid;
                    bool southEast = tile.Points[a + PreviewWidth + 1].Valid;
                    if ((northWest && northEast) || (southWest && southEast)
                        || (northWest && southWest) || (northEast && southEast))
                    {
                        var center = SamplePreviewPoint(tile.Key, x + 0.5f, z + 0.5f);
                        tile.Points[index] = center;
                        tile.Faces[face] = SupportedSurfaceWedges(center, tile.Points[a], tile.Points[a + 1],
                            tile.Points[a + PreviewWidth], tile.Points[a + PreviewWidth + 1]);
                    }
                }
            }

            return tile.Ready;
        }

        private static byte SupportedSurfaceWedges(SnowCoverSample center, SnowCoverSample northWest,
            SnowCoverSample northEast, SnowCoverSample southWest, SnowCoverSample southEast, float tolerance = 0.10f)
        {
            bool nw = center.Fits(northWest, tolerance);
            bool ne = center.Fits(northEast, tolerance);
            bool sw = center.Fits(southWest, tolerance);
            bool se = center.Fits(southEast, tolerance);
            return (byte)((nw && ne ? SurfaceNorth : 0) | (ne && se ? SurfaceEast : 0)
                | (se && sw ? SurfaceSouth : 0) | (sw && nw ? SurfaceWest : 0));
        }

        private SnowCoverSample SamplePreviewPoint(Vector2Int key, float x, float z)
        {
            var point = new Vector3((key.x * PreviewSteps + x) * PreviewSpacing, 0f,
                (key.y * PreviewSteps + z) * PreviewSpacing);
            Column column = SampleColumn(_origin + point);
            if (!column.Exposed && column.HasUpperLevel)
            {
                // The broad field has no doorway drift, so it always shows the exposed upper level.
                column = UpperLevel(column);
            }

            point.y = column.GroundHeight;
            return new SnowCoverSample(point, column.Exposed ? column.Normal : Vector3.zero);
        }

        private void RebuildGroundPreview(GroundPreview tile, float depth)
        {
            tile.Depth = depth;
            tile.Dirty = false;
            _triangles.Clear();
            if (depth > 0f)
            {
                for (int z = 0; z < PreviewSteps; z++)
                {
                    for (int x = 0; x < PreviewSteps; x++)
                    {
                        var key = new Vector2Int(tile.Key.x * ChunksPerPreview + x / PreviewStepsPerChunk,
                            tile.Key.y * ChunksPerPreview + z / PreviewStepsPerChunk);
                        int face = z * PreviewSteps + x;
                        byte wedges = tile.Faces[face];
                        if (wedges != 0
                            && (!_chunks.TryGetValue(key, out var detailed) || !detailed.Active))
                        {
                            int a = z * PreviewWidth + x;
                            int b = a + PreviewWidth;
                            int center = PreviewPointCount + face;
                            AddSupportedTriangle(wedges, SurfaceNorth, a + 1, a, center);
                            AddSupportedTriangle(wedges, SurfaceEast, b + 1, a + 1, center);
                            AddSupportedTriangle(wedges, SurfaceSouth, b, b + 1, center);
                            AddSupportedTriangle(wedges, SurfaceWest, a, b, center);
                        }
                    }
                }
            }

            if (_triangles.Count == 0)
            {
                ReleaseGroundPreviewMesh(tile);
                return;
            }

            _vertices.Clear();
            _normals.Clear();
            for (int i = 0; i < tile.Points.Length; i++)
            {
                SnowCoverSample sample = tile.Points[i];
                Vector3 point = sample.Position;
                float feather = i < PreviewPointCount ? PreviewFeather(tile, i % PreviewWidth, i / PreviewWidth)
                    : tile.Faces[i - PreviewPointCount] == 15 ? 1f : 0.55f;
                feather *= FireMeltFactor(point.x, point.z);

                // On frozen lake ice only a shore bank (FrozenLake.cs). Points are relative to the area origin.
                feather *= FrozenIce.SnowShare(_origin.x + point.x, _origin.z + point.z, _origin.y + point.y);
                point.y +=Mathf.Lerp(-0.025f, 0.006f, feather)
                    + depth * feather * GroundVariation(point.x, point.z);
                _vertices.Add(point);
                _normals.Add(sample.Normal);
            }

            if (tile.Mesh == null)
            {
                tile.Mesh = new Mesh { name = "Winter Outbreak distant ground snow" };
                tile.Mesh.MarkDynamic();
                tile.Object = new GameObject("Winter Outbreak distant ground snow");
                tile.Object.transform.position = _origin;
                tile.Object.AddComponent<MeshFilter>().sharedMesh = tile.Mesh;
                var renderer = tile.Object.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = GetDistantMaterial();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            tile.Mesh.SetVertices(_vertices);
            tile.Mesh.SetTriangles(_triangles, 0);
            tile.Mesh.SetNormals(_normals);

            // Distant tiles never know about cover, so wind ripples show at full strength on them.
            _exposure.Clear();
            for (int i = 0; i < _vertices.Count; i++)
            {
                _exposure.Add(OpenSnow);
            }

            tile.Mesh.SetColors(_exposure);
            if (_prints != null)
            {
                // Distant tiles share the print material but are never pressed: an explicit zero limit.
                _printLimits.Clear();
                PadPrintLimits();
                tile.Mesh.SetUVs(3, _printLimits);
            }
        }

        private void AddSupportedTriangle(byte wedges, byte side, int a, int b, int center)
        {
            if ((wedges & side) != 0)
            {
                _triangles.Add(a);
                _triangles.Add(b);
                _triangles.Add(center);
            }
        }

        private float PreviewFeather(GroundPreview tile, int x, int z)
        {
            int supported = 0;
            for (int dz = -1; dz <= 0; dz++)
            {
                for (int dx = -1; dx <= 0; dx++)
                {
                    int faceX = x + dx;
                    int faceZ = z + dz;
                    int offsetX = faceX < 0 ? -1 : faceX >= PreviewSteps ? 1 : 0;
                    int offsetZ = faceZ < 0 ? -1 : faceZ >= PreviewSteps ? 1 : 0;
                    var source = tile;
                    if (offsetX != 0 || offsetZ != 0)
                    {
                        var key = tile.Key + new Vector2Int(offsetX, offsetZ);
                        if (!_groundPreviews.TryGetValue(key, out source) || !source.Ready)
                        {
                            supported++;
                            continue;
                        }

                        faceX -= offsetX * PreviewSteps;
                        faceZ -= offsetZ * PreviewSteps;
                    }

                    int sides = (dx == 0 ? SurfaceWest : SurfaceEast) | (dz == 0 ? SurfaceNorth : SurfaceSouth);
                    if ((source.Faces[faceZ * PreviewSteps + faceX] & sides) == sides)
                    {
                        supported++;
                    }
                }
            }

            return Mathf.SmoothStep(0f, 1f, (supported - 2f) * 0.5f);
        }

        private void MarkPreviewNeighborsDirty(Vector2Int key)
        {
            for (int z = -1; z <= 1; z++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    if ((x != 0 || z != 0)
                        && _groundPreviews.TryGetValue(key + new Vector2Int(x, z), out var tile) && tile.Ready)
                    {
                        tile.Dirty = true;
                    }
                }
            }
        }

        private void MarkPreviewDirty(Vector2Int chunkKey, bool published = false)
        {
            Vector2Int key = PreviewKeyForChunk(chunkKey);
            if (_groundPreviews.TryGetValue(key, out var tile) && tile.Ready)
            {
                if (published && tile.Mesh != null)
                {
                    // Swap out the matching part of the coarse mesh in the frame detail arrives.
                    RebuildGroundPreview(tile, tile.Depth);
                }
                else
                {
                    tile.Dirty = true;
                }
            }
        }

        private float DetailStartingDepth(Vector2Int chunkKey, float depth)
        {
            Vector2Int key = PreviewKeyForChunk(chunkKey);
            if (!_groundPreviews.TryGetValue(key, out var tile) || tile.Mesh == null || tile.Depth >= depth)
            {
                return depth;
            }

            int x = (chunkKey.x - key.x * ChunksPerPreview) * PreviewStepsPerChunk;
            int z = (chunkKey.y - key.y * ChunksPerPreview) * PreviewStepsPerChunk;
            for (int row = z; row < z + PreviewStepsPerChunk; row++)
            {
                for (int column = x; column < x + PreviewStepsPerChunk; column++)
                {
                    if (tile.Faces[row * PreviewSteps + column] != 0)
                    {
                        return tile.Depth;
                    }
                }
            }

            return depth;
        }

        private static Vector2Int PreviewKeyForChunk(Vector2Int key)
        {
            return new Vector2Int(Mathf.FloorToInt((float)key.x / ChunksPerPreview),
                Mathf.FloorToInt((float)key.y / ChunksPerPreview));
        }

        private void TrimGroundPreview(Vector3 local, Vector3 ahead)
        {
            _expiredPreviews.Clear();
            foreach (var tile in _groundPreviews.Values)
            {
                Vector2 center = PreviewCenter(tile.Key);
                if (IsNearPreviewField(center, local) || IsNearPreviewField(center, ahead))
                {
                    continue;
                }

                if (tile == _previewSampling)
                {
                    _previewSampling = null;
                }

                ReleaseGroundPreviewMesh(tile);
                _expiredPreviews.Add(tile.Key);
            }

            foreach (var key in _expiredPreviews)
            {
                _groundPreviews.Remove(key);
            }
        }

        private static bool IsNearPreviewField(Vector2 center, Vector3 player)
        {
            const float retainedHalfWidth = PreviewHalfWidth + PreviewSize * 2f;
            return Mathf.Abs(center.x - player.x) <= retainedHalfWidth
                && Mathf.Abs(center.y - player.z) <= retainedHalfWidth;
        }

        private static void ReleaseGroundPreviewMesh(GroundPreview tile)
        {
            if (tile.Mesh != null)
            {
                Object.Destroy(tile.Object);
                Object.Destroy(tile.Mesh);
                tile.Object = null;
                tile.Mesh = null;
            }
        }

        private void DisposeGroundPreview()
        {
            foreach (var tile in _groundPreviews.Values)
            {
                ReleaseGroundPreviewMesh(tile);
            }

            _groundPreviews.Clear();
            _previewSampling = null;
            if (_distantMaterial != null)
            {
                Object.Destroy(_distantMaterial);
                _distantMaterial = null;
            }
        }

        private Material GetDistantMaterial()
        {
            if (_distantMaterial == null)
            {
                _distantMaterial = new Material(_material) { name = "Winter Outbreak distant ground snow" };
                _distantMaterial.color = _material.color;
            }

            return _distantMaterial;
        }

        private sealed class GroundPreview
        {
            public readonly Vector2Int Key;
            public readonly SnowCoverSample[] Points = new SnowCoverSample[PreviewSampleCount];
            public readonly byte[] Faces = new byte[PreviewSteps * PreviewSteps];
            public int NextSample;
            public bool Ready => NextSample == PreviewSampleCount;
            public bool Dirty;
            public float Depth;
            public Mesh Mesh;
            public GameObject Object;

            public GroundPreview(Vector2Int key)
            {
                Key = key;
            }
        }
    }
}
