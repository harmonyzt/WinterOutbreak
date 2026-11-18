using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace WinterOutbreak
{
    internal sealed partial class VoxelSnowArea
    {
        private const int PropDivisions = 6;
        private const int RockDivisions = 10;
        private const int CapHalo = 2;
        private const int CreasePasses = 3;
        private const int RaisedStackHits = 4;
        private const float PropDepthShare = 0.35f;
        private const float RockDepthShare = 0.30f;
        private const float MaxRaisedLift = 0.10f;
        private const int MaxCapWidth = RockDivisions + 1 + CapHalo * 2;
        private const float MinimumCapNormal = 0.68f;
        private const float MinimumRockNormal = 0.56f;
        private const int RaisedColumnBudget = 32;
        private const int CountdownRaisedColumnBudget = 1024;
        private const double RaisedSampleSeconds = 0.00045;

        private readonly Dictionary<Collider, bool> _raisedDustOnly = new Dictionary<Collider, bool>();
        private readonly Dictionary<Collider, bool> _rockSupports = new Dictionary<Collider, bool>();
        private readonly Dictionary<Collider, OutdoorPropKind> _outdoorProps = new Dictionary<Collider, OutdoorPropKind>();
        private readonly Dictionary<Collider, bool> _pavementSupports = new Dictionary<Collider, bool>();
        private static readonly string[] PavementNames =
        {
            "mall_border_trail", "plate_single", "plate_pack", "plates_pack"
        };
        private static readonly string[] VehicleNames =
        {
            "solaris", "toyota", "logan_", "cargo_gazel", "rimo_evo6", "forklift", "fork_lift", "truck", "trailer",
            "vehicle"
        };
        private static readonly string[] UtilityEquipmentNames =
        {
            "electric_pole", "power_transformer", "pipe_gas_", "gas_pipeline_", "middle_tube_ttp", "pipe_termoinsulation",
            "teplotras"
        };
        private static readonly string[] OutdoorClutterNames =
        {
            "tree_", "_tree", "trunk", "lamp", "lightpole", "light_pole", "street_light", "bollard",
            "wheel", "tyre", "tire", "barrel", "pallet", "crate", "barrier", "jersey", "cardboard",
            "dumpster", "wood_box_crash", "oldboardsheap", "sandbags", "metal_sheet_standing",

            // Debris the ground snow should lie through
            "oldboardspile", "barn_crahs", "barn_crash"
        };
        private readonly SnowCoverSample[] _capSamples = new SnowCoverSample[MaxCapWidth * MaxCapWidth];
        private readonly byte[] _capFaces = new byte[(MaxCapWidth - 1) * (MaxCapWidth - 1)];
        private readonly SnowCoverSample[] _capCenters = new SnowCoverSample[(MaxCapWidth - 1) * (MaxCapWidth - 1)];
        private readonly List<SnowImpression> _raisedImpressions = new List<SnowImpression>();

        // During the deployment countdown the caller passes a longer deadline and a bigger column budget.
        private void SampleRaisedChunk(Chunk chunk, long rebuildDeadline, bool countdown = false)
        {
            long deadline = countdown ? rebuildDeadline
                : Math.Min(rebuildDeadline, Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * RaisedSampleSeconds));
            int budget = countdown ? CountdownRaisedColumnBudget : RaisedColumnBudget;
            int end = Math.Min(chunk.RaisedNextSample + budget, ChunkWidth * ChunkWidth);
            while (chunk.RaisedNextSample < end && Stopwatch.GetTimestamp() < deadline)
            {
                int index = chunk.RaisedNextSample++;
                Column column = chunk.Columns[index];
                if (!column.HasGround)
                {
                    continue;
                }

                int x = chunk.X + index % ChunkWidth;
                int z = chunk.Z + index / ChunkWidth;
                var patch = TimedRaisedSurface(GroundPoint(x, z, column.GroundHeight), column.MoundStrength > 0f);
                if (patch.Faces != null && column.HasRoof && Mathf.Abs(PatchHeight(patch) - column.RoofHeight) < RoofJoinTolerance)
                {
                    // The cap lies on this column's stored open roof
                    patch = new RaisedSnowPatch(patch.Points, patch.Faces, patch.Support, patch.ModelName, patch.Divisions,
                        patch.Centers, true);
                    if (!_groundJoinedSupports.Contains(patch.Support)
                        && FlushSides(patch, x, z) != 0)
                    {
                        _groundJoinedSupports.Add(patch.Support);

                        // Caps of this roof built before now kept the thin cover: rebuild only the chunks that hold
                        // them.
                        foreach (var other in _chunks.Values)
                        {
                            if (other.RaisedReady && other.RaisedSurfaces != null && HoldsSupport(other, patch.Support))
                            {
                                other.RaisedDirty = true;
                            }
                        }
                    }
                }

                if (patch.HasSupport)
                {
                    chunk.RaisedSurfaces ??= new Dictionary<int, RaisedSnowPatch>();
                    chunk.RaisedSurfaces.Add(index, patch);
                }
            }

            if (chunk.RaisedReady && chunk.RaisedSurfaces != null)
            {
                chunk.RaisedDirty = true;
                RefreshRaisedNeighbor(chunk.Key + Vector2Int.left);
                RefreshRaisedNeighbor(chunk.Key + Vector2Int.right);
                RefreshRaisedNeighbor(chunk.Key + Vector2Int.down);
                RefreshRaisedNeighbor(chunk.Key + Vector2Int.up);
            }
        }

        private void RefreshRaisedNeighbor(Vector2Int key)
        {
            if (_chunks.TryGetValue(key, out var neighbor) && neighbor.RaisedReady && neighbor.RaisedSurfaces != null)
            {
                neighbor.RaisedDirty = true;
            }
        }

        private RaisedSnowPatch SampleRaisedSurface(Vector3 ground, bool hasGroundMound)
        {
            const float scanHeight = 12f;
            const float minimumRise = 0.07f;
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            Vector3 start = ground + Vector3.up * scanHeight;
            float reach = scanHeight - minimumRise;

            // Shelter still ends the search.
            for (int attempt = 0; attempt < RaisedStackHits && reach > 0f; attempt++)
            {
                if (!Physics.Raycast(start, Vector3.down, out var hit, reach, _solidMask, triggers)
                    && !Physics.Raycast(start, Vector3.down, out hit, reach, _fallbackGroundMask, triggers))
                {
                    return default;
                }

                var patch = SampleRaisedSupport(hit, ground, hasGroundMound, scanHeight, out bool sheltered);
                if (patch.HasSupport || sheltered)
                {
                    return patch;
                }

                float step = hit.distance + SweepStep;
                start.y -= step;
                reach -= step;
            }

            return default;
        }

        private RaisedSnowPatch SampleRaisedSupport(RaycastHit hit, Vector3 ground, bool hasGroundMound,
            float scanHeight, out bool sheltered)
        {
            sheltered = false;
            Collider support = hit.collider;
            if (support is TerrainCollider || (hit.rigidbody != null && !hit.rigidbody.isKinematic)
                || (hasGroundMound && GroundMoundStrength(support, ground.y) > 0f)
                || GetOutdoorPropKind(support) == OutdoorPropKind.UtilityEquipment
                || !TrySampleProp(support, ground, scanHeight, out var model, out var seed))
            {
                return default;
            }

            if (IsSheltered(seed.Position + Vector3.up * 0.08f))
            {
                sheltered = true;
                return default;
            }

            bool rock = IsRock(support) || seed.Normal.y < 0.94f;
            float minimumNormal = rock ? MinimumRockNormal : MinimumCapNormal;
            if (IsDustOnly(support) || seed.Normal.y < minimumNormal)
            {
                return SampleRaisedDust(support, model, seed);
            }

            int divisions = rock ? RockDivisions : PropDivisions;
            int width = divisions + 1;
            int sampleWidth = width + CapHalo * 2;
            int faceWidth = sampleWidth - 1;
            float spacing = CellWidth / divisions;
            float halfWidth = divisions * 0.5f + CapHalo;
            float tolerance = rock ? 0.019f : 0.015f;
            for (int z = 0; z < sampleWidth; z++)
            {
                for (int x = 0; x < sampleWidth; x++)
                {
                    TryCapPoint(model, seed, (x - halfWidth) * spacing, (z - halfWidth) * spacing,
                        minimumNormal, out _capSamples[z * sampleWidth + x]);
                }
            }

            GrowCapAcrossCreases(model, sampleWidth, spacing, minimumNormal);

            for (int z = 0; z < faceWidth; z++)
            {
                for (int x = 0; x < faceWidth; x++)
                {
                    int a = z * sampleWidth + x;
                    int index = z * faceWidth + x;
                    var nw = _capSamples[a];
                    var ne = _capSamples[a + 1];
                    var sw = _capSamples[a + sampleWidth];
                    var se = _capSamples[a + sampleWidth + 1];
                    _capFaces[index] = 0;
                    if (((nw.Valid && ne.Valid) || (sw.Valid && se.Valid) || (nw.Valid && sw.Valid)
                        || (ne.Valid && se.Valid))
                        && TryFaceCenter(model, seed, (x + 0.5f - halfWidth) * spacing, (z + 0.5f - halfWidth) * spacing,
                            spacing, nw, ne, sw, se, minimumNormal, out var center))
                    {
                        _capCenters[index] = center;
                        byte wedges = SupportedSurfaceWedges(center, nw, ne, sw, se, tolerance);
                        _capFaces[index] = CheckPropEdges(model, wedges, nw, ne, sw, se, minimumNormal, tolerance);
                    }
                }
            }

            var faces = new byte[divisions * divisions];
            SnowCoverSample[] centers = null;
            bool anyFace = false;
            for (int z = 0; z < divisions; z++)
            {
                Array.Copy(_capFaces, (z + CapHalo) * faceWidth + CapHalo, faces, z * divisions, divisions);
                for (int x = 0; x < divisions; x++)
                {
                    int face = z * divisions + x;
                    byte wedges = faces[face];
                    anyFace |= wedges != 0;
                    if (wedges != 0 && wedges != 15)
                    {
                        centers ??= new SnowCoverSample[faces.Length];
                        centers[face] = _capCenters[(z + CapHalo) * faceWidth + x + CapHalo];
                    }
                }
            }

            if (!anyFace)
            {
                return SampleRaisedDust(support, model, seed);
            }

            var points = new SnowCoverSample[width * width];
            for (int z = 0; z < width; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sampleX = x + CapHalo;
                    int sampleZ = z + CapHalo;
                    var point = _capSamples[sampleZ * sampleWidth + sampleX];
                    if (point.Valid)
                    {
                        float cover = CapFeather(_capFaces, faceWidth, sampleX, sampleZ);
                        Vector3 normal = point.Normal;
                        for (int row = -1; row <= 1; row++)
                        {
                            for (int side = -1; side <= 1; side++)
                            {
                                var nearby = _capSamples[(sampleZ + row) * sampleWidth + sampleX + side];
                                if (point.Fits(nearby, tolerance))
                                {
                                    normal += nearby.Normal;
                                }
                            }
                        }

                        point = new SnowCoverSample(point.Position, normal.normalized) { Cover = cover };
                    }

                    points[z * width + x] = point;
                }
            }

            return new RaisedSnowPatch(points, faces, support, model.Name, divisions, centers);
        }

        private byte CheckPropEdges(PropSnowSurface model, byte wedges, SnowCoverSample nw, SnowCoverSample ne,
            SnowCoverSample sw, SnowCoverSample se, float minimumNormal, float tolerance)
        {
            int supported = 0;
            if ((wedges & SurfaceNorth) != 0 && HasPropEdge(model, nw, ne, minimumNormal, tolerance))
            {
                supported |= SurfaceNorth;
            }

            if ((wedges & SurfaceEast) != 0 && HasPropEdge(model, ne, se, minimumNormal, tolerance))
            {
                supported |= SurfaceEast;
            }

            if ((wedges & SurfaceSouth) != 0 && HasPropEdge(model, sw, se, minimumNormal, tolerance))
            {
                supported |= SurfaceSouth;
            }

            if ((wedges & SurfaceWest) != 0 && HasPropEdge(model, nw, sw, minimumNormal, tolerance))
            {
                supported |= SurfaceWest;
            }

            return (byte)supported;
        }

        private bool HasPropEdge(PropSnowSurface model, SnowCoverSample a, SnowCoverSample b,
            float minimumNormal, float tolerance)
        {
            Vector3 midpoint = (a.Position + b.Position) * 0.5f + _origin;
            return model.TrySample(midpoint.x, midpoint.z, midpoint.y - tolerance, midpoint.y + tolerance,
                minimumNormal, out _);
        }

        private static float CapFeather(byte[] faces, int width, int x, int z)
        {
            const float featherReach = CapHalo + 0.25f;
            float nearest = featherReach;
            for (int row = -CapHalo; row < CapHalo; row++)
            {
                for (int side = -CapHalo; side < CapHalo; side++)
                {
                    if (faces[(z + row) * width + x + side] != 15)
                    {
                        float dx = side + 0.5f;
                        float dz = row + 0.5f;
                        nearest = Mathf.Min(nearest, Mathf.Sqrt(dx * dx + dz * dz));
                    }
                }
            }

            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, featherReach, nearest));
        }

        private bool IsRock(Collider collider)
        {
            if (_rockSupports.TryGetValue(collider, out bool rock))
            {
                return rock;
            }

            for (Transform part = collider.transform; part != null; part = part.parent)
            {
                if (IsRockName(part.name))
                {
                    rock = true;
                    break;
                }
            }

            if (!rock && collider is MeshCollider mesh && mesh.sharedMesh != null)
            {
                rock = IsRockName(mesh.sharedMesh.name);
            }

            _rockSupports.Add(collider, rock);
            return rock;
        }

        private static bool IsRockName(string name)
        {
            return name.IndexOf("rock", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("cliff", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("boulder", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsOutdoorClutter(Collider collider)
        {
            var kind = GetOutdoorPropKind(collider);
            return kind == OutdoorPropKind.Clutter || kind == OutdoorPropKind.Vehicle
                || kind == OutdoorPropKind.UtilityEquipment;
        }

        private bool IsGroundObstruction(Collider collider)
        {
            return GetOutdoorPropKind(collider) != OutdoorPropKind.None;
        }

        private bool IsGroundPavement(Collider collider)
        {
            if (_pavementSupports.TryGetValue(collider, out bool pavement))
            {
                return pavement;
            }

            for (Transform part = collider.transform; part != null && !pavement; part = part.parent)
            {
                pavement = ContainsPropName(part.name, PavementNames);
            }

            if (!pavement && collider is MeshCollider mesh && mesh.sharedMesh != null)
            {
                pavement = ContainsPropName(mesh.sharedMesh.name, PavementNames);
            }

            _pavementSupports.Add(collider, pavement);
            return pavement;
        }

        private OutdoorPropKind GetOutdoorPropKind(Collider collider)
        {
            if (_outdoorProps.TryGetValue(collider, out var kind))
            {
                return kind;
            }

            for (Transform part = collider.transform; part != null && kind != OutdoorPropKind.UtilityRoof; part = part.parent)
            {
                var namedKind = ClassifyOutdoorPropName(part.name);
                if (namedKind > kind)
                {
                    kind = namedKind;
                }
            }

            if (kind != OutdoorPropKind.UtilityRoof && collider is MeshCollider mesh && mesh.sharedMesh != null)
            {
                var namedKind = ClassifyOutdoorPropName(mesh.sharedMesh.name);
                if (namedKind > kind)
                {
                    kind = namedKind;
                }
            }

            if (kind == OutdoorPropKind.None)
            {
                Vector3 size = collider.bounds.size;
                if (size.x <= 0.45f && size.z <= 0.45f)
                {
                    kind = OutdoorPropKind.Clutter;
                }
            }

            _outdoorProps.Add(collider, kind);
            return kind;
        }

        private static OutdoorPropKind ClassifyOutdoorPropName(string name)
        {
            if (name.IndexOf("cabin_reserve", StringComparison.OrdinalIgnoreCase) >= 0
                || name.Equals("Cabin_BALLISTIC_metalthick", StringComparison.OrdinalIgnoreCase))
            {
                return OutdoorPropKind.UtilityRoof;
            }

            if (ContainsPropName(name, UtilityEquipmentNames))
            {
                return OutdoorPropKind.UtilityEquipment;
            }

            if (ContainsPropName(name, VehicleNames))
            {
                return OutdoorPropKind.Vehicle;
            }

            return ContainsPropName(name, OutdoorClutterNames) ? OutdoorPropKind.Clutter : OutdoorPropKind.None;
        }

        private static bool ContainsPropName(string name, string[] candidates)
        {
            foreach (string word in candidates)
            {
                if (name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private enum OutdoorPropKind : byte
        {
            None,
            Clutter,
            Vehicle,
            UtilityEquipment,
            UtilityRoof
        }

        private bool IsDustOnly(Collider collider)
        {
            if (_raisedDustOnly.TryGetValue(collider, out bool dust))
            {
                return dust;
            }

            for (Transform part = collider.transform; part != null; part = part.parent)
            {
                string name = part.name;
                if (name.IndexOf("fence", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("chainlink", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("chain_link", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("barbed", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("razorwire", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("wiremesh", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("grate", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("tire", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("tyre", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    dust = true;
                    break;
                }

                // A cutout can be a full collision plane with only a wire pattern rendered.
                var renderer = part.GetComponent<Renderer>();
                var material = renderer != null ? renderer.sharedMaterial : null;
                if (material != null && (material.GetTag("RenderType", false) == "TransparentCutout"
                    || material.IsKeywordEnabled("_ALPHATEST_ON")))
                {
                    dust = true;
                    break;
                }
            }

            _raisedDustOnly.Add(collider, dust);
            return dust;
        }

        private RaisedSnowPatch SampleRaisedDust(Collider support, PropSnowSurface model, SnowCoverSample hit)
        {
            var grains = new List<SnowCoverSample>(5);
            for (int i = 0; i < 5; i++)
            {
                float seed = i * 23.67f;
                float x = (Mathf.PerlinNoise(hit.Position.x * 7.3f + seed, hit.Position.z * 5.1f) - 0.5f) * CellWidth;
                float z = (Mathf.PerlinNoise(hit.Position.z * 6.7f + seed, hit.Position.x * 8.3f) - 0.5f) * CellWidth;
                if (TryCapPoint(model, hit, x, z, 0.32f, out var point))
                {
                    point.Cover = Mathf.Lerp(0.004f, 0.010f, Mathf.PerlinNoise(seed, hit.Position.x + hit.Position.z));
                    grains.Add(point);
                }
            }

            return grains.Count == 0 ? default : new RaisedSnowPatch(grains.ToArray(), null, support, model.Name, 0);
        }

        // A cap follows one plane from its seed within 4.5 cm
        private void GrowCapAcrossCreases(PropSnowSurface model, int sampleWidth, float spacing, float minimumNormal)
        {
            for (int pass = 0; pass < CreasePasses; pass++)
            {
                bool grew = false;
                for (int z = 0; z < sampleWidth; z++)
                {
                    for (int x = 0; x < sampleWidth; x++)
                    {
                        int index = z * sampleWidth + x;
                        if (_capSamples[index].Valid)
                        {
                            continue;
                        }

                        foreach (var (dx, dz) in FourSides)
                        {
                            int nx = x + dx;
                            int nz = z + dz;
                            if (nx < 0 || nz < 0 || nx >= sampleWidth || nz >= sampleWidth)
                            {
                                continue;
                            }

                            var near = _capSamples[nz * sampleWidth + nx];
                            if (near.Valid && TryCapPoint(model, new SnowCoverSample(near.Position + _origin, near.Normal),
                                -dx * spacing, -dz * spacing, minimumNormal, out var grown))
                            {
                                _capSamples[index] = grown;
                                grew = true;
                                break;
                            }
                        }
                    }
                }

                if (!grew)
                {
                    break;
                }
            }
        }

        private bool TryFaceCenter(PropSnowSurface model, SnowCoverSample seed, float x, float z, float spacing,
            SnowCoverSample nw, SnowCoverSample ne, SnowCoverSample sw, SnowCoverSample se, float minimumNormal,
            out SnowCoverSample center)
        {
            if (TryCapPoint(model, seed, x, z, minimumNormal, out center))
            {
                return true;
            }

            // On a creased face, predict the center from a corner that lies on the same facet.
            float half = spacing * 0.5f;
            return (nw.Valid && TryCapPoint(model, new SnowCoverSample(nw.Position + _origin, nw.Normal), half, half,
                        minimumNormal, out center))
                || (ne.Valid && TryCapPoint(model, new SnowCoverSample(ne.Position + _origin, ne.Normal), -half, half,
                        minimumNormal, out center))
                || (sw.Valid && TryCapPoint(model, new SnowCoverSample(sw.Position + _origin, sw.Normal), half, -half,
                        minimumNormal, out center))
                || (se.Valid && TryCapPoint(model, new SnowCoverSample(se.Position + _origin, se.Normal), -half, -half,
                        minimumNormal, out center));
        }

        private static float RaisedThickness(float snowDepth, bool rock)
        {
            return Mathf.Min(snowDepth * (rock ? RockDepthShare : PropDepthShare), MaxRaisedLift);
        }

        private static float RaisedCenterLift(float snowDepth)
        {
            return Mathf.Min(snowDepth * 0.06f, 0.02f);
        }

        private bool TryCapPoint(PropSnowSurface model, SnowCoverSample seed, float x, float z, float minimumNormal,
            out SnowCoverSample sample)
        {
            const float tolerance = 0.045f;
            float rise = -(seed.Normal.x * x + seed.Normal.z * z) / seed.Normal.y;
            Vector3 expected = seed.Position + new Vector3(x, rise, z);
            if (model.TrySample(expected.x, expected.z, expected.y - tolerance, expected.y + tolerance,
                minimumNormal, out var hit))
            {
                sample = new SnowCoverSample(hit.Position - _origin, hit.Normal);
                return true;
            }

            sample = new SnowCoverSample(expected - _origin, Vector3.zero);
            return false;
        }

        private void AddRaisedSnow(Chunk chunk, float snowDepth)
        {
            if (chunk.RaisedSurfaces == null)
            {
                return;
            }

            float now = Time.time;
            foreach (var entry in chunk.RaisedSurfaces)
            {
                var patch = entry.Value;
                int meltX = chunk.X + entry.Key % ChunkWidth;
                int meltZ = chunk.Z + entry.Key / ChunkWidth;
                if (FireMeltFactor((meltX + 0.5f) * CellWidth, (meltZ + 0.5f) * CellWidth) < 0.5f)
                {
                    continue;
                }

                if (patch.Faces == null)
                {
                    foreach (var point in patch.Points)
                    {
                        SnowGrain.Add(point.Position, point.Normal, point.Cover, _vertices, _normals, _triangles);
                    }

                    continue;
                }

                int cellX = chunk.X + entry.Key % ChunkWidth;
                int cellZ = chunk.Z + entry.Key / ChunkWidth;

                // No caps on a stair flight or the cells around it
                if (NearStairFlight(cellX, cellZ) || IsTrenchHidden(cellX, cellZ))
                {
                    continue;
                }

                var neighbors = GetRaisedNeighbors(cellX, cellZ);
                int divisions = patch.Divisions;
                int width = divisions + 1;
                var center = new Vector2((cellX + 0.5f) * CellWidth, (cellZ + 0.5f) * CellWidth);
                CollectRaisedImpressions(chunk, patch.Support, center);
                int start = _vertices.Count;
                int flushSides = GroundFlushSides(patch, cellX, cellZ);

                // A joined cap grows between rebuilds like the ground beside it, so the two stay level.
                float growth = GroundJoined(patch) ? 1f : 0f;
                for (int z = 0; z < width; z++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        var point = patch.Points[z * width + x];
                        float lift = RaisedVertexLift(patch, neighbors, x, z, snowDepth, flushSides);
                        AddRaisedVertex(point, lift, now, growth);
                    }
                }

                for (int z = 0; z < divisions; z++)
                {
                    for (int x = 0; x < divisions; x++)
                    {
                        int face = z * divisions + x;
                        byte wedges = patch.Faces[face];
                        int a = start + z * width + x;
                        int b = a + width;
                        if (wedges == 15)
                        {
                            AddSurfaceQuad(a, b);
                        }
                        else if (wedges != 0)
                        {
                            int middle = _vertices.Count;
                            AddRaisedVertex(patch.Centers[face], RaisedCenterLift(snowDepth), now);
                            AddSupportedTriangle(wedges, SurfaceNorth, a + 1, a, middle);
                            AddSupportedTriangle(wedges, SurfaceEast, b + 1, a + 1, middle);
                            AddSupportedTriangle(wedges, SurfaceSouth, b, b + 1, middle);
                            AddSupportedTriangle(wedges, SurfaceWest, a, b, middle);
                        }
                    }
                }
            }
        }

        private (RaisedSnowPatch West, RaisedSnowPatch East, RaisedSnowPatch South, RaisedSnowPatch North)
            GetRaisedNeighbors(int x, int z)
        {
            return (GetRaisedPatch(x - 1, z), GetRaisedPatch(x + 1, z),
                GetRaisedPatch(x, z - 1), GetRaisedPatch(x, z + 1));
        }

        private const int FlushWest = 1, FlushEast = 2, FlushSouth = 4, FlushNorth = 8;

        // Roofs (raised supports) with at least one cap level with the open ground beside it, such as the Woods
        // bunker's earth roof. Their caps hold the ground's depth; other roofs keep the thin prop cover.
        private readonly HashSet<Collider> _groundJoinedSupports = new HashSet<Collider>();

        private static bool HoldsSupport(Chunk chunk, Collider support)
        {
            foreach (var entry in chunk.RaisedSurfaces)
            {
                if (ReferenceEquals(entry.Value.Support, support))
                {
                    return true;
                }
            }

            return false;
        }

        private bool GroundJoined(RaisedSnowPatch patch)
        {
            return patch.JoinsGround && _groundJoinedSupports.Contains(patch.Support);
        }

        // Sides of a ground-joined cap where open ground lies level with it and no other cap does: the cap keeps its
        // full depth up to that edge and the ground snow carries on from there.
        private int GroundFlushSides(RaisedSnowPatch patch, int cellX, int cellZ)
        {
            return GroundJoined(patch) ? FlushSides(patch, cellX, cellZ) : 0;
        }

        private int FlushSides(RaisedSnowPatch patch, int cellX, int cellZ)
        {
            float height = PatchHeight(patch);
            return (SideGroundFlush(cellX - 1, cellZ, height) ? FlushWest : 0)
                | (SideGroundFlush(cellX + 1, cellZ, height) ? FlushEast : 0)
                | (SideGroundFlush(cellX, cellZ - 1, height) ? FlushSouth : 0)
                | (SideGroundFlush(cellX, cellZ + 1, height) ? FlushNorth : 0);
        }

        private bool SideGroundFlush(int x, int z, float height)
        {
            if (GetRaisedPatch(x, z).Faces != null)
            {
                return false;
            }

            Column column = GetSurfaceColumn(x, z);
            return column.Exposed && !column.Sheltered && !column.ShowsUpperLevel
                && Mathf.Abs(column.GroundHeight - height) < RoofJoinTolerance;
        }

        private float RaisedVertexLift(RaisedSnowPatch patch,
            (RaisedSnowPatch West, RaisedSnowPatch East, RaisedSnowPatch South, RaisedSnowPatch North) neighbors,
            int x, int z, float snowDepth, int flushSides)
        {
            var point = patch.Points[z * (patch.Divisions + 1) + x];
            float west = (flushSides & FlushWest) != 0 ? 1f : EdgeCover(patch, neighbors.West, x, z, -1, 0);
            float east = (flushSides & FlushEast) != 0 ? 1f : EdgeCover(patch, neighbors.East, x, z, 1, 0);
            float south = (flushSides & FlushSouth) != 0 ? 1f : EdgeCover(patch, neighbors.South, x, z, 0, -1);
            float north = (flushSides & FlushNorth) != 0 ? 1f : EdgeCover(patch, neighbors.North, x, z, 0, 1);
            float edge = Mathf.Min(Mathf.Min(west, east), Mathf.Min(south, north));
            bool rock = patch.Divisions == RockDivisions;

            // A cap on a roof level with the ground holds what the ground holds, not the 10 cm of a prop top.
            float depth = GroundJoined(patch) ? snowDepth * GroundVariation(point.Position.x, point.Position.z)
                : RaisedThickness(snowDepth, rock);
            float slope = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(rock ? MinimumRockNormal : MinimumCapNormal, 0.98f, point.Normal.y));
            return Mathf.Lerp(rock ? -0.018f : -0.009f, depth * slope, Mathf.Min(point.Cover, edge));
        }

        private void CollectRaisedImpressions(Chunk chunk, Collider support, Vector2 center)
        {
            _raisedImpressions.Clear();
            if (chunk.Footprints == null)
            {
                return;
            }

            foreach (var stamp in chunk.Footprints)
            {
                float reach = stamp.Reach + CellWidth;
                if (ReferenceEquals(stamp.RaisedSupport, support) && (stamp.Center - center).sqrMagnitude < reach * reach)
                {
                    _raisedImpressions.Add(stamp);
                }
            }
        }

        private void AddRaisedVertex(SnowCoverSample sample, float lift, float now, float growth = 0f)
        {
            Vector3 normal = sample.Valid ? sample.Normal : Vector3.up;
            Vector3 point = sample.Position;
            if (_prints != null)
            {
                PadPrintLimits();
                float capLimit = Mathf.Max(0f, lift) * 0.92f;
                _printLimits.Add(new Vector4(capLimit, capLimit, 0f, growth));
            }
            else if (lift > 0f && _raisedImpressions.Count > 0)
            {
                const float step = 0.02f;
                var center = new Vector2(point.x, point.z);
                var dx = new Vector2(step, 0f);
                var dz = new Vector2(0f, step);
                float limit = lift * 0.92f;
                lift -= RaisedCompression(center, point.y, limit, now);
                normal.x += normal.y * (RaisedCompression(center + dx, point.y, limit, now)
                    - RaisedCompression(center - dx, point.y, limit, now)) / (2f * step);
                normal.z += normal.y * (RaisedCompression(center + dz, point.y, limit, now)
                    - RaisedCompression(center - dz, point.y, limit, now)) / (2f * step);
                normal = normal.normalized;
            }

            _vertices.Add(point + Vector3.up * lift);
            _normals.Add(normal);
        }

        private float RaisedCompression(Vector2 point, float height, float limit, float now)
        {
            float compression = 0f;
            foreach (var stamp in _raisedImpressions)
            {
                if (Mathf.Abs(stamp.SupportHeight - height) <= 0.14f)
                {
                    compression = Mathf.Max(compression, stamp.Compression(point, now));
                }
            }

            return LimitCompression(compression, limit);
        }

        private bool TryGetRaisedFootSupport(Vector3 local, out Collider support, out float depth)
        {
            support = null;
            depth = 0f;
            int cellX = Mathf.FloorToInt(local.x / CellWidth);
            int cellZ = Mathf.FloorToInt(local.z / CellWidth);
            Chunk chunk = GetChunk(cellX, cellZ);
            if (chunk == null || !chunk.RaisedRendered)
            {
                return false;
            }

            var patch = GetRaisedPatch(cellX, cellZ);
            if (NearStairFlight(cellX, cellZ) || IsTrenchHidden(cellX, cellZ))
            {
                return false;
            }

            if (patch.Faces == null)
            {
                return false;
            }

            int divisions = patch.Divisions;
            int width = divisions + 1;
            float x = (local.x / CellWidth - cellX) * divisions;
            float z = (local.z / CellWidth - cellZ) * divisions;
            int i = Mathf.Min((int)x, divisions - 1);
            int j = Mathf.Min((int)z, divisions - 1);
            float u = x - i;
            float v = z - j;
            int face = j * divisions + i;
            byte wedges = patch.Faces[face];
            if (wedges == 0)
            {
                return false;
            }

            var neighbors = GetRaisedNeighbors(cellX, cellZ);
            int flushSides = GroundFlushSides(patch, cellX, cellZ);
            int a = j * width + i;
            var heights = new Vector4(patch.Points[a].Position.y, patch.Points[a + width].Position.y,
                patch.Points[a + 1].Position.y, patch.Points[a + width + 1].Position.y);
            var lift = new Vector4(RaisedVertexLift(patch, neighbors, i, j, chunk.Depth, flushSides),
                RaisedVertexLift(patch, neighbors, i, j + 1, chunk.Depth, flushSides),
                RaisedVertexLift(patch, neighbors, i + 1, j, chunk.Depth, flushSides),
                RaisedVertexLift(patch, neighbors, i + 1, j + 1, chunk.Depth, flushSides));

            float floor;
            float thickness;
            if (wedges == 15)
            {
                floor = InterpolateHeight(heights.x, heights.y, heights.z, heights.w, u, v);
                thickness = InterpolateHeight(lift.x, lift.y, lift.z, lift.w, u, v);
            }
            else
            {
                byte side = RaisedFaceSide(u, v);
                if ((wedges & side) == 0)
                {
                    return false;
                }

                floor = RaisedFaceHeight(heights, patch.Centers[face].Position.y, side, u, v);
                thickness = RaisedFaceHeight(lift, RaisedCenterLift(chunk.Depth), side, u, v);
            }

            depth = thickness - chunk.GrowthOffset;
            if (Mathf.Abs(local.y - floor) > 0.055f || depth < 0.003f)
            {
                return false;
            }

            support = patch.Support;
            return true;
        }

        private static byte RaisedFaceSide(float u, float v)
        {
            return Mathf.Abs(u - 0.5f) > Mathf.Abs(v - 0.5f)
                ? (u < 0.5f ? SurfaceWest : SurfaceEast)
                : (v < 0.5f ? SurfaceNorth : SurfaceSouth);
        }

        private static float RaisedFaceHeight(Vector4 corners, float center, byte side, float u, float v)
        {
            return side switch
            {
                SurfaceWest => InterpolateRaisedWedge(corners.x, corners.y, center, v, u),
                SurfaceEast => InterpolateRaisedWedge(corners.z, corners.w, center, v, 1f - u),
                SurfaceNorth => InterpolateRaisedWedge(corners.x, corners.z, center, u, v),
                _ => InterpolateRaisedWedge(corners.y, corners.w, center, u, 1f - v)
            };
        }

        private static float InterpolateRaisedWedge(float a, float b, float center, float along, float inward)
        {
            return a * (1f - along - inward) + b * (along - inward) + center * (2f * inward);
        }

        private RaisedSnowPatch GetRaisedPatch(int x, int z)
        {
            Chunk chunk = GetChunk(x, z);
            return chunk?.RaisedSurfaces != null && chunk.RaisedReady
                && chunk.RaisedSurfaces.TryGetValue(chunk.ColumnIndex(x, z), out var patch)
                ? patch : default;
        }

        private static float EdgeCover(RaisedSnowPatch patch, RaisedSnowPatch neighbor, int x, int z, int dx, int dz)
        {
            int end = patch.Divisions;
            float taperCells = end * 0.4f;
            int borderX = dx < 0 ? 0 : dx > 0 ? end : x;
            int borderZ = dz < 0 ? 0 : dz > 0 ? end : z;
            int distance = Mathf.Abs(x - borderX) + Mathf.Abs(z - borderZ);
            if (distance >= taperCells)
            {
                return 1f;
            }

            if (neighbor.Faces != null)
            {
                // Patches can use different subdivisions (rock 10, props 6). Map the border point onto the neighbor's
                // own grid so both sides of the seam keep their buildup.
                int other = neighbor.Divisions;
                int along = Mathf.RoundToInt((dx != 0 ? z : x) * (float)other / end);
                int oppositeX = dx == 0 ? along : (dx < 0 ? other : 0);
                int oppositeZ = dz == 0 ? along : (dz < 0 ? other : 0);
                var own = patch.Points[borderZ * (end + 1) + borderX];
                var match = neighbor.Points[oppositeZ * (other + 1) + oppositeX];
                float seamTolerance = other == end ? 0.015f : 0.03f;
                if (own.Fits(match, seamTolerance)
                    && HasContinuousBorder(patch, borderX, borderZ, dx != 0)
                    && HasContinuousBorder(neighbor, oppositeX, oppositeZ, dx != 0))
                {
                    return Mathf.SmoothStep(Mathf.Min(own.Cover, match.Cover), 1f, distance / taperCells);
                }
            }

            return Mathf.SmoothStep(0f, 1f, distance / taperCells);
        }

        private static bool HasContinuousBorder(RaisedSnowPatch patch, int x, int z, bool sideRunsAlongZ)
        {
            int end = patch.Divisions;
            int along = sideRunsAlongZ ? z : x;
            int border = sideRunsAlongZ ? x : z;
            int face = border == 0 ? 0 : end - 1;
            int first = sideRunsAlongZ ? face : face * end;
            int stride = sideRunsAlongZ ? end : 1;

            // Both incident spans must exist or the supported vertices can leave a raised lip.
            byte side = sideRunsAlongZ ? (border == 0 ? SurfaceWest : SurfaceEast)
                : (border == 0 ? SurfaceNorth : SurfaceSouth);
            return (along == 0 || (patch.Faces[first + (along - 1) * stride] & side) != 0)
                && (along == end || (patch.Faces[first + along * stride] & side) != 0);
        }

        private readonly struct RaisedSnowPatch
        {
            public readonly SnowCoverSample[] Points;
            public readonly byte[] Faces;
            public readonly SnowCoverSample[] Centers;
            public readonly Collider Support;
            public readonly string ModelName;
            public readonly int Divisions;

            // The cap lies on a stored open roof level with the ground around it (see SampleRaisedChunk).
            public readonly bool JoinsGround;
            public bool HasSupport => Points != null;

            public RaisedSnowPatch(SnowCoverSample[] points, byte[] faces, Collider support, string modelName, int divisions,
                SnowCoverSample[] centers = null, bool joinsGround = false)
            {
                Points = points;
                Faces = faces;
                Centers = centers;
                Support = support;
                ModelName = modelName;
                Divisions = divisions;
                JoinsGround = joinsGround;
            }
        }

        private static float PatchHeight(RaisedSnowPatch patch)
        {
            float sum = 0f;
            int count = 0;
            foreach (var point in patch.Points)
            {
                if (point.Valid)
                {
                    sum += point.Position.y;
                    count++;
                }
            }

            return count == 0 ? float.NaN : sum / count;
        }
    }
}
