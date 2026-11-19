using System.Collections.Generic;
using UnityEngine;

namespace WinterOutbreak
{
    internal sealed partial class VoxelSnowArea
    {
        private const int FootprintSubdivision = 12;
        private const int StampWidth = ChunkWidth * FootprintSubdivision + 3;
        private const float FineSpacing = CellWidth / FootprintSubdivision;
        private readonly int _maxFootprintsPerChunk;

        private readonly float[] _compression = new float[StampWidth * StampWidth];
        private readonly bool[] _detailedCells = new bool[ChunkWidth * ChunkWidth];

        // GPU prints (SnowPrintTexture)
        private const int GpuFootprintsPerChunk = 256;
        // Background stamps (bot steps, BTR tracks) this close to the player refine their cells promptly
        private const float PromptRefineRadius = 35f;
        private const int GpuSubdivision = 8;
        // A cell is refined when its centre lies within this distance of a print's outline
        private const float DetailMargin = 0.36f;
        private readonly SnowPrintTexture _prints;
        // UV3.w = how fast the vertex rises with snow depth (metres per metre)
        private readonly List<Vector4> _printLimits = new List<Vector4>();
        private const float GrowthProbe = 0.01f;

        private float GrowthSlopeAt(int vertex)
        {
            return vertex < _printLimits.Count ? _printLimits[vertex].w : 0f;
        }
        private const float ProneMaxCompression = 0.15f;
        private const float ProneDepthShare = 0.8f;
        // Skirts hang this far below the lowest a refined edge can be pressed
        private const float SkirtDrop = 0.08f;

        public string PrintStatus { get; }

        private int ImpressionDivisions => _prints != null ? GpuSubdivision : FootprintSubdivision;

        public bool StampFootprint(Vector3 position, Vector3 forward, float refillSeconds, bool aiStep = false)
        {
            return StampImpression(position, forward, refillSeconds, false, aiStep: aiStep);
        }

        public bool StampProne(Vector3 position, Vector3 forward, float refillSeconds)
        {
            // With GPU prints the trail (PressProneTrail) draws the furrow
            return StampImpression(position, forward, refillSeconds, true, drawNow: _prints == null);
        }

        public bool GpuPrints => _prints != null;

        /// <summary>
        /// Presses the prone body into the print texture only, between the listed prone stamps, so a crawl reads as one
        /// continuous furrow.
        /// </summary>
        public void PressProneTrail(Vector3 position, Vector3 forward, float refillSeconds, float settle = 1f)
        {
            if (_prints == null || !TryGetDepth(position, out float depth) || depth < 0.025f)
            {
                return;
            }

            Vector3 local = position - _origin;
            // settle eases the body in while lying down
            var stamp = new SnowImpression(new Vector2(local.x, local.z), new Vector2(forward.x, forward.z),
                Mathf.Min(ProneMaxCompression, depth * ProneDepthShare) * Mathf.Clamp01(settle), Time.time, refillSeconds, true);
            if (InPrintWindow(stamp))
            {
                QueuePrint(stamp, Time.time);
            }
        }

        public bool StampBody(Vector3 position, Vector3 forward, float refillSeconds)
        {
            return StampImpression(position, forward, refillSeconds, true, corpse: true);
        }

        // No published chunk at this point yet: the snow here is not known, as opposed to known to be bare
        private bool IsUnbuiltGround(Vector3 local)
        {
            Chunk chunk = GetChunk(Mathf.FloorToInt(local.x / CellWidth), Mathf.FloorToInt(local.z / CellWidth));
            return chunk == null || !chunk.Active;
        }

        private bool StampImpression(Vector3 position, Vector3 forward, float refillSeconds, bool prone,
            bool aiStep = false, bool corpse = false, bool drawNow = true)
        {
            Vector3 local = position - _origin;
            Collider raisedSupport = null;
            float depth = 0f;
            bool raised = !prone && TryGetRaisedFootSupport(local, out raisedSupport, out depth);
            if (!raised && !TryGetDepth(position, out depth))
            {
                // Where the chunk is simply not built, keep the player's own step at the storm's depth
                if (prone || aiStep || corpse || !IsUnbuiltGround(local))
                {
                    return false;
                }

                depth = LastDepth;
            }

            if (!raised && depth < (prone ? 0.025f : 0.012f))
            {
                return false;
            }

            var center = new Vector2(local.x, local.z);
            var flatForward = new Vector2(forward.x, forward.z);
            // A prone player sinks most of the way in, so the view and the weapon clear the snow ahead
            float compression = corpse ? Mathf.Min(0.13f, depth * 0.91f)
                : prone ? Mathf.Min(ProneMaxCompression, depth * ProneDepthShare)
                : Mathf.Min(0.105f, depth * 0.84f);
            var stamp = new SnowImpression(center, flatForward,
                compression, Time.time, refillSeconds, prone, aiStep: aiStep, corpse: corpse,
                raisedSupport: raisedSupport, supportHeight: local.y);

            AddImpression(stamp, drawNow);
            return true;
        }

        /// <summary>
        /// Presses a tyre track along a wheel's path from start to end (BTR).
        /// </summary>
        public bool StampTrack(Vector3 start, Vector3 end, float halfWidth, float maxDepth, float refillSeconds)
        {
            Vector3 middle = (start + end) * 0.5f;
            if (!TryGetDepth(middle, out float depth))
            {
                // Use the storm's depth
                depth = LastDepth;
            }

            if (depth < 0.02f)
            {
                return false;
            }

            Vector3 local = middle - _origin;
            var segment = new Vector2(end.x - start.x, end.z - start.z);
            var stamp = new SnowImpression(new Vector2(local.x, local.z), segment,
                Mathf.Min(maxDepth, depth * 0.92f), Time.time, refillSeconds, false, aiStep: true,
                trackHalfWidth: halfWidth);
            AddImpression(stamp);
            return true;
        }

        /// <summary>
        /// A bullet hole: a small round pit with a rim.
        /// </summary>
        public void StampBulletHole(Vector3 position, float radius, float depth, float refillSeconds)
        {
            Vector3 local = position - _origin;
            var stamp = new SnowImpression(new Vector2(local.x, local.z), RandomPhases(),
                depth, Time.time, refillSeconds, false, radius, aiStep: true);
            AddImpression(stamp);
        }

        // Two random phases for a round stamp's lobed outline (carried in its forward vector)
        private static Vector2 RandomPhases()
        {
            return new Vector2(Random.Range(0f, 6.283f), Random.Range(0f, 6.283f));
        }

        /// <summary>
        /// Melts snow down to the ground inside a lobed outline with a soft edge (grenade blasts, fires).
        /// </summary>
        public void StampMelt(Vector3 position, float radius, float depth, float refillSeconds, bool scorch,
            Vector2? phases = null)
        {
            Vector3 local = position - _origin;
            var stamp = new SnowImpression(new Vector2(local.x, local.z), phases ?? RandomPhases(),
                depth, Time.time, refillSeconds, false, radius,
                meltKind: scorch ? SnowPrintTexture.KindGrenadeMelt : SnowPrintTexture.KindFireMelt);
            AddImpression(stamp);
        }

        /// <summary>
        /// A fire melt drawn into the print texture only (no stamp list, no mesh work).
        /// </summary>
        private void PressMeltTrail(Vector3 position, float radius, float depth, float refillSeconds, Vector2 phases)
        {
            Vector3 local = position - _origin;
            var stamp = new SnowImpression(new Vector2(local.x, local.z), phases, depth, Time.time, refillSeconds,
                false, radius, meltKind: SnowPrintTexture.KindFireMelt);
            if (InPrintWindow(stamp))
            {
                QueuePrint(stamp, Time.time);
            }
        }

        /// <summary>
        /// A hollow under a loose item lying in snow: a rounded groove along the item's long axis (start to end), with
        /// no tread, so small items sit visibly in a dent instead of vanishing into the snow.
        /// </summary>
        public bool StampItemHollow(Vector3 start, Vector3 end, float halfWidth, float maxDepth, float refillSeconds)
        {
            Vector3 middle = (start + end) * 0.5f;
            if (!TryGetDepth(middle, out float depth) || depth < 0.02f)
            {
                return false;
            }

            Vector3 local = middle - _origin;
            var segment = new Vector2(end.x - start.x, end.z - start.z);
            var stamp = new SnowImpression(new Vector2(local.x, local.z), segment,
                Mathf.Min(maxDepth, depth * 0.9f), Time.time, refillSeconds, false, aiStep: true,
                trackHalfWidth: halfWidth, meltKind: SnowPrintTexture.KindItem);
            AddImpression(stamp);
            return true;
        }

        public void StampBlast(Vector3 position, float radius, float depth, float refillSeconds)
        {
            Vector3 local = position - _origin;
            var stamp = new SnowImpression(new Vector2(local.x, local.z), RandomPhases(),
                depth, Time.time, refillSeconds, false, radius);
            AddImpression(stamp);
        }

        private Vector2 WorldXZ(Vector2 local)
        {
            return new Vector2(local.x + _origin.x, local.y + _origin.z);
        }

        /// <summary>
        /// Draws a ground stamp into the print texture at its current, partly refilled depth.
        /// </summary>
        private bool QueuePrint(SnowImpression stamp, float now)
        {
            float remaining = stamp.EndTime - now;
            if (_prints == null || remaining <= 0.05f)
            {
                return false;
            }

            float depth = stamp.PeakDepth * Mathf.Clamp01(remaining / stamp.Duration);
            _prints.Queue(WorldXZ(stamp.Center), stamp.Forward, depth, stamp.Kind, stamp.ShapeRadius, remaining,
                urgent: !stamp.IsAiStep);
            return true;
        }

        private bool InPrintWindow(SnowImpression stamp)
        {
            return _prints != null && _prints.Overlaps(WorldXZ(stamp.Center), stamp.Reach);
        }

        /// <summary>
        /// The print texture only covers the area around the player.
        /// </summary>
        private void RedrawPrintsAfterRecentre()
        {
            if (_prints == null || !_prints.TakeRecentre(out var previous))
            {
                return;
            }

            RefreshChunksNearFires();

            float now = Time.time;
            foreach (var chunk in _chunks.Values)
            {
                if (chunk.Footprints == null)
                {
                    continue;
                }

                bool refine = false;
                foreach (var stamp in chunk.Footprints)
                {
                    if (stamp.EndTime <= now)
                    {
                        continue;
                    }

                    Vector2 world = WorldXZ(stamp.Center);
                    if (!_prints.Overlaps(world, stamp.Reach)
                        || (previous.HasValue && SnowPrintTexture.Inside(previous.Value, world, stamp.Reach)))
                    {
                        continue;
                    }

                    // Each stamp is listed in every chunk it reaches
                    if (ChunkKeyAt(stamp.Center) == chunk.Key)
                    {
                        QueuePrint(stamp, now);
                    }

                    refine |= chunk.Active && ReferenceEquals(stamp.RaisedSupport, null) && AddsDetail(chunk, stamp);
                }

                if (refine)
                {
                    if (DistanceSquared(chunk, _previousPosition) <= PromptRefineRadius * PromptRefineRadius)
                    {
                        chunk.FootprintsDirty = true;
                    }
                    else
                    {
                        chunk.FootprintRefresh = Mathf.Min(chunk.FootprintRefresh, now + 0.15f);
                    }
                }
            }
        }

        private static Vector2Int ChunkKeyAt(Vector2 local)
        {
            return new Vector2Int(Mathf.FloorToInt(local.x / ChunkSize), Mathf.FloorToInt(local.y / ChunkSize));
        }

        private void AddImpression(SnowImpression stamp, bool drawNow = true)
        {
            Vector2 center = stamp.Center;
            float reach = stamp.Reach + FineSpacing;
            int minX = Mathf.FloorToInt((center.x - reach) / ChunkSize);
            int maxX = Mathf.FloorToInt((center.x + reach) / ChunkSize);
            int minZ = Mathf.FloorToInt((center.y - reach) / ChunkSize);
            int maxZ = Mathf.FloorToInt((center.y + reach) / ChunkSize);

            // Ground prints outside the texture window are kept in the lists below and drawn, and their cells refined,
            // when the window reaches them (RedrawPrintsAfterRecentre)
            bool visible = InPrintWindow(stamp);
            if (visible && drawNow)
            {
                QueuePrint(stamp, Time.time);
            }

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Chunk chunk = GetOrCreateChunk(new Vector2Int(x, z));
                    chunk.Footprints ??= new List<SnowImpression>();
                    if (chunk.Footprints.Count >= _maxFootprintsPerChunk)
                    {
                        // Bot footsteps give way first, then BTR tracks
                        int oldestAiStep = chunk.Footprints.FindIndex(existing =>
                            existing.IsAiStep && existing.Kind != SnowPrintTexture.KindTrack);
                        if (oldestAiStep < 0)
                        {
                            oldestAiStep = chunk.Footprints.FindIndex(existing => existing.IsAiStep);
                        }

                        if (oldestAiStep < 0 && stamp.IsAiStep)
                        {
                            continue;
                        }

                        chunk.Footprints.RemoveAt(Mathf.Max(0, oldestAiStep));
                    }

                    chunk.Footprints.Add(stamp);
                    if (_prints != null && !ReferenceEquals(stamp.RaisedSupport, null))
                    {
                        // A print on a raised cap: the texture shows it on the cap's existing vertices
                        continue;
                    }

                    if (_prints != null && ReferenceEquals(stamp.RaisedSupport, null))
                    {
                        // The texture already shows the print
                        if (!visible || !AddsDetail(chunk, stamp))
                        {
                            continue;
                        }
                    }

                    if (stamp.IsAiStep && DistanceSquared(chunk, _previousPosition) > PromptRefineRadius * PromptRefineRadius)
                    {
                        // Batch distant bot tracks with regular refreshes so they cannot delay terrain sampling
                        chunk.FootprintRefresh = Mathf.Min(chunk.FootprintRefresh, Time.time + 0.15f);
                    }
                    else
                    {
                        chunk.FootprintsDirty = true;
                    }
                }
            }
        }

        private bool PrepareFootprints(Chunk chunk)
        {
            chunk.FootprintsDirty = false;
            chunk.FootprintRefresh = float.PositiveInfinity;
            if (chunk.Footprints == null)
            {
                chunk.BuiltDetail = null;
                return false;
            }

            float now = Time.time;
            ExpireFootprints(chunk, now);
            if (chunk.Footprints == null)
            {
                chunk.BuiltDetail = null;
                return false;
            }

            if (_prints != null)
            {
                return PrepareGpuFootprints(chunk, now);
            }

            // Refill is only animated closely near the player
            chunk.FootprintRefresh = now + (DistanceSquared(chunk, _previousPosition)
                <= NearbyDepthRadius * NearbyDepthRadius ? NearbyRefillSeconds : DistantRefillSeconds);
            if (!chunk.Footprints.Exists(stamp => ReferenceEquals(stamp.RaisedSupport, null)))
            {
                return false;
            }

            System.Array.Clear(_compression, 0, _compression.Length);
            System.Array.Clear(_detailedCells, 0, _detailedCells.Length);
            Vector2 gridOrigin = new Vector2(chunk.X * CellWidth, chunk.Z * CellWidth)
                - Vector2.one * FineSpacing;
            foreach (var stamp in chunk.Footprints)
            {
                if (ReferenceEquals(stamp.RaisedSupport, null))
                {
                    RasterizeFootprint(stamp, gridOrigin, now);
                }
            }

            return true;
        }

        private bool PrepareGpuFootprints(Chunk chunk, float now)
        {
            bool ground = false;
            foreach (var stamp in chunk.Footprints)
            {
                if (ReferenceEquals(stamp.RaisedSupport, null))
                {
                    ground = true;
                    break;
                }
            }

            // Prints refill on the GPU, so no rebuild is scheduled once they have gone

            // Raised cap prints refill in the print texture too

            if (!ground)
            {
                chunk.BuiltDetail = null;
                return false;
            }

            System.Array.Clear(_detailedCells, 0, _detailedCells.Length);
            foreach (var stamp in chunk.Footprints)
            {
                // Stamps outside the texture window are refined when the window reaches them
                if (ReferenceEquals(stamp.RaisedSupport, null) && InPrintWindow(stamp))
                {
                    VisitStampCells(chunk, stamp, markOnly: true);
                }
            }

            chunk.RefinedAt ??= new float[ChunkWidth * ChunkWidth];
            for (int i = 0; i < _detailedCells.Length; i++)
            {
                if (_detailedCells[i] && (chunk.BuiltDetail == null || !chunk.BuiltDetail[i]))
                {
                    chunk.RefinedAt[i] = now;
                }
            }

            chunk.BuiltDetail ??= new bool[ChunkWidth * ChunkWidth];
            System.Array.Copy(_detailedCells, chunk.BuiltDetail, _detailedCells.Length);
            return true;
        }

        private bool AddsDetail(Chunk chunk, SnowImpression stamp)
        {
            return VisitStampCells(chunk, stamp, markOnly: false);
        }

        // markOnly: mark every covered cell in _detailedCells
        private bool VisitStampCells(Chunk chunk, SnowImpression stamp, bool markOnly)
        {
            bool[] built = chunk.BuiltDetail;
            if (!markOnly && built == null)
            {
                return true;
            }

            float minX = chunk.X * CellWidth;
            float minZ = chunk.Z * CellWidth;
            float reach = stamp.Reach + DetailMargin;
            Vector2 center = stamp.Center;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((center.x - reach - minX) / CellWidth));
            int x1 = Mathf.Min(ChunkWidth - 1, Mathf.FloorToInt((center.x + reach - minX) / CellWidth));
            int z0 = Mathf.Max(0, Mathf.FloorToInt((center.y - reach - minZ) / CellWidth));
            int z1 = Mathf.Min(ChunkWidth - 1, Mathf.FloorToInt((center.y + reach - minZ) / CellWidth));
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var cell = new Vector2(minX + (x + 0.5f) * CellWidth, minZ + (z + 0.5f) * CellWidth);
                    if (!stamp.Covers(cell, DetailMargin))
                    {
                        continue;
                    }

                    int index = z * ChunkWidth + x;
                    if (markOnly)
                    {
                        _detailedCells[index] = true;
                    }
                    else if (!built[index])
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // UV3 for vertices added without their own entry: x = displacement limit, y = shading-only limit
        private void PadPrintLimits(Vector4 fill = default)
        {
            while (_printLimits.Count < _vertices.Count)
            {
                _printLimits.Add(fill);
            }
        }

        private static void ExpireFootprints(Chunk chunk, float now)
        {
            var footprints = chunk.Footprints;
            for (int i = footprints.Count - 1; i >= 0; i--)
            {
                if (footprints[i].EndTime <= now)
                {
                    footprints.RemoveAt(i);
                }
            }

            if (footprints.Count == 0)
            {
                chunk.Footprints = null;
            }
        }

        private void AddDetailedCell(Chunk chunk, int cellX, int cellZ, CellVertices cell, Column column,
            bool hasImpression, bool hasTransition, GroundTransition transition)
        {
            int first = _vertices.Count;
            int corner = (cellZ + 1) * CornerWidth + cellX + 1;
            int divisions = hasImpression ? ImpressionDivisions : TransitionDivisions;
            bool gpu = hasImpression && _prints != null;
            float refinedAt = chunk.RefinedAt?[cellZ * ChunkWidth + cellX] ?? 0f;
            int width = divisions + 1;
            float depth = chunk.Depth;
            var available = new Vector4(
                CellCornerDepthWeight(column, chunk.Corners[corner], 0.5f, 0.5f),
                CellCornerDepthWeight(column, chunk.Corners[corner + CornerWidth], 0.5f, -0.5f),
                CellCornerDepthWeight(column, chunk.Corners[corner + 1], -0.5f, 0.5f),
                CellCornerDepthWeight(column, chunk.Corners[corner + CornerWidth + 1], -0.5f, -0.5f));

            for (int z = 0; z < width; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    float u = (float)x / divisions;
                    float v = (float)z / divisions;
                    Vector3 point = Interpolate(_vertices[cell.A], _vertices[cell.B],
                        _vertices[cell.C], _vertices[cell.D], u, v);
                    Vector3 normal = Interpolate(_normals[cell.A], _normals[cell.B],
                        _normals[cell.C], _normals[cell.D], u, v);

                    if (hasTransition)
                    {
                        point.y = hasImpression ? transition.Height(u, v) : transition.Sample(u, v);
                    }

                    float slope = _prints == null ? 0f : InterpolateHeight(GrowthSlopeAt(cell.A), GrowthSlopeAt(cell.B),
                        GrowthSlopeAt(cell.C), GrowthSlopeAt(cell.D), u, v);
                    if (gpu)
                    {
                        // The shader presses this vertex down by the print texture, up to this limit
                        float weight = InterpolateHeight(available.x, available.y, available.z, available.w, u, v);
                        PadPrintLimits(new Vector4(0f, depth * 0.95f, 0f, 0f));
                        float limit = depth * weight * 0.95f;
                        // Refined cells do not share vertices
                        float vertexRefined = refinedAt;
                        int sx = x == 0 ? -1 : x == divisions ? 1 : 0;
                        int sz = z == 0 ? -1 : z == divisions ? 1 : 0;
                        if (sx != 0)
                        {
                            vertexRefined = Mathf.Max(vertexRefined, CellRefinedAt(chunk, cellX + sx, cellZ));
                        }

                        if (sz != 0)
                        {
                            vertexRefined = Mathf.Max(vertexRefined, CellRefinedAt(chunk, cellX, cellZ + sz));
                        }

                        if (sx != 0 && sz != 0)
                        {
                            vertexRefined = Mathf.Max(vertexRefined, CellRefinedAt(chunk, cellX + sx, cellZ + sz));
                        }

                        _printLimits.Add(new Vector4(limit, limit, vertexRefined, slope));
                    }
                    else if (_prints != null)
                    {
                        // A transition cell
                        PadPrintLimits(new Vector4(0f, depth * 0.95f, 0f, 0f));
                        _printLimits.Add(new Vector4(0f, depth * 0.95f, 0f, slope));
                    }
                    else if (hasImpression)
                    {
                        int sampleX = cellX * FootprintSubdivision + x + 1;
                        int sampleZ = cellZ * FootprintSubdivision + z + 1;
                        int sample = sampleZ * StampWidth + sampleX;
                        float weight = InterpolateHeight(available.x, available.y, available.z, available.w, u, v);
                        float limit = depth * weight * 0.90f;

                        point.y -= LimitCompression(_compression[sample], limit);
                        float dx = LimitCompression(_compression[sample + 1], limit)
                            - LimitCompression(_compression[sample - 1], limit);
                        float dz = LimitCompression(_compression[sample + StampWidth], limit)
                            - LimitCompression(_compression[sample - StampWidth], limit);
                        normal.x += normal.y * dx / (2f * FineSpacing);
                        normal.z += normal.y * dz / (2f * FineSpacing);
                    }

                    _vertices.Add(point);
                    _normals.Add(normal.normalized);
                }
            }

            for (int z = 0; z < divisions; z++)
            {
                for (int x = 0; x < divisions; x++)
                {
                    int top = first + z * width + x;
                    AddSurfaceQuad(top, top + width);
                }
            }

            if (gpu)
            {
                AddSkirtIfOpen(chunk, cellX, cellZ, -1, 0, first, width, 0, width);
                AddSkirtIfOpen(chunk, cellX, cellZ, 1, 0, first + divisions, width, 0, width);
                AddSkirtIfOpen(chunk, cellX, cellZ, 0, -1, first, 1, 0, width);
                AddSkirtIfOpen(chunk, cellX, cellZ, 0, 1, first + divisions * width, 1, 0, width);
            }
        }

        private float CellRefinedAt(Chunk chunk, int cellX, int cellZ)
        {
            Chunk owner = chunk;
            if (cellX < 0 || cellZ < 0 || cellX >= ChunkWidth || cellZ >= ChunkWidth)
            {
                owner = GetChunk(chunk.X + cellX, chunk.Z + cellZ);
                if (owner == null)
                {
                    return float.NegativeInfinity;
                }

                cellX = chunk.X + cellX - owner.X;
                cellZ = chunk.Z + cellZ - owner.Z;
            }

            int index = cellZ * ChunkWidth + cellX;
            return owner.BuiltDetail != null && owner.BuiltDetail[index] && owner.RefinedAt != null
                ? owner.RefinedAt[index]
                : float.NegativeInfinity;
        }

        /// <summary>
        /// Hangs a skirt under one edge of a refined cell where the neighbour beside it is not refined in this chunk
        /// (coarse cells, or any cell across the chunk border, which is rebuilt on its own).
        /// </summary>
        private void AddSkirtIfOpen(Chunk chunk, int cellX, int cellZ, int dx, int dz, int start, int stride,
            int from, int count)
        {
            int nx = cellX + dx, nz = cellZ + dz;
            bool inside = nx >= 0 && nz >= 0 && nx < ChunkWidth && nz < ChunkWidth;
            if (inside && _detailedCells[nz * ChunkWidth + nx])
            {
                return;
            }

            if (!GetSurfaceColumn(chunk.X + nx, chunk.Z + nz).Exposed)
            {
                return;
            }

            int bottom = _vertices.Count;
            for (int k = from; k < count; k++)
            {
                int top = start + k * stride;
                Vector3 point = _vertices[top];
                point.y -= _printLimits[top].x + SkirtDrop;
                _vertices.Add(point);
                _normals.Add(_normals[top]);
                // The skirt's foot grows with its top, so it stays the same height under the edge
                _printLimits.Add(new Vector4(0f, 0f, 0f, _printLimits[top].w));
            }

            // Both windings: the gap can be seen from either side
            for (int k = from; k < count - 1; k++)
            {
                int a = start + k * stride, b = start + (k + 1) * stride;
                int c = bottom + (k - from), d = bottom + (k - from) + 1;
                _triangles.Add(a); _triangles.Add(b); _triangles.Add(c);
                _triangles.Add(b); _triangles.Add(d); _triangles.Add(c);
                _triangles.Add(a); _triangles.Add(c); _triangles.Add(b);
                _triangles.Add(b); _triangles.Add(c); _triangles.Add(d);
            }
        }

        private static float LimitCompression(float compression, float limit)
        {
            if (limit <= 0f)
            {
                return 0f;
            }

            // Round the transition into the depth limit so the base does not clip flat abruptly
            float blend = limit * 0.3f;
            float overlap = Mathf.Max(0f, blend - Mathf.Abs(compression - limit));
            return Mathf.Min(compression, limit) - overlap * overlap / (4f * blend);
        }

        private void RasterizeFootprint(SnowImpression stamp, Vector2 origin, float now)
        {
            Vector2 center = (stamp.Center - origin) / FineSpacing;
            float radius = stamp.Reach / FineSpacing;
            int minX = Mathf.Max(0, Mathf.FloorToInt(center.x - radius));
            int maxX = Mathf.Min(StampWidth - 1, Mathf.CeilToInt(center.x + radius));
            int minZ = Mathf.Max(0, Mathf.FloorToInt(center.y - radius));
            int maxZ = Mathf.Min(StampWidth - 1, Mathf.CeilToInt(center.y + radius));

            MarkDetailedCells(center - Vector2.one, radius + 1f);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Vector2 point = origin + new Vector2(x * FineSpacing, z * FineSpacing);
                    int index = z * StampWidth + x;
                    _compression[index] = Mathf.Max(_compression[index], stamp.Compression(point, now));
                }
            }
        }

        private void MarkDetailedCells(Vector2 center, float radius)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt((center.x - radius) / FootprintSubdivision));
            int maxX = Mathf.Min(ChunkWidth - 1, Mathf.FloorToInt((center.x + radius) / FootprintSubdivision));
            int minZ = Mathf.Max(0, Mathf.FloorToInt((center.y - radius) / FootprintSubdivision));
            int maxZ = Mathf.Min(ChunkWidth - 1, Mathf.FloorToInt((center.y + radius) / FootprintSubdivision));
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    _detailedCells[z * ChunkWidth + x] = true;
                }
            }
        }

        private static Vector3 Interpolate(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u, float v)
        {
            return u + v <= 1f ? a + u * (c - a) + v * (b - a)
                : d + (1f - u) * (b - d) + (1f - v) * (c - d);
        }

        private static float InterpolateHeight(float a, float b, float c, float d, float u, float v)
        {
            return u + v <= 1f ? a + u * (c - a) + v * (b - a)
                : d + (1f - u) * (b - d) + (1f - v) * (c - d);
        }

        private readonly struct SnowImpression
        {
            public readonly Vector2 Center;
            public readonly float EndTime;
            public readonly bool IsAiStep;
            public readonly Collider RaisedSupport;
            public readonly float SupportHeight;
            private readonly Vector2 _forward;
            private readonly float _depth;
            private readonly float _duration;
            private readonly bool _prone;
            private readonly bool _corpse;
            private readonly float _blastRadius;
            // Tyre track
            private readonly float _trackHalfWidth;
            // Melt (KindGrenadeMelt or KindFireMelt)
            private readonly int _meltKind;

            // Keep these shapes in step with PrintUpdate.shader in the WinterOutbreakShaders project
            private const float ProneForwardReach = 2.30f;
            private const float ProneRearReach = 0.95f;
            private const float ProneNarrowStart = 1.20f;
            private const float ProneMuzzleHalfWidth = 0.30f;

            public float Reach => _trackHalfWidth > 0f ? _forward.magnitude * 0.5f + _trackHalfWidth
                : _blastRadius > 0f ? _blastRadius * 1.3f
                : _corpse ? 1.05f : _prone ? ProneForwardReach : 0.25f;

            public int Kind => _meltKind > 0 ? _meltKind
                : _trackHalfWidth > 0f ? SnowPrintTexture.KindTrack
                : _blastRadius > 0f ? SnowPrintTexture.KindBlast
                : _corpse ? SnowPrintTexture.KindBody : _prone ? SnowPrintTexture.KindProne : SnowPrintTexture.KindBoot;
            public float PeakDepth => _depth;
            public float Duration => _duration;
            public Vector2 Forward => _forward;
            private static float Noise1(float s)
            {
                float i = Mathf.Floor(s);
                float f = s - i;
                f = f * f * (3f - 2f * f);
                return Mathf.Lerp(Hash1(i), Hash1(i + 1f), f);
            }

            private static float Hash1(float n)
            {
                float v = Mathf.Sin(n * 12.9898f) * 43758.5453f;
                return v - Mathf.Floor(v);
            }

            /// <summary>
            /// Blast radius for craters, half width for tracks; what the print shader's stamp z holds.
            /// </summary>
            public float ShapeRadius => _trackHalfWidth > 0f ? _trackHalfWidth : _blastRadius;

            private float TrackDistance(Vector2 point)
            {
                Vector2 start = Center - _forward * 0.5f;
                float t = Mathf.Clamp01(Vector2.Dot(point - start, _forward) / Mathf.Max(_forward.sqrMagnitude, 1e-6f));
                return Vector2.Distance(point, start + _forward * t);
            }

            /// <summary>
            /// True when the point lies within margin of the impression's oriented outline, a box around the shape
            /// Compression draws.
            /// </summary>
            public bool Covers(Vector2 point, float margin)
            {
                Vector2 offset = point - Center;
                if (_trackHalfWidth > 0f)
                {
                    return TrackDistance(point) <= _trackHalfWidth + margin;
                }

                if (_blastRadius > 0f)
                {
                    float reach = Reach + margin;
                    return offset.sqrMagnitude <= reach * reach;
                }

                // Compression measures along the stored forward vector as given
                float scale = Mathf.Max(0.01f, _forward.magnitude);
                float length = Vector2.Dot(offset, _forward);
                float width = Vector2.Dot(offset, new Vector2(_forward.y, -_forward.x));
                float forwardReach, rearReach, halfWidth;
                if (_corpse)
                {
                    forwardReach = 0.85f;
                    rearReach = 1.05f;
                    halfWidth = 0.51f;
                }
                else if (_prone)
                {
                    forwardReach = ProneForwardReach;
                    rearReach = ProneRearReach;
                    halfWidth = 0.62f;
                }
                else
                {
                    forwardReach = 0.22f;
                    rearReach = 0.22f;
                    halfWidth = 0.139f;
                }

                float m = margin * scale;
                return length <= forwardReach + m && length >= -rearReach - m && Mathf.Abs(width) <= halfWidth + m;
            }

            public SnowImpression(Vector2 center, Vector2 forward, float depth, float time,
                float duration, bool prone, float blastRadius = 0f, bool aiStep = false, bool corpse = false,
                Collider raisedSupport = null, float supportHeight = 0f, float trackHalfWidth = 0f, int meltKind = 0)
            {
                _trackHalfWidth = trackHalfWidth;
                _meltKind = meltKind;
                Center = center;
                EndTime = time + duration;
                _forward = forward;
                _depth = depth;
                _duration = duration;
                _prone = prone;
                _blastRadius = blastRadius;
                IsAiStep = aiStep;
                _corpse = corpse;
                RaisedSupport = raisedSupport;
                SupportHeight = supportHeight;
            }

            public float Compression(Vector2 point, float time)
            {
                Vector2 offset = point - Center;
                float remaining = Mathf.Clamp01((EndTime - time) / _duration);
                if (_trackHalfWidth > 0f)
                {
                    // Same groove and tread as the print shader's track stamp
                    float q = TrackDistance(point) / _trackHalfWidth;
                    float groove = Mathf.Clamp01(1f - q * q);
                    groove = groove * groove * (3f - 2f * groove);
                    Vector2 direction = _forward.normalized;
                    float s = Vector2.Dot(point, direction);
                    Vector2 start = Center - _forward * 0.5f;
                    float t = Mathf.Clamp01(Vector2.Dot(point - start, _forward) / Mathf.Max(_forward.sqrMagnitude, 1e-6f));
                    float side = Vector2.Dot(point - (start + _forward * t), new Vector2(direction.y, -direction.x))
                        / _trackHalfWidth;
                    float wander = 0.84f + 0.10f * Noise1(s * 0.9f) + 0.06f * Noise1(s * 3.1f + 17f);
                    float spacing = s / 0.15f + 0.9f * Noise1(s * 1.3f + 5f) + 0.3f * Noise1(s * 4.7f + 11f);
                    float band = Mathf.Repeat(spacing + Mathf.Abs(side) * 0.45f, 1f);
                    float ridge = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.28f, 0.46f, band))
                        - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.60f, 0.80f, band));
                    float strength = 0.07f + 0.14f * Noise1(s * 0.7f + 29f);
                    float tread = 1f - strength * ridge * Mathf.Clamp01(1f - q * q);
                    return _depth * groove * tread * wander * remaining;
                }

                if (_blastRadius > 0f)
                {
                    if (offset.sqrMagnitude >= Reach * Reach)
                    {
                        return 0f;
                    }

                    // Same lobed outline as PrintUpdate.shader
                    float angle = Mathf.Atan2(offset.y, offset.x);
                    float rim = 1f + 0.13f * Mathf.Sin(3f * angle + _forward.x) + 0.08f * Mathf.Sin(5f * angle + _forward.y)
                        + 0.05f * Mathf.Sin(8f * angle + _forward.x * 1.7f + _forward.y);
                    float edge = _blastRadius * rim;
                    if (_meltKind > 0)
                    {
                        float melt = Mathf.Clamp01((edge - offset.magnitude) / (_blastRadius * 0.4f));
                        return _depth * Mathf.SmoothStep(0f, 1f, melt) * remaining;
                    }

                    float bowl = Mathf.Clamp01(1f - offset.sqrMagnitude / (edge * edge));
                    return _depth * Mathf.SmoothStep(0f, 1f, bowl) * remaining;
                }

                float length = Vector2.Dot(offset, _forward);
                float width = Vector2.Dot(offset, new Vector2(_forward.y, -_forward.x));
                float halfLength;
                float halfWidth;
                if (_corpse)
                {
                    halfLength = length >= 0f ? 0.85f : 1.05f;
                    halfWidth = Mathf.Lerp(0.30f, 0.51f, Mathf.InverseLerp(-0.65f, 0.35f, length));
                }
                else if (_prone)
                {
                    halfLength = length >= 0f ? ProneForwardReach : ProneRearReach;
                    halfWidth = Mathf.Lerp(0.46f, 0.62f, Mathf.InverseLerp(-0.55f, 0.55f, length));
                    // Ahead of the shoulders the channel narrows toward the arms and weapon
                    halfWidth = Mathf.Lerp(halfWidth, ProneMuzzleHalfWidth,
                        Mathf.InverseLerp(ProneNarrowStart, ProneNarrowStart + 1f, length));
                }
                else
                {
                    halfLength = 0.22f;
                    halfWidth = Mathf.Lerp(0.107f, 0.139f, Mathf.InverseLerp(-0.12f, 0.12f, length));
                }

                float along = length / halfLength;
                float across = width / halfWidth;
                // Prone keeps its depth further forward (a fourth-power falloff) so the weapon clears the snow
                float alongTerm = _prone && !_corpse && length >= 0f ? along * along * along * along : along * along;
                float profile = Mathf.Clamp01(1f - alongTerm - across * across);
                float shape = profile * profile * (3f - 2f * profile);
                return _depth * shape * remaining;
            }
        }
    }
}
