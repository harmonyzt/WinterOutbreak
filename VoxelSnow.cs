using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    internal sealed partial class VoxelSnowArea
    {
        private const float CellWidth = 0.25f;
        internal const float CountdownDepth = 0.04f;
        private const int FeatherCells = 3;
        private const int GapRepairCells = 2;
        private const float FeatherDistance = 0.54f;
        private const int ChunkWidth = 22;
        private const int CornerWidth = ChunkWidth + 3;
        private const float ChunkSize = ChunkWidth * CellWidth;
        private const int SampleBudget = 256;
        private const double SampleSeconds = 0.002;
        private const double SecondRebuildSeconds = 0.0045;
        private const int CountdownSampleBudget = 2048;
        private const double CountdownSampleSeconds = 0.010;
        private const double CountdownRaisedSeconds = 0.008;
        private const float GroundRise = 0.08f;
        private const float ReleaseMargin = 12f;
        private const float SampleCacheMargin = ChunkSize * 3f;
        private const float LookAheadSeconds = 7f;
        private const float MaxRouteLead = 48f;
        private const float StreamingExtension = 24f;
        private const float ChunkSelectionInterval = 0.20f;
        private const float ImmediateRadius = 14f;
        private const float RouteHalfWidth = 16f;
        private const float NearbyDepthRadius = 24f;
        private const float NearbyDepthStep = 0.005f;
        private const float DistantDepthStep = 0.02f;
        private const float GrowthSeconds = 0.85f;

        private static readonly (int X, int Z, float Distance)[] FeatherOffsets = BuildSurfaceOffsets(FeatherCells, FeatherDistance, 0.5f);

        private const float ShelterTaperReach = 2.10f;
        private const int ShelterTaperCells = 9;
        private const float MoundReach = 1.10f;
        private static readonly (int X, int Z, float Distance)[] GroundEdgeOffsets = BuildSurfaceOffsets(ShelterTaperCells, ShelterTaperReach, 0.5f, MoundReach);

        private const float MinimumDrift = 0.06f;
        private const float MaxDrapeStep = 0.30f;
        private const float LedgeRimWeight = 0f;
        private const float DrapeCover = 0.02f;
        private const float LedgeBankWeight = 1.25f;

        // Snow thins toward it over 55-95 cm, and ground on a raised surface (planter, wall top, building base) holds
        // at most RaisedSurfaceDepth within RaisedFullReach of its rim, fading back to the storm depth by
        // RaisedFadeReach.
        private const float RimDrop = MaxDrapeStep;
        private const int RimCells = 5;
        private const float RimReachMin = 0.55f;
        private const float RimReachMax = 0.95f;
        private const float RaisedSurfaceDepth = 0.12f;
        private const float RaisedFullReach = 0.60f;
        private const float RaisedFadeReach = 1.10f;
        private const float MoundBoost = 0.60f;
        private const float PitDepth = 0.10f;
        private const float MaxUpperLevelRise = 4f;
        private const int StairReach = 2;
        private const float StairSpan = 0.90f;
        private const float StairProbe = 0.30f;
        private const float MaxPitDepth = 0.60f;
        private const int ShapeWalk = 3;
        private const int GapWalk = 5;
        internal const byte ShapeNone = 0, ShapeRidge = 1, ShapeValley = 2, ShapePit = 3, ShapeDeepGap = 4, ShapeStair = 5;
        private const float RidgeTolerance = 0.15f;
        private const float RidgeDrop = 0.50f;
        private const float MaxValleyRise = 1.20f;
        private const float MaxStairGapRise = 2.00f;
        private const float RidgeMaxDrop = 2.00f;
        private const float IndoorVolumeCeiling = 6.00f;
        private const int MaxOverheadHits = 4;
        private const float LowPolyFloorLift = 0.25f;

        private const float WalkSurfaceMinNormal = 0.9f;
        private const float MaxGapDepth = 5f;
        private const float DeepFall = 0.60f;
        private const float MaxSmoothDrift = 0.70f;

        private readonly Vector3 _origin;
        private readonly float _streamRadius;
        private readonly float _releaseRadiusSquared;
        private readonly float _sampleCacheRadiusSquared;
        private readonly int _samplePadding;
        private readonly int _samplingWidth;
        private readonly int _samplesPerChunk;
        private readonly int _terrainMask = LayersMaskController.TerrainMask;
        private readonly int _solidMask = LayersMaskController.HighPolyCollider;
        private readonly int _fallbackGroundMask = LayersMaskController.LowPolyColliderLayerMask;
        private readonly int _doorMask = LayersMaskController.HighPolyCollider | LayersMaskController.LowPolyColliderLayerMask | LayersMaskController.PlayerStaticDoorMask;
        private readonly Dictionary<Vector2Int, Chunk> _chunks = new Dictionary<Vector2Int, Chunk>();
        private readonly List<Chunk> _active = new List<Chunk>();
        private readonly List<Vector2Int> _expiredChunks = new List<Vector2Int>();
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<int> _triangles = new List<int>();
        private readonly Material _material;

        // Terrain-less columns search around a local ground height taken from the static scene, never the player.
        private const float SweepTop = 250f;
        private const float SweepLength = 500f;
        private const float SweepStep = 0.02f;
        private const int SweepHits = 12;
        private const int ReferenceCells = 6;
        private const int ReferenceReach = 3;
        private const float UpperLevelRise = 0.50f;
        private const float RoofJoinNormal = 0.85f;
        private const float RoofJoinTolerance = 0.15f;
        private const int ReferenceCacheLimit = 131072;
        private const float FlatNormal = 0.995f;
        private const float FlatGroundReach = 2f;
        private const float RampGroundReach = 4f;
        private const float ShallowSupportRise = 0.18f;
        private const float BroadSpread = 0.45f;
        private const float LargeSupportWidth = 3f;
        private readonly Dictionary<Collider, bool> _largeSupports = new Dictionary<Collider, bool>();
        private readonly Dictionary<Collider, Bounds[]> _supportRenderers = new Dictionary<Collider, Bounds[]>();
        private readonly List<Bounds> _supportBounds = new List<Bounds>();
        private const int ReferenceSupport = 4;
        private const float ReferenceTolerance = 0.25f;
        private readonly float[] _referenceTops = new float[(2 * ReferenceReach + 1) * (2 * ReferenceReach + 1)];
        private readonly Dictionary<Vector2Int, float> _surfaceTops = new Dictionary<Vector2Int, float>();
        private readonly Dictionary<Vector2Int, float> _groundReferences = new Dictionary<Vector2Int, float>();

        private Chunk _sampling;
        private int _refreshIndex;
        private float _nextCacheSweep;
        private float _nextChunkSelection;
        private bool _footprintDeferred;
        private Vector3 _previousPosition;
        private Vector3 _streamVelocity;

        public bool SuppressIndoorSnow { get; }

        public VoxelSnowArea(Vector3 center, float streamRadius, bool suppressIndoorSnow, int maxFootprintsPerChunk, bool gpuPrints = false)
        {
            _origin = center;
            _streamRadius = streamRadius;
            _maxFootprintsPerChunk = maxFootprintsPerChunk;
            if (gpuPrints)
            {
                _prints = SnowPrintTexture.TryCreate(out string status);
                PrintStatus = status;
                _maxFootprintsPerChunk = _prints != null ? Mathf.Max(maxFootprintsPerChunk, GpuFootprintsPerChunk) : maxFootprintsPerChunk;
            }
            else
            {
                PrintStatus = "GPU snow prints off in the config, using CPU prints";
            }

            SuppressIndoorSnow = suppressIndoorSnow;
            // The surface reads base ground up to FeatherCells + 3 + StairReach + 2 * ShapeWalk cells outside a chunk.
            // The surface cache reaches SurfaceCacheMargin + 1 cells outside the chunk (the rim field needs it).
            int shapeReach = SurfaceCacheMargin + 1 + StairReach + ShapeWalk + GapWalk + 2;
            _samplePadding = Mathf.Max(shapeReach, suppressIndoorSnow
                ? Mathf.Max(FeatherCells + GapRepairCells, ShelterTaperCells) + 1
                // A halo cell's drift field must be exact: the surface cache (rim field) reads SurfaceCacheMargin + 1
                // cells past the chunk, and each of those cells reads openings up to DriftFieldCells away.
                : SurfaceCacheMargin + 1 + DriftFieldCells);
            _samplingWidth = ChunkWidth + 2 * _samplePadding;
            _samplesPerChunk = _samplingWidth * _samplingWidth;
            float releaseRadius = streamRadius + ReleaseMargin;
            _releaseRadiusSquared = releaseRadius * releaseRadius;
            float cacheRadius = releaseRadius + SampleCacheMargin;
            _sampleCacheRadiusSquared = cacheRadius * cacheRadius;
            _material = _prints != null ? _prints.CreateMaterial() : SnowSurface.CreateSnowMaterial();
        }

        private void UpdateStreaming(Vector3 playerPosition, float depth, bool fullyAccumulated, bool preparing)
        {
            if (_material == null)
            {
                return;
            }

            _stairCountdown = preparing;
            _prints?.Update(playerPosition, Time.time);
            if (_prints != null)
            {
                // Snow depth for the shader's smooth growth between rebuilds (snapped like the rebuilt depth).
                Shader.SetGlobalFloat(SnowDepthId, depth < NearbyDepthStep ? 0f : depth);
            }

            RedrawPrintsAfterRecentre();
            UpdateGpuFireMelts();
            Vector3 local = playerPosition - _origin;
            Vector3 travel = PredictTravel(local, Time.deltaTime);
            float lead = travel.magnitude;
            Vector3 direction = lead > 0.001f ? travel / lead : Vector3.zero;
            Vector3 ahead = local + direction * Mathf.Min(lead, StreamingExtension);
            if (depth < NearbyDepthStep)
            {
                depth = 0f;
            }

            float now = Time.time;
            Chunk nearbyGrowth = null;
            float nearestGrowthDistance = NearbyDepthRadius * NearbyDepthRadius;
            Chunk footprint = null;
            float nearestFootprintDistance = float.PositiveInfinity;
            Chunk raisedSampling = null;
            float nearestRaisedSample = _streamRadius * _streamRadius;
            Chunk raisedRefresh = null;
            float nearestRaisedRefresh = nearestRaisedSample;

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Chunk chunk = _active[i];
                float distance = DistanceSquared(chunk, local);
                if (distance > _releaseRadiusSquared && DistanceSquared(chunk, ahead) > _releaseRadiusSquared)
                {
                    ReleaseChunk(chunk);
                    _active.RemoveAt(i);
                    MarkPreviewDirty(chunk.Key);
                    continue;
                }

                if (chunk.GrowthOffset > 0f)
                {
                    float progress = (now - chunk.GrowthStarted) / GrowthSeconds;
                    chunk.GrowthOffset = Mathf.SmoothStep(chunk.GrowthInset, 0f, progress);
                    chunk.Object.transform.position = _origin - Vector3.up * chunk.GrowthOffset;
                }

                if (distance < nearestGrowthDistance && NeedsDepthRefresh(chunk.Depth, depth,
                        _prints != null ? GpuNearbyDepthStep : NearbyDepthStep, fullyAccumulated, _prints != null))
                {
                    nearbyGrowth = chunk;
                    nearestGrowthDistance = distance;
                }

                if (chunk.FootprintsDirty && distance < nearestFootprintDistance)
                {
                    footprint = chunk;
                    nearestFootprintDistance = distance;
                }

                if (chunk.Depth > 0f && !chunk.RaisedReady && distance < nearestRaisedSample)
                {
                    raisedSampling = chunk;
                    nearestRaisedSample = distance;
                }

                if (chunk.RaisedDirty && distance < nearestRaisedRefresh)
                {
                    raisedRefresh = chunk;
                    nearestRaisedRefresh = distance;
                }
            }

            long rebuildDeadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * SecondRebuildSeconds);
            UpdateGroundPreview(local, ahead, direction, lead, depth, fullyAccumulated, now, preparing);

            if (_sampling != null && DistanceSquared(_sampling, local) > _releaseRadiusSquared && DistanceSquared(_sampling, ahead) > _releaseRadiusSquared)
            {
                _sampling = null;
                _nextChunkSelection = 0f;
            }

            if (now >= _nextCacheSweep)
            {
                _nextCacheSweep = now + 2f;
                TrimSampleCache(local, ahead, now);
            }

            if (now >= _nextChunkSelection && (_sampling == null || _sampling.NextSample < _samplesPerChunk))
            {
                _nextChunkSelection = now + ChunkSelectionInterval;
                Chunk preferred = FindPreferredChunk(local, ahead, direction, lead);
                if (_sampling == null || (preferred != null && preferred != _sampling && StreamPriority(ChunkOffset(preferred.Key, local), direction, lead) + 16f
                        < StreamPriority(ChunkOffset(_sampling.Key, local), direction, lead)))
                {
                    _sampling = preferred;
                }
            }

            if (_sampling != null && SampleChunk(_sampling, preparing))
            {
                QueueForPublish();
            }

            if (preparing && raisedSampling != null)
            {
                SampleRaisedChunk(raisedSampling, Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * CountdownRaisedSeconds), true);
            }
            else if (raisedSampling != null && Stopwatch.GetTimestamp() < rebuildDeadline)
            {
                SampleRaisedChunk(raisedSampling, rebuildDeadline);
            }

            long preparationStarted = Stopwatch.GetTimestamp();
            Chunk ready = AdvancePublishPreparation(local, ahead, direction, lead, preparing);
            // Preparation has its own budget; it must not use up the time growth and refill rebuilds wait for.
            rebuildDeadline += Stopwatch.GetTimestamp() - preparationStarted;
            bool sampled = ready != null;
            if (sampled && (footprint == null || !_footprintDeferred))
            {
                PublishChunk(ready, depth);
                _footprintDeferred = footprint != null;
                if (footprint != null && Stopwatch.GetTimestamp() < rebuildDeadline)
                {
                    RebuildChunk(footprint, depth, "footprints");
                    _footprintDeferred = false;
                }

                return;
            }

            // Give footprints the next turn if sampling and the first rebuild used the time target.
            _footprintDeferred = false;
            if (footprint != null)
            {
                RebuildChunk(footprint, depth, "footprints");
                if (sampled && Stopwatch.GetTimestamp() < rebuildDeadline)
                {
                    PublishChunk(ready, depth);
                }

                return;
            }

            if (nearbyGrowth != null)
            {
                RebuildChunk(nearbyGrowth, depth, "nearby growth");
                return;
            }

            if (raisedRefresh != null)
            {
                RebuildChunk(raisedRefresh, depth, "raised refresh");
                return;
            }

            // Spend remaining rebuild time on distant growth and impression refill. This pass is the lowest priority,
            // so it waits for a frame where sampling and the higher passes left time in the target.
            if (Stopwatch.GetTimestamp() >= rebuildDeadline)
            {
                return;
            }

            for (int i = 0; i < _active.Count; i++)
            {
                _refreshIndex %= _active.Count;
                var chunk = _active[_refreshIndex++];
                bool refill = now >= chunk.FootprintRefresh;
                if (refill || NeedsDepthRefresh(chunk.Depth, depth, _prints != null ? GpuDistantDepthStep : DistantDepthStep, fullyAccumulated, _prints != null))
                {
                    RebuildChunk(chunk, depth, refill ? "footprint refill" : "distant growth");
                    break;
                }
            }
        }

        private const double PrepareSliceSeconds = 0.0022;
        private const double BacklogPrepareSliceSeconds = 0.0045;
        private const double CountdownPrepareSliceSeconds = 0.010;
        private readonly List<Chunk> _publishQueue = new List<Chunk>();

        private void QueueForPublish()
        {
            Chunk chunk = _sampling;
            _sampling = null;
            _nextChunkSelection = 0f;
            if (chunk.PublishQueued || chunk.Active)
            {
                return;
            }

            // Refresh before the surface is prepared. A neighbour's scan has usually cached this chunk's drift field
            // from a partly sampled region; preparing first drew that field, or mixed it with a mid-scan recompute.
            RefreshDriftNear(chunk.Key);
            chunk.PublishQueued = true;
            _publishQueue.Add(chunk);
        }

        /// <summary>
        /// Prepares the queued chunk the player most needs (nearest, or ahead along the route) for a slice of this
        /// frame; returns it once it is ready.
        /// </summary>
        private Chunk AdvancePublishPreparation(Vector3 local, Vector3 ahead, Vector3 direction, float lead, bool preparing)
        {
            Chunk best = null;
            float bestPriority = float.PositiveInfinity;
            for (int i = _publishQueue.Count - 1; i >= 0; i--)
            {
                Chunk chunk = _publishQueue[i];
                if (DistanceSquared(chunk, local) > _releaseRadiusSquared && DistanceSquared(chunk, ahead) > _releaseRadiusSquared)
                {
                    // Start over if it comes back: its samples may be dropped from the cache meanwhile.
                    chunk.PublishQueued = false;
                    chunk.PrepareStep = 0;
                    _publishQueue.RemoveAt(i);
                    continue;
                }

                float priority = StreamPriority(ChunkOffset(chunk.Key, local), direction, lead);
                if (priority < bestPriority)
                {
                    bestPriority = priority;
                    best = chunk;
                }
            }

            if (best != null)
            {
                double seconds = preparing ? CountdownPrepareSliceSeconds : _publishQueue.Count > 1 ? BacklogPrepareSliceSeconds : PrepareSliceSeconds;
                long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * seconds);
                return PrepareSurfaceSlice(best, deadline) ? best : null;
            }

            // Nothing to publish: prepare again, ahead of their next rebuild, live chunks whose surface a drift refresh
            // invalidated. A rebuild that comes first finishes the rest itself.
            for (int i = 0; i < _active.Count; i++)
            {
                _reprepareIndex %= _active.Count;
                Chunk chunk = _active[_reprepareIndex];
                if (!chunk.SurfacePrepared && chunk.Columns != null)
                {
                    double seconds = preparing ? CountdownPrepareSliceSeconds : PrepareSliceSeconds;
                    PrepareSurfaceSlice(chunk, Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * seconds));
                    break;
                }

                _reprepareIndex++;
            }

            return null;
        }

        private int _reprepareIndex;

        private void PublishChunk(Chunk chunk, float depth)
        {
            _publishQueue.Remove(chunk);
            chunk.PublishQueued = false;
            chunk.Active = true;
            _active.Add(chunk);
            RebuildChunk(chunk, DetailStartingDepth(chunk.Key, depth), "publish");
            MarkPreviewDirty(chunk.Key, published: true);
        }

        // With GPU prints the shader grows the snow between rebuilds, so rebuilds only correct the shape every few
        // centimetres.
        private const float GpuNearbyDepthStep = 0.03f;
        private const float GpuDistantDepthStep = 0.06f;
        private static readonly int BuiltDepthId = Shader.PropertyToID("_WSBuiltDepth");
        private static readonly int SnowDepthId = Shader.PropertyToID("_WSSnowDepth");
        private MaterialPropertyBlock _growthBlock;

        private static bool NeedsDepthRefresh(float rendered, float depth, float step, bool fullyAccumulated, bool smoothGrowth = false)
        {
            if (depth == 0f || rendered == 0f || fullyAccumulated || (!smoothGrowth && depth < rendered))
            {
                return Mathf.Abs(depth - rendered) >= 0.0005f;
            }

            return Mathf.Abs(depth - rendered) >= step;
        }

        private void TrimSampleCache(Vector3 local, Vector3 ahead, float now)
        {
            _expiredChunks.Clear();
            foreach (var entry in _chunks)
            {
                Chunk chunk = entry.Value;
                if (chunk.Active)
                {
                    continue;
                }

                if (chunk.Footprints != null)
                {
                    ExpireFootprints(chunk, now);
                }

                if (chunk == _sampling || DistanceSquared(chunk, local) <= _sampleCacheRadiusSquared || DistanceSquared(chunk, ahead) <= _sampleCacheRadiusSquared)
                {
                    continue;
                }

                if (chunk.Columns != null || chunk.NextSample != 0)
                {
                    chunk.Columns = null;
                    chunk.RaisedSurfaces = null;
                    chunk.RaisedNextSample = 0;
                    chunk.RaisedDirty = false;
                    InvalidateSampleHalo(entry.Key);
                }

                if (chunk.Footprints == null)
                {
                    _expiredChunks.Add(entry.Key);
                }
            }

            foreach (var key in _expiredChunks)
            {
                _chunks.Remove(key);
            }

            // Keep the per-collider classification caches bounded on longer routes.
            if (_rockSupports.Count > 4096)
            {
                _rockSupports.Clear();
            }

            if (_raisedDustOnly.Count > 4096)
            {
                _raisedDustOnly.Clear();
            }

            if (_outdoorProps.Count > 4096)
            {
                _outdoorProps.Clear();
            }

            if (_propModels.Count > 4096)
            {
                _propModels.Clear();
            }

            if (_pavementSupports.Count > 4096)
            {
                _pavementSupports.Clear();
            }

            if (_largeSupports.Count > 4096)
            {
                _largeSupports.Clear();
            }

            if (_softSurfaces.Count > 4096)
            {
                _softSurfaces.Clear();
            }

            if (_supportRenderers.Count > 4096)
            {
                _supportRenderers.Clear();
            }

            // Both caches depend only on static geometry, so clearing them never changes a later sample.
            if (_surfaceTops.Count > ReferenceCacheLimit || _groundReferences.Count > ReferenceCacheLimit)
            {
                _surfaceTops.Clear();
                _groundReferences.Clear();
            }
        }

        private void InvalidateSampleHalo(Vector2Int key)
        {
            // Each sampled chunk reads into its neighbors. The cache margin keeps all live halos inside it.
            for (int z = key.y - 1; z <= key.y + 1; z++)
            {
                for (int x = key.x - 1; x <= key.x + 1; x++)
                {
                    if (_chunks.TryGetValue(new Vector2Int(x, z), out var neighbor))
                    {
                        neighbor.NextSample = 0;
                        neighbor.Corners = null;
                        neighbor.PreparedCorners = null;
                        neighbor.SurfacePrepared = false;
                    }
                }
            }
        }

        internal bool HasFootstepSnow(Vector3 position)
        {
            Vector3 local = position - _origin;
            return TryGetRaisedFootSupport(local, out _, out _) || (TryGetSnowHeights(local, out float ground, out float top) && local.y >= ground - 0.15f
                && local.y <= ground + 0.2f && top - ground > 0.002f);
        }

        public bool TryGetDepth(Vector3 feetPosition, out float depth)
        {
            depth = 0f;
            Vector3 local = feetPosition - _origin;
            if (!TryGetSnowHeights(local, out float ground, out float top) || local.y < ground - 0.15f || local.y > top + 0.35f)
            {
                return false;
            }

            depth = Mathf.Max(0f, top - ground);
            return depth > 0f;
        }

        /// <summary>
        /// Undisturbed ground snow at a point: the world height of the ground under it and the snow depth there. Prints
        /// and hollows are not included.
        /// </summary>
        public bool TryGetGroundSnow(Vector3 position, out float ground, out float depth)
        {
            ground = 0f;
            depth = 0f;
            Vector3 local = position - _origin;
            if (!TryGetSnowHeights(local, out float localGround, out float top))
            {
                return false;
            }

            ground = localGround + _origin.y;
            depth = Mathf.Max(0f, top - localGround);
            return true;
        }

        private bool TryGetSnowHeights(Vector3 local, out float ground, out float top)
        {
            ground = 0f;
            top = 0f;
            int x = Mathf.FloorToInt(local.x / CellWidth);
            int z = Mathf.FloorToInt(local.z / CellWidth);
            Chunk chunk = GetChunk(x, z);
            if (chunk == null || !chunk.Active || chunk.Mesh == null || chunk.Corners == null || chunk.Depth <= 0f)
            {
                return false;
            }

            Column column = GetSurfaceColumn(x, z);
            if (!column.Exposed)
            {
                return false;
            }

            int corner = (z - chunk.Z + 1) * CornerWidth + x - chunk.X + 1;
            float u = local.x / CellWidth - x;
            float v = local.z / CellWidth - z;
            float a = CellVertexHeight(column, chunk.Corners[corner], chunk.Depth, 0.5f, 0.5f, x, z);
            float b = CellVertexHeight(column, chunk.Corners[corner + CornerWidth], chunk.Depth, 0.5f, -0.5f, x, z + 1);
            float c = CellVertexHeight(column, chunk.Corners[corner + 1], chunk.Depth, -0.5f, 0.5f, x + 1, z);
            float d = CellVertexHeight(column, chunk.Corners[corner + CornerWidth + 1], chunk.Depth, -0.5f, -0.5f, x + 1, z + 1);
            top = InterpolateHeight(a, b, c, d, u, v);
            if (TryGetGroundTransition(chunk, corner, column, new Vector4(a, b, c, d), out var transition))
            {
                top = transition.Height(u, v);
            }

            top -= chunk.GrowthOffset;
            ground = ProjectGroundHeight(column, 0.5f - u, 0.5f - v);
            float melt = FireMeltFactor(local.x, local.z);
            if (melt < 1f)
            {
                top = ground + Mathf.Max(0f, top - ground) * melt;
            }

            return true;
        }

        private Vector3 PredictTravel(Vector3 local, float elapsed)
        {
            Vector3 travel = local - _previousPosition;
            _previousPosition = local;
            travel.y = 0f;

            if (travel.sqrMagnitude > ChunkSize * ChunkSize)
            {
                _streamVelocity = Vector3.zero;
            }
            else if (elapsed > 0f)
            {
                Vector3 velocity = Vector3.ClampMagnitude(travel / elapsed, MaxRouteLead / LookAheadSeconds);
                _streamVelocity = Vector3.Lerp(_streamVelocity, velocity, 1f - Mathf.Exp(-8f * elapsed));
            }

            return _streamVelocity * LookAheadSeconds;
        }

        private Chunk FindPreferredChunk(Vector3 local, Vector3 ahead, Vector3 direction, float lead)
        {
            int minX = Mathf.FloorToInt((Mathf.Min(local.x, ahead.x) - _streamRadius) / ChunkSize);
            int maxX = Mathf.FloorToInt((Mathf.Max(local.x, ahead.x) + _streamRadius) / ChunkSize);
            int minZ = Mathf.FloorToInt((Mathf.Min(local.z, ahead.z) - _streamRadius) / ChunkSize);
            int maxZ = Mathf.FloorToInt((Mathf.Max(local.z, ahead.z) + _streamRadius) / ChunkSize);

            Vector2Int bestKey = default;
            bool found = false;
            float radiusSquared = _streamRadius * _streamRadius;
            float nearestPriority = float.PositiveInfinity;
            var leadOffset = new Vector2(ahead.x - local.x, ahead.z - local.z);
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var key = new Vector2Int(x, z);
                    Vector2 offset = ChunkOffset(key, local);
                    float distance = offset.sqrMagnitude;
                    float aheadDistance = (offset - leadOffset).sqrMagnitude;
                    if (Mathf.Min(distance, aheadDistance) > radiusSquared)
                    {
                        continue;
                    }

                    float priority = StreamPriority(offset, direction, lead);
                    if (priority < nearestPriority && (!_chunks.TryGetValue(key, out var chunk) || (!chunk.Active && !chunk.PublishQueued)))
                    {
                        bestKey = key;
                        found = true;
                        nearestPriority = priority;
                    }
                }
            }

            return found ? GetOrCreateChunk(bestKey) : null;
        }

        private static float StreamPriority(Vector2 offset, Vector3 direction, float lead)
        {
            float distance = offset.sqrMagnitude;
            const float immediateSquared = ImmediateRadius * ImmediateRadius;
            if (lead < ImmediateRadius || distance <= immediateSquared)
            {
                return distance;
            }

            float forward = offset.x * direction.x + offset.y * direction.z;
            float lateral = Mathf.Max(0f, distance - forward * forward);
            if (forward > 0f && forward <= lead && lateral <= RouteHalfWidth * RouteHalfWidth)
            {
                return immediateSquared + distance;
            }

            // Finish the strip ahead before spending time on the rest of the streaming radius.
            const float routePriorityLimit = MaxRouteLead * MaxRouteLead + RouteHalfWidth * RouteHalfWidth;
            return immediateSquared + routePriorityLimit + distance;
        }

        private static float DistanceSquared(Chunk chunk, Vector3 local)
        {
            return ChunkOffset(chunk.Key, local).sqrMagnitude;
        }

        private static Vector2 ChunkOffset(Vector2Int key, Vector3 local)
        {
            return new Vector2((key.x + 0.5f) * ChunkSize - local.x, (key.y + 0.5f) * ChunkSize - local.z);
        }

        private static Vector2Int KeyForCell(int x, int z)
        {
            int chunkX = x >= 0 ? x / ChunkWidth : (x + 1) / ChunkWidth - 1;
            int chunkZ = z >= 0 ? z / ChunkWidth : (z + 1) / ChunkWidth - 1;
            return new Vector2Int(chunkX, chunkZ);
        }

        private Chunk GetOrCreateChunk(Vector2Int key)
        {
            if (!_chunks.TryGetValue(key, out var chunk))
            {
                chunk = new Chunk(key);
                _chunks.Add(key, chunk);
            }

            return chunk;
        }

        private Chunk GetChunk(int x, int z)
        {
            return _chunks.TryGetValue(KeyForCell(x, z), out var chunk) ? chunk : null;
        }

        private Column GetColumn(int x, int z)
        {
            Chunk chunk = GetChunk(x, z);
            return chunk?.Columns == null ? default : chunk.Columns[chunk.ColumnIndex(x, z)];
        }

        private Column GetSurfaceColumn(int x, int z)
        {
            Column column = GetColumn(x, z);
            if (!column.Sampled)
            {
                return column;
            }

            // The snow surface rests on the filled ground. Shelter, drift sources and raised caps keep the sample.
            if (column.HasGround)
            {
                float ground = GetSurfaceGround(x, z, ref column);
                if (column.PitFilled)
                {
                    column.GroundHeight = ground;
                    column.Normal = column.SurfaceNormal;
                }
            }

            if (column.Exposed)
            {
                // Bunker stairwells and trenches keep the game's own winter shading. The stored sample stays open, so
                // the bunker's doorway drift still finds its opening there.
                if (!column.Sheltered && IsTrenchHidden(x, z, column))
                {
                    column.Exposed = false;
                }

                return column;
            }

            if (column.Sheltered)
            {
                if (!SuppressIndoorSnow && column.HasGround && column.Normal.y >= 0.65f)
                {
                    float drift = GetDriftField(x, z);
                    column.Exposed = drift > 0f;
                    column.DoorDriftWeight = drift;
                }

                // A sheltered floor without drift shows the exposed roof or deck above it instead.
                return column.Exposed || !column.HasUpperLevel || !UpperLevelShared(x, z, column.UpperHeight) ? column : UpperLevel(column);
            }

            // Only close narrow gaps bracketed by compatible, directly sampled ground.
            if (TryBridgeAlongAxis(x, z, 1, 0, column, out var bridge) || TryBridgeAlongAxis(x, z, 0, 1, column, out bridge))
            {
                return bridge;
            }

            return column;
        }

        // A floor sample well below nearly all of its neighbors fell through a gap, such as the space between two stair
        // treads. Snow bridges it at the neighbors' height instead of dropping a pit into the surface.
        private float GetSurfaceGround(int x, int z, ref Column copy)
        {
            if (copy.SurfaceGroundResolved)
            {
                return copy.SurfaceGround;
            }

            Chunk owner = GetChunk(x, z);
            ref Column column = ref owner.Columns[owner.ColumnIndex(x, z)];
            TryGetBaseGround(x, z, out float own);
            float higher = 0f;
            int higherCount = 0;
            int grounded = 0;
            float deep = 0f;
            int deepCount = 0;
            float deepLow = float.PositiveInfinity;
            float deepHigh = float.NegativeInfinity;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    if (!TryGetBaseGround(x + dx, z + dz, out float neighborGround))
                    {
                        continue;
                    }

                    grounded++;

                    // Only a tread or curb height counts. A neighbor whose sample is a roof or an upper floor is
                    // another level, not the rim of a gap.
                    float rise = neighborGround - own;
                    if (rise > PitDepth && rise <= MaxPitDepth)
                    {
                        higher += neighborGround;
                        higherCount++;
                    }
                    else if (rise > MaxPitDepth && rise <= MaxGapDepth)
                    {
                        // A drain slot or the gap beside a planter: the sample fell metres to something below.
                        deep += neighborGround;
                        deepCount++;
                        deepLow = Mathf.Min(deepLow, neighborGround);
                        deepHigh = Mathf.Max(deepHigh, neighborGround);
                    }
                }
            }

            // The lake ice is flat. A cell on a measured flight already lies on the flight's slope
            // (VoxelStairFlights.cs).
            bool stairOwned = GetColumn(x, z).StairOwned;
            bool noStairShaping = FrozenIce.IsIceTop(_origin.x + (x + 0.5f) * CellWidth, _origin.z + (z + 0.5f) * CellWidth, _origin.y + own) || stairOwned;
            float stairGround = own;
            Vector3 stairNormal = Vector3.up;
            bool probed = !noStairShaping && TryProbeStairs(x, z, own, out stairGround, out stairNormal);

            if (probed || (!noStairShaping && TryFitStairs(x, z, own, out stairGround, out stairNormal)))
            {
                column = ref owner.Columns[owner.ColumnIndex(x, z)];
                column.PitFilled = true;
                column.SurfaceGround = stairGround;
                column.SurfaceNormal = stairNormal;
                column.ShapeKind = probed ? ShapeStairProbe : ShapeStair;
            }
            else
            {
                // Flight cells take no pit or deep-gap fill either.
                column.PitFilled = !stairOwned && grounded >= 7 && higherCount >= 6;
                column.ShapeKind = column.PitFilled ? ShapePit : ShapeNone;
                column.SurfaceGround = column.PitFilled ? higher / higherCount : own;
                column.SurfaceNormal = Vector3.up;

                // A deep, narrow gap is bridged at the level of the ground around it, when that ground agrees. Only for
                // samples that fell below the local street level, not for street cells between platforms.
                if (!column.PitFilled && !stairOwned && grounded >= 7 && deepCount >= 3 && deepCount + higherCount >= 6 && deepHigh - deepLow <= 0.30f
                    && _origin.y + own < GetGroundReference(GroundPoint(x, z, 0f)) - DeepFall)
                {
                    column.PitFilled = true;
                    column.SurfaceGround = (deep + higher) / (deepCount + higherCount);
                    column.ShapeKind = ShapeDeepGap;
                }

                // A sample reshaped from its line of neighbors (a narrow strip or a fall-through gap) moves too.
                if (!column.PitFilled && column.BaseShaped)
                {
                    column.PitFilled = true;
                    column.ShapeKind = column.BaseKind;
                }
            }

            column.SurfaceGroundResolved = true;
            copy.PitFilled = column.PitFilled;
            copy.SurfaceGround = column.SurfaceGround;
            copy.SurfaceNormal = column.SurfaceNormal;
            copy.SurfaceGroundResolved = true;
            return column.SurfaceGround;
        }

        // The ground a cell's surface starts from, before pit filling and the stair fit. A raw sample is reshaped from
        // the line of cells through it:
        // - A strip at most 0.75 m wide standing 0.5 m or more above real ground on both sides, such as a wagon
        //   underframe beam or the top of a railing, is lowered to that ground. Large architecture otherwise made it a
        //   row of floating snow plates.
        // - A gap at most 0.75 m wide between two higher samples, such as the street seen between the thin treads of an
        //   open staircase, is raised to the line between them.
        private bool TryGetBaseGround(int x, int z, out float height)
        {
            Chunk owner = GetChunk(x, z);
            if (owner?.Columns == null)
            {
                height = 0f;
                return false;
            }

            ref Column column = ref owner.Columns[owner.ColumnIndex(x, z)];
            if (!column.Sampled || !column.HasGround)
            {
                height = 0f;
                return false;
            }

            if (!column.BaseGroundResolved)
            {
                float own = column.GroundHeight;
                // A strip narrow along a grid axis is lowered. One narrow only diagonally (a wagon beam at an angle) is
                // lowered only when its same-height neighbours along the axes are strips too. Otherwise it is the
                // corner of something wide, such as a wall end or a building's base, and lowering it alone tore the
                // snow into a flap. Frozen lake ice is flat: no ridges or gaps to shape.
                bool onIce = FrozenIce.IsIceTop(_origin.x + (x + 0.5f) * CellWidth, _origin.z + (z + 0.5f) * CellWidth, _origin.y + own);

                // Gap filling still moved some of them toward the walls beside narrow stairs.
                if (column.StairOwned)
                {
                    column.BaseShaped = false;
                    column.BaseKind = ShapeNone;
                    column.BaseGround = own;
                    column.BaseGroundResolved = true;
                    height = own;
                    return true;
                }

                float ground = own;
                bool ridge = !onIce && (TryLowerSpike(x, z, own, out ground) || TryLowerRidge(x, z, own, out ground, true)
                                                                             || (TryLowerRidge(x, z, own, out ground, false) && DiagonalRidgeIsStrip(x, z, own)));
                bool shaped = ridge || (!onIce ? TryFillValley(x, z, own, out ground) : TryFillDeckGap(x, z, own, out ground));
                if (onIce && shaped)
                {
                    // A filled plank gap is part of the deck now, so its corners keep the deck rule.
                    column.OnTerrain = false;
                }

                column.BaseShaped = shaped;
                column.BaseKind = !shaped ? ShapeNone : ridge ? ShapeRidge : ShapeValley;
                column.BaseGround = shaped ? ground : own;
                column.BaseGroundResolved = true;
            }

            height = column.BaseGround;
            return true;
        }

        // A single cell standing 0.3-1.5 m above every neighbour on its level, such as a wall plinth ledge taken as
        // ground beside a barracks door, pulled the snow up into a standing sheet. It is lowered to the average of its
        // neighbours. Neighbours more than 1.5 m above are another level (a roof edge) and do not count either way.
        private bool TryLowerSpike(int x, int z, float own, out float ground)
        {
            ground = own;
            float sum = 0f;
            int lower = 0;
            int level = 0;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    Column neighbour = GetColumn(x + dx, z + dz);
                    if (!neighbour.Sampled || !neighbour.HasGround)
                    {
                        continue;
                    }

                    float drop = own - neighbour.GroundHeight;
                    if (drop < -1.5f)
                    {
                        continue;
                    }

                    // One neighbour on the same level is allowed: a ledge two cells wide.
                    if (Mathf.Abs(drop) < 0.3f && level == 0)
                    {
                        level++;
                        continue;
                    }

                    if (drop < 0.3f || drop > 1.5f)
                    {
                        return false;
                    }

                    sum += neighbour.GroundHeight;
                    lower++;
                }
            }

            // Four is enough: beside a door the other neighbours are often the roof edge or unsampled.
            if (lower < 4)
            {
                return false;
            }

            ground = sum / lower;
            return true;
        }

        private bool TryGetRawGround(int x, int z, out float height)
        {
            Column column = GetColumn(x, z);
            height = column.GroundHeight;
            return column.Sampled && column.HasGround;
        }

        private static readonly Vector2Int[] RidgeAxes = { new Vector2Int(1, 0), new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, -1) };

        private bool DiagonalRidgeIsStrip(int x, int z, float own)
        {
            for (int i = 0; i < 4; i++)
            {
                int nx = x + (i == 0 ? 1 : i == 1 ? -1 : 0);
                int nz = z + (i == 2 ? 1 : i == 3 ? -1 : 0);
                if (TryGetRawGround(nx, nz, out float neighbor) && Mathf.Abs(neighbor - own) <= RidgeTolerance && !TryLowerRidge(nx, nz, neighbor, out _, false))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryLowerRidge(int x, int z, float own, out float ground, bool axesOnly = false)
        {
            ground = own;
            int narrowest = int.MaxValue;
            foreach (var axis in RidgeAxes)
            {
                if (axesOnly && axis.x != 0 && axis.y != 0)
                {
                    continue;
                }

                if (!TryFindRidgeEnd(x, z, axis.x, axis.y, own, out int k1, out float h1, out int x1, out int z1)
                    || !TryFindRidgeEnd(x, z, -axis.x, -axis.y, own, out int k2, out float h2, out int x2, out int z2))
                {
                    continue;
                }

                // The low side must be real ground, not the gap between two stair treads.
                int width = k1 + k2 - 1;
                if (width > ShapeWalk || width >= narrowest || TryFillValley(x1, z1, h1, out _) || TryFillValley(x2, z2, h2, out _))
                {
                    continue;
                }

                narrowest = width;
                ground = (h1 * k2 + h2 * k1) / (k1 + k2);
            }

            return narrowest != int.MaxValue;
        }

        private bool TryFindRidgeEnd(int x, int z, int dx, int dz, float own, out int steps, out float end, out int endX, out int endZ)
        {
            for (steps = 1; steps <= ShapeWalk; steps++)
            {
                endX = x + dx * steps;
                endZ = z + dz * steps;
                if (!TryGetRawGround(endX, endZ, out end))
                {
                    return false;
                }

                if (Mathf.Abs(end - own) > RidgeTolerance)
                {
                    // Lower only onto nearby ground, never into a void beside a landing.
                    return own - end >= RidgeDrop && own - end <= RidgeMaxDrop;
                }
            }

            endX = endZ = 0;
            end = 0f;
            return false;
        }

        private bool TryFillValley(int x, int z, float own, out float ground)
        {
            ground = own;
            int narrowest = int.MaxValue;

            // Terrain at the foot of a floor raised off the ground is real ground, not a fall-through gap. Filling it
            // toward the floor of the Woods swamp and sawmill trailers (1 m up, under their roofs) hung slanted sheets
            // of snow under them. Stairs climbing under a roof are not terrain and still fill.
            bool onTerrain = GetColumn(x, z).OnTerrain;
            for (int axis = 0; axis < 2; axis++)
            {
                int dx = axis == 0 ? 1 : 0;
                int dz = 1 - dx;
                if (!TryFindValleyEnd(x, z, dx, dz, own, out int k1, out float h1) || !TryFindValleyEnd(x, z, -dx, -dz, own, out int k2, out float h2))
                {
                    continue;
                }

                if (onTerrain && IsFootOfRaisedFloor(x + dx * k1, z + dz * k1, h1, x - dx * k2, z - dz * k2, h2, own))
                {
                    continue;
                }

                int width = k1 + k2 - 1;
                if (width > GapWalk || width >= narrowest)
                {
                    continue;
                }

                // The top treads of the long Sandbox stairs returned only the street over five rows, and their snow
                // ended in an overhanging slab.
                if (width > ShapeWalk)
                {
                    float spread = Mathf.Abs(h1 - h2);
                    if (spread < 0.30f || spread / ((k1 + k2) * CellWidth) > 1f)
                    {
                        continue;
                    }

                    narrowest = width;
                    ground = (h1 * k2 + h2 * k1) / (k1 + k2);
                    continue;
                }

                // Deeper gaps are filled only on stairs: the two sides differ in height, no steeper than stairs. Two
                // beams of a wagon underframe stand at the same height and keep the ground between them.
                bool shallow = h1 - own <= MaxValleyRise && h2 - own <= MaxValleyRise;
                bool stairLike = Mathf.Abs(h1 - h2) >= 0.10f && Mathf.Abs(h1 - h2) / ((k1 + k2) * CellWidth) <= 1f;
                if (!shallow && !stairLike)
                {
                    continue;
                }

                narrowest = width;
                ground = (h1 * k2 + h2 * k1) / (k1 + k2);
            }

            return narrowest != int.MaxValue;
        }

        // A gap of one or two cells between planks of a pier deck over the frozen lake or sea: the sample fell through
        // to the ice, and the deck snow dropped 30-50 cm into every gap. Snow bridges it at deck height when both sides
        // are deck at the same height. Wider holes, such as missing planks, and the ice beside a pier stay open.
        private bool TryFillDeckGap(int x, int z, float own, out float ground)
        {
            const int maxWidth = 2;
            const float deckTolerance = 0.06f;
            ground = own;
            bool filled = false;
            for (int axis = 0; axis < 2; axis++)
            {
                int dx = axis == 0 ? 1 : 0;
                int dz = 1 - dx;
                if (!TryFindDeckSide(x, z, dx, dz, own, out int k1, out float h1) || !TryFindDeckSide(x, z, -dx, -dz, own, out int k2, out float h2) || k1 + k2 - 1 > maxWidth
                    || Mathf.Abs(h1 - h2) > deckTolerance)
                {
                    continue;
                }

                float height = (h1 * k2 + h2 * k1) / (k1 + k2);
                if (!filled || height > ground)
                {
                    ground = height;
                    filled = true;
                }
            }

            return filled;
        }

        private bool TryFindDeckSide(int x, int z, int dx, int dz, float own, out int steps, out float side)
        {
            for (steps = 1; steps <= 2; steps++)
            {
                int sideX = x + dx * steps;
                int sideZ = z + dz * steps;
                if (!TryGetRawGround(sideX, sideZ, out side))
                {
                    return false;
                }

                float rise = side - own;
                if (rise > PitDepth)
                {
                    return rise <= 0.60f && FrozenIce.IsLakeDeck(_origin.x + (sideX + 0.5f) * CellWidth, _origin.z + (sideZ + 0.5f) * CellWidth, _origin.y + side);
                }
            }

            side = 0f;
            return false;
        }

        // A "valley" whose one side is terrain barely higher than the cell and whose other side is a non-terrain floor
        // well above it is the foot of a room on wheels or stilts, which snow does not fill up to. Bridges have no roof
        // and the ice is left out.
        private bool IsFootOfRaisedFloor(int ax, int az, float ha, int bx, int bz, float hb, float own)
        {
            const float footRise = 0.25f;
            const float floorRise = 0.60f;
            bool aLow = ha < hb;
            int lowX = aLow ? ax : bx;
            int lowZ = aLow ? az : bz;
            int highX = aLow ? bx : ax;
            int highZ = aLow ? bz : az;
            float low = Mathf.Min(ha, hb) - own;
            float high = Mathf.Max(ha, hb);
            if (low >= footRise || high - own <= floorRise || !GetColumn(lowX, lowZ).OnTerrain)
            {
                return false;
            }

            Column floor = GetColumn(highX, highZ);
            return !floor.OnTerrain && !FrozenIce.IsIceTop(_origin.x + (highX + 0.5f) * CellWidth, _origin.z + (highZ + 0.5f) * CellWidth, _origin.y + high);
        }

        private bool TryFindValleyEnd(int x, int z, int dx, int dz, float own, out int steps, out float end)
        {
            for (steps = 1; steps <= GapWalk; steps++)
            {
                if (!TryGetRawGround(x + dx * steps, z + dz * steps, out end))
                {
                    return false;
                }

                float rise = end - own;
                if (rise > PitDepth)
                {
                    return rise <= MaxStairGapRise;
                }
            }

            end = 0f;
            return false;
        }

        // A staircase sampled on a 25 cm grid lands each cell on a different tread, so every cell became its own plate.
        // Where the cells within 0.5 m form three or more steps on a steady incline, the snow surface follows one plane
        // laid through the step edges instead.
        private bool TryFitStairs(int x, int z, float own, out float ground, out Vector3 normal)
        {
            ground = own;
            normal = Vector3.up;
            double n = 0, sx = 0, sz = 0, sh = 0, sxx = 0, szz = 0, sxz = 0, sxh = 0, szh = 0;
            int heights = 0;
            for (int dz = -StairReach; dz <= StairReach; dz++)
            {
                for (int dx = -StairReach; dx <= StairReach; dx++)
                {
                    if (!TryGetBaseGround(x + dx, z + dz, out float neighborGround))
                    {
                        continue;
                    }

                    float h = neighborGround - own;
                    if (Mathf.Abs(h) > StairSpan)
                    {
                        continue;
                    }

                    n++;
                    sx += dx;
                    sz += dz;
                    sh += h;
                    sxx += dx * dx;
                    szz += dz * dz;
                    sxz += dx * dz;
                    sxh += dx * h;
                    szh += dz * h;
                    _stairHeights[heights++] = h;
                }
            }

            if (n < 15 || !HasEvenTreads(heights))
            {
                return false;
            }

            // Least squares for h = a + b * dx + c * dz.
            double m00 = n, m01 = sx, m02 = sz, m11 = sxx, m12 = sxz, m22 = szz;
            double det = m00 * (m11 * m22 - m12 * m12) - m01 * (m01 * m22 - m12 * m02) + m02 * (m01 * m12 - m11 * m02);
            if (System.Math.Abs(det) < 1e-6)
            {
                return false;
            }

            double a = (sh * (m11 * m22 - m12 * m12) - m01 * (sxh * m22 - m12 * szh) + m02 * (sxh * m12 - m11 * szh)) / det;
            double b = (m00 * (sxh * m22 - szh * m12) - sh * (m01 * m22 - m12 * m02) + m02 * (m01 * szh - sxh * m02)) / det;
            double c = (m00 * (m11 * szh - m12 * sxh) - m01 * (m01 * szh - sxh * m02) + sh * (m01 * m12 - m11 * m02)) / det;
            float slope = (float)System.Math.Sqrt(b * b + c * c) / CellWidth;
            if (slope < 0.25f || slope > 1.0f || System.Math.Abs(a) > 0.20)
            {
                return false;
            }

            double squares = 0;
            float highest = 0f;
            for (int dz = -StairReach; dz <= StairReach; dz++)
            {
                for (int dx = -StairReach; dx <= StairReach; dx++)
                {
                    if (!TryGetBaseGround(x + dx, z + dz, out float neighborGround))
                    {
                        continue;
                    }

                    float h = neighborGround - own;
                    if (Mathf.Abs(h) > StairSpan)
                    {
                        continue;
                    }

                    float residual = (float)(h - (a + b * dx + c * dz));
                    squares += residual * residual;
                    highest = Mathf.Max(highest, residual);
                }
            }

            // A smooth ramp has almost no residual and already renders well. Stairs leave a sawtooth.
            double rms = System.Math.Sqrt(squares / n);
            if (rms < 0.025 || rms > 0.12)
            {
                return false;
            }

            ground = own + (float)a + Mathf.Min(highest, 0.15f);
            normal = new Vector3((float)-b / CellWidth, 1f, (float)-c / CellWidth).normalized;
            return true;
        }

        private readonly float[] _stairHeights = new float[(2 * StairReach + 1) * (2 * StairReach + 1)];

        // Stairs have flat treads: heights cluster into three or more levels of at least two cells, each level within 4
        // cm, with risers of 8 to 26 cm between them. Rubble, rocks and planter edges do not.
        private bool HasEvenTreads(int count)
        {
            System.Array.Sort(_stairHeights, 0, count);
            int levels = 0;
            float previousMean = 0f;
            int start = 0;
            for (int i = 1; i <= count; i++)
            {
                if (i < count && _stairHeights[i] - _stairHeights[i - 1] <= 0.06f)
                {
                    continue;
                }

                int size = i - start;
                if (size >= 2)
                {
                    if (_stairHeights[i - 1] - _stairHeights[start] > 0.04f)
                    {
                        return false;
                    }

                    float sum = 0f;
                    for (int j = start; j < i; j++)
                    {
                        sum += _stairHeights[j];
                    }

                    float mean = sum / size;
                    if (levels > 0 && (mean - previousMean < 0.08f || mean - previousMean > 0.26f))
                    {
                        return false;
                    }

                    previousMean = mean;
                    levels++;
                }

                start = i;
            }

            return levels >= 3;
        }

        private bool TryBridgeAlongAxis(int x, int z, int dx, int dz, Column column, out Column bridge)
        {
            for (int reach = 1; reach <= GapRepairCells; reach++)
            {
                Column a = GetColumn(x - dx * reach, z - dz * reach);
                Column b = GetColumn(x + dx * reach, z + dz * reach);
                if (!a.Sampled || !b.Sampled || a.Sheltered || b.Sheltered)
                {
                    break;
                }

                if (TryBridge(column, a, b, out bridge))
                {
                    return true;
                }
            }

            bridge = default;
            return false;
        }

        private static (int X, int Z, float Distance)[] BuildSurfaceOffsets(int cells, float radius, float centerOffset, float denseRadius = float.PositiveInfinity)
        {
            var offsets = new List<(int X, int Z, float Distance)>();
            for (int z = -cells; z <= cells; z++)
            {
                for (int x = -cells; x <= cells; x++)
                {
                    float dx = (x + centerOffset) * CellWidth;
                    float dz = (z + centerOffset) * CellWidth;
                    float distance = (float)System.Math.Sqrt(dx * dx + dz * dz);
                    bool onWideGrid = x % 2 == 0 && z % 2 == 0;
                    if (distance < radius && (distance < denseRadius || onWideGrid))
                    {
                        offsets.Add((x, z, distance));
                    }
                }
            }

            offsets.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return offsets.ToArray();
        }

        private static float DoorDriftFalloff(float distance, float reach, float strength)
        {
            return strength * (1f - Mathf.SmoothStep(0f, 1f, distance / reach));
        }

        private Vector3 GroundPoint(int x, int z, float height)
        {
            return _origin + new Vector3((x + 0.5f) * CellWidth, height, (z + 0.5f) * CellWidth);
        }

        private static bool TryBridge(Column sample, Column a, Column b, out Column bridge)
        {
            bridge = default;
            float height = (a.GroundHeight + b.GroundHeight) * 0.5f;
            float maxRise = a.Normal.y >= 0.85f && b.Normal.y >= 0.85f ? 0.12f : 0.24f;
            if (!a.Exposed || !b.Exposed || Mathf.Abs(a.GroundHeight - b.GroundHeight) > maxRise)
            {
                return false;
            }

            if (sample.HasGround && Mathf.Abs(sample.GroundHeight - height) > 0.24f)
            {
                return false;
            }

            bridge = new Column
            {
                Sampled = true,
                HasGround = true,
                Exposed = true,
                GroundHeight = height,
                Normal = (a.Normal + b.Normal).normalized
            };
            return true;
        }

        private bool SampleChunk(Chunk chunk, bool preparing)
        {
            // Sample beyond the chunk so the surface and feathering match across its borders.
            int budget = preparing ? CountdownSampleBudget : SampleBudget;
            int remaining = budget;
            double seconds = preparing ? CountdownSampleSeconds : SampleSeconds;
            long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * seconds);
            for (; chunk.NextSample < _samplesPerChunk && remaining > 0; chunk.NextSample++)
            {
                int x = chunk.X + chunk.NextSample % _samplingWidth - _samplePadding;
                int z = chunk.Z + chunk.NextSample / _samplingWidth - _samplePadding;
                Chunk owner = GetOrCreateChunk(KeyForCell(x, z));
                if (owner.Columns == null)
                {
                    owner.Columns = new Column[ChunkWidth * ChunkWidth];
                }

                ref Column column = ref owner.Columns[owner.ColumnIndex(x, z)];
                if (column.Sampled)
                {
                    continue;
                }

                if (remaining < budget && Stopwatch.GetTimestamp() >= deadline)
                {
                    break;
                }

                remaining--;
                column = SampleColumn(GroundPoint(x, z, 0f));
                ApplyStairRamp(x, z, ref column);
            }

            return chunk.NextSample == _samplesPerChunk;
        }

        private Column SampleColumnCore(Vector3 position)
        {
            // position.y is ignored.
            _hasNarrowArchitecture = false;
            _hasRejectedTop = false;
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            bool hasTerrain = Physics.Raycast(new Vector3(position.x, _origin.y + SweepTop, position.z), Vector3.down, out var terrain, SweepLength, _terrainMask, triggers);
            bool onLake = FrozenIce.RaiseTerrain(position, ref terrain, ref hasTerrain);

            const float rampReach = 4f;
            float reference;
            if (hasTerrain)
            {
                reference = terrain.point.y;
            }
            else
            {
                reference = GetGroundReference(position);
                if (float.IsPositiveInfinity(reference))
                {
                    if (!TryFindColumnTop(position, out var own))
                    {
                        return new Column
                        {
                            Sampled = true
                        };
                    }

                    reference = own.point.y;
                }
            }

            float below = hasTerrain ? 0.10f : 7f;
            var origin = new Vector3(position.x, reference + rampReach, position.z);
            float reach = rampReach + below;
            Collider obstruction = null;

            // One ownership rule on every map: the highest surface in the window that the ground layer owns. Surfaces
            // it does not own stay with the raised layer, and the search continues beneath them.
            bool hasGround = TrySelectGround(origin, reach, reference, ref obstruction, out var ground, _solidMask);

            // The first solid hit and the first rejected surface of the detailed search, for the bunker trench test.
            Collider firstSolid = _firstSolidHit;
            Collider firstRejected = _firstRejectedHit;

            if (hasTerrain && (!hasGround || ground.normal.y < 0.55f || ground.point.y < terrain.point.y
                               || (ground.collider != terrain.collider && (IsDustOnly(ground.collider) || IsRock(ground.collider)))))
            {
                ground = terrain;
                hasGround = true;
            }
            else if (onLake && ground.collider != terrain.collider && ground.point.y - terrain.point.y <= FrozenIce.FrozenInRise)
            {
                // A log, boat rim or board lying in the lake ice is part of the flat ice (FrozenLake.cs).
                ground = terrain;
            }
            else if (!hasTerrain && hasGround)
            {
                // EFT's detailed (ballistic) colliders have holes: thin or missing stair treads, gaps in raised decks.
                // When that layer holds an owned floor clearly above the detailed one, snow lies on it. The sweep can
                // only return a floor that high if the layer has a surface that high, so one short ray decides whether
                // to run it. Most columns have nothing there and skip the ownership checks.
                bool hadNarrow = _hasNarrowArchitecture;
                RaycastHit narrowHit = _narrowArchitecture;
                Collider walkObstruction = null;
                var walkOrigin = new Vector3(position.x, reference + rampReach, position.z);
                float walkWindow = walkOrigin.y - (ground.point.y + LowPolyFloorLift);
                if (walkWindow > 0f && Physics.Raycast(walkOrigin, Vector3.down, walkWindow, _fallbackGroundMask, triggers)
                                    && TrySelectGroundUnnoted(walkOrigin, rampReach + below, reference, ref walkObstruction, out var walk, _fallbackGroundMask)
                                    && walk.point.y > ground.point.y + LowPolyFloorLift && walk.normal.y >= WalkSurfaceMinNormal)
                {
                    ground = walk;
                }

                _hasNarrowArchitecture = hadNarrow;
                _narrowArchitecture = narrowHit;
            }
            else if (!hasTerrain && !hasGround)
            {
                origin.y = reference + rampReach;
                reach = rampReach + below;
                hasGround = TrySelectGround(origin, reach, reference, ref obstruction, out ground, _fallbackGroundMask);
            }

            // The shared reference can sit too low for this column, for example on a street more than 2 m above a
            // nearby sunken yard. Use the column's own top surface rather than caching a gap.
            if (!hasTerrain && !hasGround && TryFindColumnTop(position, out var columnTop) && SupportRejection(columnTop, true, false) == null)
            {
                ground = columnTop;
                hasGround = true;
            }

            if (!hasTerrain && !hasGround && _hasNarrowArchitecture)
            {
                ground = _narrowArchitecture;
                hasGround = true;
            }

            if (!hasGround)
            {
                return new Column
                {
                    Sampled = true
                };
            }

            // The ground layer can continue under low props, where solid geometry hides it.

            bool sheltered = IsSheltered(ground.point + Vector3.up * 0.40f);
            byte shelterKind = _lastShelterKind;
            float coverRise = _lastCoverRise;
            bool hasUpper = false;
            RaycastHit upper = default;
            if (!hasTerrain && sheltered && TryFindColumnTop(position, out upper) && upper.point.y > ground.point.y + UpperLevelRise
                && upper.point.y <= ground.point.y + MaxUpperLevelRise && SupportRejection(upper, true, false) == null)
            {
                // Keep the sheltered floor for doorway drift, and remember the exposed level above it.
                hasUpper = !IsSheltered(upper.point + Vector3.up * 0.40f);
            }

            // Remember the open roof here without showing it: the ground beside it and the raised cap on it then join
            // where they meet (RoofFlush). The bunker keeps its doorway drift underneath.
            bool hasRoof = false;
            float roofHeight = 0f;
            if (hasTerrain && sheltered && TryFindColumnTop(position, out var roof) && roof.point.y > ground.point.y + UpperLevelRise
                && roof.point.y <= ground.point.y + MaxUpperLevelRise && roof.normal.y >= RoofJoinNormal && !IsShelteredUnnoted(roof.point + Vector3.up * 0.40f))
            {
                hasRoof = true;
                roofHeight = roof.point.y - _origin.y;
            }

            bool hasTop = _hasRejectedTop && _rejectedTop.point.y > ground.point.y + 0.10f;
            bool bunkerPart = IsBunkerPart(ground.collider) || IsBunkerPart(firstSolid) || IsBunkerPart(firstRejected);
            bool bunkerTrench = bunkerPart && !sheltered && (IsBunkerShell(ground.collider) || IsBunkerShell(firstSolid) || IsBunkerShell(firstRejected));
            return new Column
            {
                HasTop = hasTop,
                TopHeight = hasTop ? _rejectedTop.point.y - _origin.y : 0f,
                BunkerPart = bunkerPart,
                BunkerTrench = bunkerTrench,
                HasRoof = hasRoof,
                RoofHeight = roofHeight,
                HasUpperLevel = hasUpper,
                UpperHeight = hasUpper ? upper.point.y - _origin.y : 0f,
                UpperNormal = hasUpper ? upper.normal : Vector3.zero,
                Sampled = true,
                HasGround = true,
                Exposed = !sheltered && ground.normal.y >= (ground.collider == terrain.collider ? 0.22f : 0.35f),
                Sheltered = sheltered,
                Pavement = ground.collider != terrain.collider && IsGroundPavement(ground.collider),
                OnTerrain = ground.collider == terrain.collider,
                MoundStrength = !sheltered && obstruction != ground.collider ? GroundMoundStrength(obstruction, ground.point.y) : 0f,
                GroundHeight = ground.point.y - _origin.y,
                Normal = ground.normal,
                ShelterKind = shelterKind,
                CoverRise = coverRise
            };
        }

        private bool UpperLevelShared(int x, int z, float height)
        {
            int shared = 0;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    Column neighbour = GetColumn(x + dx, z + dz);
                    bool level = neighbour.HasUpperLevel
                        ? Mathf.Abs(neighbour.UpperHeight - height) < 0.15f
                        : neighbour.Exposed && Mathf.Abs(neighbour.GroundHeight - height) < 0.15f;
                    if (level)
                    {
                        shared++;
                    }
                }
            }

            return shared >= 3;
        }

        private static Column UpperLevel(Column floor)
        {
            return new Column
            {
                Sampled = true,
                HasGround = true,
                Exposed = true,
                ShowsUpperLevel = true,
                GroundHeight = floor.UpperHeight,
                Normal = floor.UpperNormal
            };
        }

        // Lowest top surface near a column. On open ground it is the street or yard beside the column, so the existing
        // search window picks the same floor a street-level player would have seen.
        private float GetGroundReference(Vector3 position)
        {
            const float spacing = ReferenceCells * CellWidth;
            var key = new Vector2Int(Mathf.FloorToInt((position.x - _origin.x) / spacing), Mathf.FloorToInt((position.z - _origin.z) / spacing));
            if (_groundReferences.TryGetValue(key, out float reference))
            {
                return reference;
            }

            int count = 0;
            for (int z = -ReferenceReach; z <= ReferenceReach; z++)
            {
                for (int x = -ReferenceReach; x <= ReferenceReach; x++)
                {
                    float top = GetSurfaceTop(new Vector2Int(key.x + x, key.y + z));
                    if (!float.IsPositiveInfinity(top))
                    {
                        _referenceTops[count++] = top;
                    }
                }
            }

            reference = count == 0 ? float.PositiveInfinity : LowestSharedTop(count);
            _groundReferences[key] = reference;
            return reference;
        }

        // The lowest height shared by several nearby tops. A window well, drain or ramp mouth covers only a point or
        // two and must not pull the search below the street that the doorways open onto.
        private float LowestSharedTop(int count)
        {
            System.Array.Sort(_referenceTops, 0, count);
            for (int i = 0; i < count; i++)
            {
                int shared = 1;
                for (int j = i + 1; j < count && _referenceTops[j] - _referenceTops[i] <= ReferenceTolerance; j++)
                {
                    shared++;
                }

                if (shared >= ReferenceSupport)
                {
                    return _referenceTops[i];
                }
            }

            return _referenceTops[0];
        }

        private float GetSurfaceTop(Vector2Int key)
        {
            if (_surfaceTops.TryGetValue(key, out float top))
            {
                return top;
            }

            const float spacing = ReferenceCells * CellWidth;
            var point = new Vector3(_origin.x + (key.x + 0.5f) * spacing, 0f, _origin.z + (key.y + 0.5f) * spacing);
            // Any solid top counts here, walkable or not. A steep roof only raises a value the minimum ignores.
            top = TryFindTopSurface(point, _solidMask | _terrainMask, false, out var hit) || TryFindTopSurface(point, _fallbackGroundMask, false, out hit)
                ? hit.point.y
                : float.PositiveInfinity;
            _surfaceTops[key] = top;
            return top;
        }

        private bool TryFindColumnTop(Vector3 position, out RaycastHit top)
        {
            return TryFindTopSurface(position, _solidMask, true, out top) || TryFindTopSurface(position, _fallbackGroundMask, true, out top);
        }

        private bool TryFindTopSurface(Vector3 position, int mask, bool walkable, out RaycastHit top)
        {
            // Any other surface ends the sweep, so a steep curb, embankment or roof can never expose a garage or tunnel
            // below it.
            var origin = new Vector3(position.x, _origin.y + SweepTop, position.z);
            float remaining = SweepLength;
            for (int i = 0; i < SweepHits && remaining > 0f; i++)
            {
                if (!Physics.Raycast(origin, Vector3.down, out var hit, remaining, mask, QueryTriggerInteraction.Ignore))
                {
                    break;
                }

                if ((hit.rigidbody == null || hit.rigidbody.isKinematic) && !IsGroundObstruction(hit.collider))
                {
                    top = hit;
                    // Rocks keep the game's own winter finish. They never become a column's floor or roof level.
                    return !walkable || (hit.normal.y >= 0.55f && !IsRock(hit.collider) && !IsDustOnly(hit.collider));
                }

                float step = hit.distance + SweepStep;
                origin.y -= step;
                remaining -= step;
            }

            top = default;
            return false;
        }

        private float GroundMoundStrength(Collider obstacle, float floorHeight)
        {
            if (obstacle == null || IsGroundPavement(obstacle) || IsDustOnly(obstacle) || (obstacle.attachedRigidbody != null && !obstacle.attachedRigidbody.isKinematic))
            {
                return 0f;
            }

            var kind = GetOutdoorPropKind(obstacle);
            if (kind == OutdoorPropKind.UtilityEquipment || kind == OutdoorPropKind.UtilityRoof)
            {
                return 0f;
            }

            Bounds bounds = obstacle.bounds;
            return GroundMoundStrength(bounds.size, bounds.min.y - floorHeight, bounds.max.y - floorHeight, kind == OutdoorPropKind.Clutter);
        }

        private static float GroundMoundStrength(Vector3 size, float baseRise, float topRise, bool clutter)
        {
            if (size.x > 4.5f || size.y > 2f || size.z > 4.5f || baseRise > 0.24f || topRise < 0.14f || topRise > (clutter ? 2.0f : 1.05f))
            {
                return 0f;
            }

            return Mathf.SmoothStep(0.42f, 1f, Mathf.InverseLerp(0.14f, 0.70f, topRise));
        }

        private bool TrySelectGroundUnnoted(Vector3 origin, float reach, float reference, ref Collider obstruction, out RaycastHit ground, int mask)
        {
            bool found = TrySelectGround(origin, reach, reference, ref obstruction, out ground, mask);
            return found;
        }

        private bool TrySelectGround(Vector3 origin, float reach, float reference, ref Collider obstruction, out RaycastHit ground, int mask)
        {
            RaycastHit clutterTop = default;
            bool hasClutterTop = false;
            _firstSolidHit = null;
            _firstRejectedHit = null;
            for (int i = 0; i < SweepHits && reach > 0f; i++)
            {
                if (!Physics.Raycast(origin, Vector3.down, out var hit, reach, mask, QueryTriggerInteraction.Ignore))
                {
                    break;
                }

                if (i == 0)
                {
                    _firstSolidHit = hit.collider;
                }

                string rejection = GroundRejection(hit, reference, out string ownership);
                if (rejection == null)
                {
                    // Snow rests on a tree grate or drain cover rather than falling through it to a void below.
                    if (hasClutterTop && hit.point.y < reference - DeepFall && clutterTop.point.y - hit.point.y > DeepFall)
                    {
                        hit = clutterTop;
                        ownership = "rests on clutter over a void";
                    }

                    ground = hit;

                    return true;
                }

                if (!hasClutterTop && IsGroundObstruction(hit.collider) && hit.normal.y >= 0.55f && hit.point.y <= reference + GroundRise)
                {
                    clutterTop = hit;
                    hasClutterTop = true;
                }

                if (_firstRejectedHit == null && !IsGroundObstruction(hit.collider))
                {
                    _firstRejectedHit = hit.collider;
                }

                // The first skipped object can still bank a low mound into the ground beside it.
                obstruction ??= hit.collider;
                if (!_hasRejectedTop && !IsGroundObstruction(hit.collider) && hit.normal.y >= 0.85f)
                {
                    _rejectedTop = hit;
                    _hasRejectedTop = true;
                }

                // Keep the first narrow part of large, visible architecture, such as a stair tread or a curb strip. It
                // becomes the floor only when nothing else is found, instead of leaving a hole in the snow.
                if (!_hasNarrowArchitecture && ((rejection.StartsWith("narrow", System.StringComparison.Ordinal) && IsLargeSupport(hit.collider)) || rejection == "small object")
                                            && HasVisibleSupport(hit))
                {
                    _narrowArchitecture = hit;
                    _hasNarrowArchitecture = true;
                }

                // Re-cast just below the hit, so a lower level of the same collider is still reachable.
                float step = hit.distance + SweepStep;
                origin.y -= step;
                reach -= step;
            }

            ground = default;
            return false;
        }

        // Null when the ground layer owns the hit. Otherwise the reason it stays with the raised layer.
        private string GroundRejection(RaycastHit hit, float reference, out string ownership)
        {
            ownership = null;
            if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
            {
                return "moving body";
            }

            if (IsGroundObstruction(hit.collider))
            {
                return "outdoor obstruction";
            }

            float rise = hit.point.y - reference;
            if (hit.normal.y < 0.35f)
            {
                // The ground continues beneath a leaning sign, crate side or shield. A wall or embankment face still
                // ends the search, so the sweep cannot pass through architecture to a lower level.
                if (!IsLargeSupport(hit.collider))
                {
                    return "steep side of a small object";
                }

                ownership = "steep face";
                return null;
            }

            if (rise <= GroundRise)
            {
                ownership = "at ground height";
                return null;
            }

            if (IsSoftSurface(hit.collider))
            {
                if (IsLyingOnFloor(hit, out var floor) && GroundRejection(floor, reference, out _) == null)
                {
                    ownership = "mat on a floor";
                    return null;
                }

                return "water or fabric surface";
            }

            bool flat = hit.normal.y >= FlatNormal;
            if (rise > (flat ? FlatGroundReach : RampGroundReach)
                && !(flat && rise <= RampGroundReach && IsLargeSupport(hit.collider) && !IsWindowPart(hit.collider) && IsStairTread(hit)))
            {
                return "above the ground window";
            }

            if (IsRock(hit.collider) || IsDustOnly(hit.collider))
            {
                return "rock or dust-only support";
            }

            string rejection = SupportRejection(hit, rise > 0.55f, rise <= ShallowSupportRise);
            if (rejection == null)
            {
                ownership = IsGroundPavement(hit.collider) && hit.normal.y >= 0.85f ? "mapped pavement" : flat ? "broad support" : "walkable ramp";
            }

            return rejection;
        }

        private string SupportRejection(RaycastHit hit, bool raised, bool shallow)
        {
            // Mapped pavement and broad ramps keep their earlier, verified acceptance.
            if (IsGroundPavement(hit.collider) && hit.normal.y >= 0.85f)
            {
                return null;
            }

            if (hit.normal.y < FlatNormal)
            {
                // Planks of a lake deck tilt a little where they rest on logs; they stay part of the deck.
                if (!IsWalkableRamp(hit, raised) && !(hit.normal.y >= 0.90f && IsLakeDeckSupport(hit.collider)))
                {
                    return "narrow or uneven incline";
                }
            }
            else if (!IsBroadFlat(hit))
            {
                // A narrow flat surface between a higher and a lower step is a stair tread, not a ledge or rail.
                return IsStairTread(hit) && HasVisibleSupport(hit) ? null : "narrow support";
            }

            // Low broad slabs and ramps are buried by the snow either way. Higher ones, flat or inclined, must be
            // architecture the player sees, not a rubble pile, fence curtain or debris slab passing a plane test.
            if (shallow)
            {
                return null;
            }

            if (!IsLargeSupport(hit.collider) && !(hit.normal.y >= 0.90f && IsLakeDeckSupport(hit.collider)))
            {
                return "small object";
            }

            return HasVisibleSupport(hit) ? null : "no render geometry at the support";
        }

        // A window frame or sill: its bars can pass the riser test, but a window is never a staircase.
        private static bool IsWindowPart(Collider collider)
        {
            Transform parent = collider.transform.parent;
            return ContainsIgnoreCase(collider.name, "window") || (parent != null && ContainsIgnoreCase(parent.name, "window"));
        }

        private bool IsBroadFlat(RaycastHit hit)
        {
            return (FollowsSurface(hit, Vector3.right * BroadSpread) || FollowsSurface(hit, Vector3.left * BroadSpread))
                   && (FollowsSurface(hit, Vector3.forward * BroadSpread) || FollowsSurface(hit, Vector3.back * BroadSpread));
        }

        private RaycastHit _narrowArchitecture;
        private bool _hasNarrowArchitecture;

        // The first surface the ground search passed over (not clutter): the visible top of the column when the ground
        // itself lies below it, as under the Woods bunker stairs (VoxelStairFlights.cs).
        private RaycastHit _rejectedTop;
        private bool _hasRejectedTop;
        private readonly Dictionary<Collider, bool> _softSurfaces = new Dictionary<Collider, bool>();

        // Ballistic material names mark water and fabric. Neither holds a full snow layer above the ground. A thin flat
        // surface with a solid floor within 6 cm under it.
        private bool IsLyingOnFloor(RaycastHit hit, out RaycastHit floor)
        {
            floor = default;
            if (hit.normal.y < 0.95f)
            {
                return false;
            }

            Vector3 below = hit.point - Vector3.up * 0.005f;
            return Physics.Raycast(below, Vector3.down, out floor, 0.06f, _solidMask, QueryTriggerInteraction.Ignore) && floor.collider != hit.collider
                                                                                                                      && !IsSoftSurface(floor.collider) && floor.normal.y >= 0.95f;
        }

        private bool IsSoftSurface(Collider collider)
        {
            if (!_softSurfaces.TryGetValue(collider, out bool soft))
            {
                string name = collider.name;
                soft = name.IndexOf("_BALLISTIC_Water", System.StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("fabric", System.StringComparison.OrdinalIgnoreCase) >= 0;
                _softSurfaces.Add(collider, soft);
            }

            return soft;
        }

        // A platform that reaches out over the frozen lake is a deck along its whole length. Decided per collider from
        // its centre and four points around it, so the answer does not depend on which cell is sampled first.
        private readonly Dictionary<Collider, bool> _lakeDeckSupports = new Dictionary<Collider, bool>();

        private bool IsLakeDeckSupport(Collider collider)
        {
            if (_lakeDeckSupports.TryGetValue(collider, out bool deck))
            {
                return deck;
            }

            Bounds bounds = collider.bounds;
            Vector3 c = bounds.center;
            Vector3 e = bounds.extents * 0.5f;
            deck = FrozenIce.IsLakeDeck(c.x, c.z, c.y) || FrozenIce.IsLakeDeck(c.x + e.x, c.z + e.z, c.y) || FrozenIce.IsLakeDeck(c.x - e.x, c.z + e.z, c.y)
                   || FrozenIce.IsLakeDeck(c.x + e.x, c.z - e.z, c.y) || FrozenIce.IsLakeDeck(c.x - e.x, c.z - e.z, c.y);
            // Only a yes is kept: a platform sampled before the lake has frozen asks again later.
            if (deck)
            {
                _lakeDeckSupports.Add(collider, true);
            }

            return deck;
        }

        private bool IsLargeSupport(Collider collider)
        {
            if (_largeSupports.TryGetValue(collider, out bool large))
            {
                return large;
            }

            // Measure in the collider's own axes, so a parked car at 45 degrees still reads as 1.8 m wide.
            Vector3 size = collider switch
            {
                BoxCollider box => Vector3.Scale(box.size, collider.transform.lossyScale),
                MeshCollider mesh when mesh.sharedMesh != null => Vector3.Scale(mesh.sharedMesh.bounds.size, collider.transform.lossyScale),
                _ => collider.bounds.size
            };
            float a = Mathf.Abs(size.x);
            float b = Mathf.Abs(size.y);
            float c = Mathf.Abs(size.z);
            float middle = Mathf.Max(Mathf.Min(a, b), Mathf.Min(Mathf.Max(a, b), c));
            large = middle >= LargeSupportWidth;
            _largeSupports.Add(collider, large);
            return large;
        }

        private bool HasVisibleSupport(RaycastHit hit)
        {
            if (!_supportRenderers.TryGetValue(hit.collider, out var renderers))
            {
                renderers = FindSupportRenderers(hit.collider);
                _supportRenderers.Add(hit.collider, renderers);
            }

            if (renderers == null)
            {
                // Map-scale architecture is not a proxy prop.
                return true;
            }

            const float margin = 0.10f;
            Vector3 point = hit.point;
            foreach (var bounds in renderers)
            {
                if (point.x >= bounds.min.x - margin && point.x <= bounds.max.x + margin && point.z >= bounds.min.z - margin && point.z <= bounds.max.z + margin
                    && bounds.max.y >= point.y - margin)
                {
                    return true;
                }
            }

            return false;
        }

        private Bounds[] FindSupportRenderers(Collider collider)
        {
            const int maxNodes = 256;
            Bounds support = collider.bounds;
            support.Expand(0.25f);
            Transform part = collider.transform;
            for (int level = 0; level < 4 && part != null; level++, part = part.parent)
            {
                _propNodes.Clear();
                _supportBounds.Clear();
                _propNodes.Add(part);
                for (int i = 0; i < _propNodes.Count; i++)
                {
                    Transform node = _propNodes[i];
                    if (node.TryGetComponent<MeshRenderer>(out var renderer) && renderer.enabled && renderer.gameObject.activeInHierarchy && renderer.bounds.Intersects(support))
                    {
                        _supportBounds.Add(renderer.bounds);
                    }

                    if (_propNodes.Count + node.childCount > maxNodes)
                    {
                        return _supportBounds.Count != 0 ? _supportBounds.ToArray() : null;
                    }

                    for (int child = 0; child < node.childCount; child++)
                    {
                        _propNodes.Add(node.GetChild(child));
                    }
                }

                if (_supportBounds.Count != 0)
                {
                    return _supportBounds.ToArray();
                }
            }

            return System.Array.Empty<Bounds>();
        }

        private bool IsWalkableRamp(RaycastHit hit, bool raisedAboveTerrain)
        {
            if (hit.normal.y < 0.55f || hit.normal.y >= 0.995f || (hit.rigidbody != null && !hit.rigidbody.isKinematic) || IsRock(hit.collider) || IsDustOnly(hit.collider)
                || IsGroundObstruction(hit.collider))
            {
                return false;
            }

            var downhill = new Vector3(hit.normal.x, 0f, hit.normal.z).normalized * 0.40f;
            var across = new Vector3(-downhill.z, 0f, downhill.x);
            if (raisedAboveTerrain && !FollowsSurface(hit, downhill * 3f))
            {
                return false;
            }

            // High up, a ramp must continue on both sides. A handrail or stair cheek wall only runs lengthwise.
            bool acrossOk = raisedAboveTerrain ? FollowsSurface(hit, across) && FollowsSurface(hit, -across) : FollowsSurface(hit, across) || FollowsSurface(hit, -across);
            return acrossOk && (FollowsSurface(hit, downhill) || FollowsSurface(hit, -downhill));
        }

        private static readonly Vector3[] StairAxes = { Vector3.right, Vector3.forward, new Vector3(1f, 0f, 1f).normalized, new Vector3(1f, 0f, -1f).normalized };

        private bool IsStairTread(RaycastHit hit)
        {
            if (hit.normal.y < FlatNormal || IsGroundObstruction(hit.collider) || IsRock(hit.collider))
            {
                return false;
            }

            int axisIndex = -1;

            foreach (var across in StairAxes)
            {
                axisIndex++;
                var along = new Vector3(-across.z, 0f, across.x);
                if (!FollowsSurface(hit, along * 0.40f) && !FollowsSurface(hit, -along * 0.40f))
                {
                    continue;
                }

                float ahead = StepHeight(hit.point + across * StairProbe);
                float behind = StepHeight(hit.point - across * StairProbe);
                if (IsTreadBetween(hit.point.y, ahead, behind))
                {
                    return true;
                }

                // Only when the other side already shows a riser, look again nearer and farther.
                bool partial = IsRiser(ahead - hit.point.y) || IsRiser(hit.point.y - ahead) || IsRiser(behind - hit.point.y) || IsRiser(hit.point.y - behind);
                if (!partial)
                {
                    continue;
                }

                foreach (float distance in StairRetryProbes)
                {
                    float nearAhead = StepHeight(hit.point + across * distance);
                    float nearBehind = StepHeight(hit.point - across * distance);
                    if (IsTreadBetween(hit.point.y, nearAhead, behind) || IsTreadBetween(hit.point.y, ahead, nearBehind) || IsTreadBetween(hit.point.y, nearAhead, nearBehind))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private float StepHeight(Vector3 point)
        {
            return Physics.Raycast(point + Vector3.up * 0.40f, Vector3.down, out var step, 0.80f, _solidMask | _fallbackGroundMask, QueryTriggerInteraction.Ignore)
                   && step.normal.y >= 0.94f && !IsGroundObstruction(step.collider)
                ? step.point.y
                : float.NaN;
        }

        private static readonly float[] StairRetryProbes = { 0.18f, 0.42f };

        private static bool IsTreadBetween(float tread, float ahead, float behind)
        {
            return (IsRiser(ahead - tread) && IsRiser(tread - behind)) || (IsRiser(behind - tread) && IsRiser(tread - ahead));
        }

        private static bool IsRiser(float rise)
        {
            return rise >= 0.08f && rise <= 0.25f;
        }

        private bool FollowsSurface(RaycastHit seed, Vector3 offset)
        {
            const float tolerance = 0.05f;
            float rise = -Vector3.Dot(seed.normal, offset) / seed.normal.y;
            Vector3 start = seed.point + offset + Vector3.up * (rise + tolerance);
            if (!Physics.Raycast(start, Vector3.down, out var hit, tolerance * 2f, _solidMask | _fallbackGroundMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            return !IsGroundObstruction(hit.collider) && Vector3.Dot(seed.normal, hit.normal) >= 0.94f;
        }

        internal const byte ShelterOpen = 0, ShelterIndoorVolume = 1, ShelterInterchangeVolume = 2, ShelterRoof = 3;

        // The last IsSheltered call's decision, kept on the ground column for the automatic checks.
        private byte _lastShelterKind;
        private float _lastCoverRise;

        private bool IsShelteredUnnoted(Vector3 origin)
        {
            bool sheltered = IsSheltered(origin);
            return sheltered;
        }

        private bool IsSheltered(Vector3 origin)
        {
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            _lastShelterKind = ShelterOpen;
            _lastCoverRise = -1f;
            if (SuppressIndoorSnow && SnowSurface.IsInIndoorVolume(origin))
            {
                return ShelterResult("Interchange indoor volume", true);
            }

            // Invisible blockers (vehicle and AI boundaries, trigger shells) left open road bare. Skip overhead hits
            // with no rendered geometry at the hit point and keep looking above them.
            RaycastHit overhead = default;
            bool covered = false;
            bool skipped = false;
            Vector3 from = origin;
            float reach = 60f;
            for (int i = 0; i < MaxOverheadHits && reach > 0f; i++)
            {
                if (!Physics.Raycast(from, Vector3.up, out var hit, reach, _doorMask, triggers))
                {
                    break;
                }

                if (HasVisibleSupport(hit))
                {
                    overhead = hit;
                    covered = true;
                    break;
                }

                skipped = true;
                float step = hit.distance + 0.05f;
                from += Vector3.up * step;
                reach -= step;
            }

            if (!covered)
            {
                return ShelterResult(skipped ? "only invisible cover above" : "clear upward ray", false);
            }

            float rise = overhead.point.y - origin.y;
            _lastCoverRise = rise;

            // EFT's indoor volumes can reach past a doorway or tunnel mouth. Trust one only under nearby cover; under a
            // high bridge or canopy the roof checks below decide.
            if (!SuppressIndoorSnow && rise <= IndoorVolumeCeiling && SnowSurface.IsInIndoorVolume(origin))
            {
                return ShelterResult("EFT indoor volume", true);
            }

            if (IsOutdoorClutter(overhead.collider) || (!SuppressIndoorSnow && rise < 1.30f))
            {
                return ShelterResult("outdoor clutter or low cover", false);
            }

            // A single hit on a lamp head or beam should not create a bare strip beneath it.
            if (!HasOverheadWidth(origin, rise))
            {
                return ShelterResult("overhead hit lacks roof width", false);
            }

            if (SuppressIndoorSnow && rise >= 1.25f)
            {
                return ShelterResult("Interchange roof fallback", true);
            }

            // One open side can feed a drift. Opposite open sides indicate narrow overhead cover.
            bool sheltered = !HasOpenSides(origin, ShelterDirections[0], ShelterDirections[1]) && !HasOpenSides(origin, ShelterDirections[2], ShelterDirections[3]);
            return ShelterResult(sheltered ? "broad roof blocks both axes" : "opposite sides open", sheltered);
        }

        private bool ShelterResult(string reason, bool sheltered)
        {
            _lastShelterKind = !sheltered ? ShelterOpen :
                reason == "EFT indoor volume" ? ShelterIndoorVolume :
                reason == "Interchange indoor volume" ? ShelterInterchangeVolume : ShelterRoof;

            return sheltered;
        }

        private bool HasOverheadWidth(Vector3 origin, float height)
        {
            const float widthProbe = 0.65f;
            return (HasNearbyOverhead(origin + Vector3.right * widthProbe, height) || HasNearbyOverhead(origin - Vector3.right * widthProbe, height))
                   && (HasNearbyOverhead(origin + Vector3.forward * widthProbe, height) || HasNearbyOverhead(origin - Vector3.forward * widthProbe, height));
        }

        private bool HasNearbyOverhead(Vector3 origin, float height)
        {
            const float tolerance = 0.55f;
            return Physics.Raycast(origin, Vector3.up, out var hit, height + tolerance, _doorMask, QueryTriggerInteraction.Ignore) && Mathf.Abs(hit.distance - height) <= tolerance
                && !IsOutdoorClutter(hit.collider);
        }

        private bool HasOpenSides(Vector3 origin, Vector3 first, Vector3 second)
        {
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            return !Physics.Raycast(origin, first, 24f, _doorMask, triggers) && !Physics.Raycast(origin, second, 24f, _doorMask, triggers);
        }

        private static readonly Vector3[] ShelterDirections =
        {
            new Vector3(1.2f, 1f, 0f).normalized, new Vector3(-1.2f, 1f, 0f).normalized, new Vector3(0f, 1f, 1.2f).normalized, new Vector3(0f, 1f, -1.2f).normalized
        };

        private void RebuildChunkCore(Chunk chunk, float depth)
        {
            float previousDepth = chunk.Depth;
            chunk.Depth = depth;
            chunk.RaisedDirty = false;
            chunk.RaisedRendered = false;
            _vertices.Clear();
            _normals.Clear();
            _triangles.Clear();
            _printLimits.Clear();

            if (depth > 0f)
            {
                long phase = Stopwatch.GetTimestamp();
                bool hasFootprints = PrepareFootprints(chunk);
                _rebuildPhases.Footprints = Stopwatch.GetTimestamp() - phase;

                phase = Stopwatch.GetTimestamp();
                BuildSurface(chunk, depth, hasFootprints);
                _rebuildPhases.Surface = Stopwatch.GetTimestamp() - phase - _rebuildPhases.Corners;
                _rebuildPhases.GroundVertices = _vertices.Count;
                if (_prints != null)
                {
                    // Remaining ground vertices (split levels, transitions) may shade prints; raised caps may not.
                    PadPrintLimits(new Vector4(0f, depth * 0.95f, 0f, 0f));
                }

                if (chunk.RaisedReady)
                {
                    phase = Stopwatch.GetTimestamp();
                    AddRaisedSnow(chunk, depth);
                    _rebuildPhases.Raised = Stopwatch.GetTimestamp() - phase;
                    chunk.RaisedRendered = chunk.RaisedSurfaces != null;
                }
            }
            else
            {
                chunk.FootprintsDirty = false;
                chunk.FootprintRefresh = float.PositiveInfinity;
            }

            if (_triangles.Count == 0)
            {
                chunk.FootprintRefresh = float.PositiveInfinity;
                ReleaseChunkMesh(chunk);
                return;
            }

            if (chunk.Mesh == null)
            {
                chunk.Mesh = new Mesh
                {
                    name = "Winter Outbreak Voxel Snow Mesh"
                };
                chunk.Mesh.MarkDynamic();
                chunk.Object = new GameObject("Winter Outbreak Voxel Snow Chunk");
                chunk.Object.transform.position = _origin;
                chunk.Object.AddComponent<MeshFilter>().sharedMesh = chunk.Mesh;

                var renderer = chunk.Object.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
            else if (_prints == null && chunk.RaisedSurfaces == null && depth - previousDepth >= 0.002f)
            {
                // Ease the added thickness without lifting any separate rock or prop caps.
                chunk.GrowthStarted = Time.time;
                chunk.GrowthInset = chunk.GrowthOffset + (depth - previousDepth) * 0.9f;
                chunk.GrowthOffset = chunk.GrowthInset;
                chunk.Object.transform.position = _origin - Vector3.up * chunk.GrowthOffset;
            }

            long upload = Stopwatch.GetTimestamp();
            chunk.Mesh.Clear();
            IndexFormat format = _vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            if (chunk.Mesh.indexFormat != format)
            {
                chunk.Mesh.indexFormat = format;
            }

            chunk.Mesh.SetVertices(_vertices);
            chunk.Mesh.SetTriangles(_triangles, 0);
            chunk.Mesh.SetNormals(_normals);
            FillExposure(chunk);
            chunk.Mesh.SetColors(_exposure);
            if (_prints != null)
            {
                // Always written in GPU mode: a mesh without this channel must not depend on the driver's default.
                PadPrintLimits();
                chunk.Mesh.SetUVs(3, _printLimits);

                // The shader presses refined cells down by up to 16 cm and grows the snow up to 10 cm past its built
                // depth; keep both inside the culling bounds.
                Bounds bounds = chunk.Mesh.bounds;
                bounds.Encapsulate(bounds.min - Vector3.up * 0.25f);
                bounds.Encapsulate(bounds.max + Vector3.up * 0.12f);
                chunk.Mesh.bounds = bounds;

                _growthBlock ??= new MaterialPropertyBlock();
                _growthBlock.SetFloat(BuiltDepthId, depth);
                chunk.Object.GetComponent<MeshRenderer>()?.SetPropertyBlock(_growthBlock);
            }

            _rebuildPhases.Upload = Stopwatch.GetTimestamp() - upload;
            _rebuildPhases.TotalVertices = _vertices.Count;
        }

        // Vertex colour alpha tells the shader how open the sky is above the snow: 1 outdoors, 0 on doorway drift and
        // other sheltered snow. Wind ripples and sparkle fade with it, so indoor drift reads as calm snow. Each mesh
        // corner averages its four cells, so the fade spans one cell at a doorway.
        private static readonly Color32 OpenSnow = new Color32(255, 255, 255, 255);
        private readonly List<Color32> _exposure = new List<Color32>();
        private readonly float[] _cellOpen = new float[(ChunkWidth + 2) * (ChunkWidth + 2)];
        private readonly float[] _cornerOpen = new float[(ChunkWidth + 1) * (ChunkWidth + 1)];

        private void FillExposure(Chunk chunk)
        {
            const int cellWidth = ChunkWidth + 2;
            const int meshWidth = ChunkWidth + 1;
            for (int z = 0; z < cellWidth; z++)
            {
                for (int x = 0; x < cellWidth; x++)
                {
                    Column column = GetSurfaceColumn(chunk.X + x - 1, chunk.Z + z - 1);
                    _cellOpen[z * cellWidth + x] = column.Sheltered && !column.ShowsUpperLevel ? 0f : 1f;
                }
            }

            for (int z = 0; z < meshWidth; z++)
            {
                for (int x = 0; x < meshWidth; x++)
                {
                    int cell = z * cellWidth + x;
                    _cornerOpen[z * meshWidth + x] = (_cellOpen[cell] + _cellOpen[cell + 1] + _cellOpen[cell + cellWidth] + _cellOpen[cell + cellWidth + 1]) * 0.25f;
                }
            }

            // Every vertex (refined cells, skirts, raised caps) reads the corner grid at its own position.
            _exposure.Clear();
            for (int i = 0; i < _vertices.Count; i++)
            {
                Vector3 point = _vertices[i];
                float u = Mathf.Clamp(point.x / CellWidth - chunk.X, 0f, ChunkWidth);
                float v = Mathf.Clamp(point.z / CellWidth - chunk.Z, 0f, ChunkWidth);
                int x = Mathf.Min((int)u, ChunkWidth - 1);
                int z = Mathf.Min((int)v, ChunkWidth - 1);
                int a = z * meshWidth + x;
                float open = InterpolateHeight(_cornerOpen[a], _cornerOpen[a + meshWidth], _cornerOpen[a + 1], _cornerOpen[a + meshWidth + 1], u - x, v - z);

                _exposure.Add(new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(open) * 255f)));
            }
        }

        private void BuildSurface(Chunk chunk, float depth, bool hasFootprints)
        {
            chunk.Corners ??= new SurfaceCorner[CornerWidth * CornerWidth];
            chunk.PreparedCorners ??= new SurfaceCorner[CornerWidth * CornerWidth];
            if (!chunk.SurfacePrepared)
            {
                // Normally done ahead in slices (AdvancePublishPreparation); finish any remainder now.
                long started = Stopwatch.GetTimestamp();
                PrepareSurfaceSlice(chunk, long.MaxValue);
                _rebuildPhases.Corners = Stopwatch.GetTimestamp() - started;
            }

            // Steps the snow has outgrown drape into one surface; taller steps stay split at their risers.
            for (int i = 0; i < chunk.Corners.Length; i++)
            {
                chunk.Corners[i] = ResolveCorner(chunk.PreparedCorners[i], depth);
            }

            const int meshWidth = ChunkWidth + 1;
            bool melting = _fireMelts.Count > 0 && !FireMeltOnGpu(chunk);
            if (melting)
            {
                _meltFactors ??= new float[meshWidth * meshWidth];
                if (IsChunkMelting(chunk))
                {
                    chunk.FootprintRefresh = Mathf.Min(chunk.FootprintRefresh, Time.time + MeltRefreshSeconds);
                }
            }

            for (int z = 0; z < meshWidth; z++)
            {
                for (int x = 0; x < meshWidth; x++)
                {
                    int index = (z + 1) * CornerWidth + x + 1;
                    SurfaceCorner corner = chunk.Corners[index];
                    float melt = 1f;
                    if (melting)
                    {
                        melt = FireMeltFactor((chunk.X + x) * CellWidth, (chunk.Z + z) * CellWidth);
                        _meltFactors[z * meshWidth + x] = melt;
                    }

                    float height = melt < 1f ? Mathf.Lerp(corner.Height(0f) - 0.03f, corner.Height(depth), melt) : corner.Height(depth);
                    Vector3 position = new Vector3((chunk.X + x) * CellWidth, height, (chunk.Z + z) * CellWidth);
                    if (corner.HasGround && !corner.Split)
                    {
                        // Where snow ends beside bare or blocked cells, move the corner onto the real edge.
                        Vector2 shift = chunk.OutlineShifts[z * meshWidth + x];
                        position.x += shift.x;
                        position.z += shift.y;
                    }

                    _vertices.Add(position);
                    if (_prints != null)
                    {
                        // Coarse ground shows prints in its lighting until the cells are refined.
                        float rise = (corner.Height(depth + GrowthProbe) - corner.Height(depth)) / GrowthProbe * melt;
                        // Coarse ground shows prints in its lighting only.
                        _printLimits.Add(new Vector4(0f, Mathf.Max(0f, height - corner.Height(0f)) * 0.95f, 0f, rise));
                    }

                    // The one-vertex halo gives adjacent chunks identical normals at the seam.
                    float dx = Slope(chunk.Corners[index - 1], chunk.Corners[index + 1], depth);
                    float dz = Slope(chunk.Corners[index - CornerWidth], chunk.Corners[index + CornerWidth], depth);
                    _normals.Add(new Vector3(-dx, 2f * CellWidth, -dz).normalized);
                }
            }

            for (int z = 0; z < ChunkWidth; z++)
            {
                for (int x = 0; x < ChunkWidth; x++)
                {
                    Column column = GetSurfaceColumn(chunk.X + x, chunk.Z + z);
                    if (!column.Exposed)
                    {
                        continue;
                    }

                    int a = z * meshWidth + x;
                    int b = a + meshWidth;
                    int corner = (z + 1) * CornerWidth + x + 1;
                    if (melting)
                    {
                        float meltA = _meltFactors[a];
                        float meltB = _meltFactors[b];
                        float meltC = _meltFactors[a + 1];
                        float meltD = _meltFactors[b + 1];
                        float lowest = Mathf.Min(Mathf.Min(meltA, meltB), Mathf.Min(meltC, meltD));
                        if (Mathf.Max(Mathf.Max(meltA, meltB), Mathf.Max(meltC, meltD)) < 0.03f)
                        {
                            continue;
                        }

                        if (lowest < 0.999f)
                        {
                            // Thinned cells beside a fire stay one plain quad on the shared vertices.
                            AddSurfaceQuad(a, b, a + 1, b + 1);
                            continue;
                        }
                    }

                    var cell = new CellVertices
                    {
                        A = AddLevelVertex(a, column, chunk.Corners[corner], depth, 0.5f, 0.5f, chunk.X + x, chunk.Z + z),
                        B = AddLevelVertex(b, column, chunk.Corners[corner + CornerWidth], depth, 0.5f, -0.5f, chunk.X + x, chunk.Z + z + 1),
                        C = AddLevelVertex(a + 1, column, chunk.Corners[corner + 1], depth, -0.5f, 0.5f, chunk.X + x + 1, chunk.Z + z),
                        D = AddLevelVertex(b + 1, column, chunk.Corners[corner + CornerWidth + 1], depth, -0.5f, -0.5f, chunk.X + x + 1, chunk.Z + z + 1)
                    };
                    bool hasImpression = hasFootprints && _detailedCells[z * ChunkWidth + x];
                    var heights = new Vector4(_vertices[cell.A].y, _vertices[cell.B].y, _vertices[cell.C].y, _vertices[cell.D].y);
                    bool hasTransition = TryGetGroundTransition(chunk, corner, column, heights, out var transition);
                    if (hasImpression || hasTransition)
                    {
                        AddDetailedCell(chunk, x, z, cell, column, hasImpression, hasTransition, transition);
                    }
                    else
                    {
                        AddSurfaceQuad(cell.A, cell.B, cell.C, cell.D);
                    }
                }
            }
        }

        /// <summary>
        /// Prepares a chunk's surface until the deadline, resuming where the last slice stopped. Every step either
        /// stores its result in the chunk or warms a value memoised in the columns, so the first build only reads: 1.
        /// the surface columns around the chunk, 2. the prepared corners, 3. the outline edge shifts (snow cover does
        /// not change between rebuilds), 4. the edges at split corners, which the surface build searches when it places
        /// each level's edge.
        /// </summary>
        private bool PrepareSurfaceSlice(Chunk chunk, long deadline)
        {
            if (chunk.SurfacePrepared)
            {
                return true;
            }

            const int shiftWidth = ChunkWidth + 1;
            const int warmEnd = SurfaceCacheWidth * SurfaceCacheWidth;
            const int cornersEnd = warmEnd + CornerWidth * CornerWidth;
            const int shiftsEnd = cornersEnd + shiftWidth * shiftWidth;
            const int edgesEnd = shiftsEnd + CornerWidth * CornerWidth;
            if (chunk.PreparedCorners == null || chunk.OutlineShifts == null)
            {
                // Freed or never made: any earlier progress belongs to old arrays.
                chunk.PrepareStep = 0;
            }

            chunk.Corners ??= new SurfaceCorner[CornerWidth * CornerWidth];
            chunk.PreparedCorners ??= new SurfaceCorner[CornerWidth * CornerWidth];
            chunk.OutlineShifts ??= new Vector2[shiftWidth * shiftWidth];

            if (chunk.PrepareStep == 0 && !ScanChunkForStairs(chunk))
            {
                return false;
            }

            long started = Stopwatch.GetTimestamp();
            int step = chunk.PrepareStep;
            try
            {
                int originX = chunk.X - 1 - SurfaceCacheMargin;
                int originZ = chunk.Z - 1 - SurfaceCacheMargin;
                for (; step < warmEnd; step++)
                {
                    GetSurfaceColumn(originX + step % SurfaceCacheWidth, originZ + step / SurfaceCacheWidth);
                    if ((step & 7) == 7 && Stopwatch.GetTimestamp() > deadline)
                    {
                        step++;
                        return false;
                    }
                }

                FillSurfaceCache(chunk);
                for (; step < cornersEnd; step++)
                {
                    int i = step - warmEnd;
                    chunk.PreparedCorners[i] = SampleCorner(chunk.X + i % CornerWidth - 1, chunk.Z + i / CornerWidth - 1);
                    if ((step & 7) == 7 && Stopwatch.GetTimestamp() > deadline)
                    {
                        step++;
                        return false;
                    }
                }

                for (; step < shiftsEnd; step++)
                {
                    int i = step - cornersEnd;
                    int cx = chunk.X + i % shiftWidth, cz = chunk.Z + i / shiftWidth;
                    int mask = (SurfaceAt(cx - 1, cz - 1).Exposed ? 1 : 0) | (SurfaceAt(cx, cz - 1).Exposed ? 2 : 0) | (SurfaceAt(cx - 1, cz).Exposed ? 4 : 0)
                               | (SurfaceAt(cx, cz).Exposed ? 8 : 0);
                    chunk.OutlineShifts[i] = EdgeShift(cx, cz, mask);
                    if ((step & 3) == 3 && Stopwatch.GetTimestamp() > deadline)
                    {
                        step++;
                        return false;
                    }
                }

                for (; step < edgesEnd; step++)
                {
                    int i = step - shiftsEnd;
                    if (chunk.PreparedCorners[i].Split)
                    {
                        int cx = chunk.X + i % CornerWidth - 1, cz = chunk.Z + i / CornerWidth - 1;
                        GetEdgeCrossing(cx - 1, cz - 1, 1, 0);
                        GetEdgeCrossing(cx - 1, cz, 1, 0);
                        GetEdgeCrossing(cx - 1, cz - 1, 0, 1);
                        GetEdgeCrossing(cx, cz - 1, 0, 1);
                        if (Stopwatch.GetTimestamp() > deadline)
                        {
                            step++;
                            return false;
                        }
                    }
                }
            }
            finally
            {
                _surfaceCacheActive = false;
                chunk.PrepareStep = step;
            }

            chunk.SurfacePrepared = true;
            return true;
        }

        private void AddSurfaceQuad(int a, int b)
        {
            AddSurfaceQuad(a, b, a + 1, b + 1);
        }

        private void AddSurfaceQuad(int a, int b, int c, int d)
        {
            _triangles.Add(a);
            _triangles.Add(b);
            _triangles.Add(c);
            _triangles.Add(c);
            _triangles.Add(b);
            _triangles.Add(d);
        }

        private int AddLevelVertex(int shared, Column column, SurfaceCorner corner, float depth, float x, float z, int cornerX, int cornerZ)
        {
            if (!corner.Split)
            {
                return shared;
            }

            // Terminate each side at its own supporting surface. No triangles span a riser.
            Vector3 point = _vertices[shared];
            _lastLevelMask = 0;
            point.y = CellVertexHeight(column, corner, depth, x, z, cornerX, cornerZ);
            if (_lastLevelMask != 0)
            {
                // Undo any outline shift of the shared vertex, then place this level's edge on the riser line.
                Vector2 shift = EdgeShift(cornerX, cornerZ, _lastLevelMask);
                point.x = cornerX * CellWidth + shift.x;
                point.z = cornerZ * CellWidth + shift.y;
            }

            int index = _vertices.Count;
            if (_prints != null)
            {
                // This level's own growth rate, so split levels grow apart the way their rebuild would place them.
                int mask = _lastLevelMask;
                float rise = (CellVertexHeight(column, corner, depth + GrowthProbe, x, z, cornerX, cornerZ) - point.y) / GrowthProbe;
                _lastLevelMask = mask;
                PadPrintLimits();
                _printLimits.Add(new Vector4(0f, depth * 0.95f, 0f, rise));
            }

            _vertices.Add(point);
            _normals.Add(column.Normal);
            return index;
        }

        // Cells on the same level of a split corner share one vertex height, the mean of their own corner heights.
        // Levels are split where the projected ground steps by more than LevelJoinStep, so the riser itself stays
        // split.
        private float SharedLevelHeight(Column column, SurfaceCorner corner, float depth, float x, float z, int cornerX, int cornerZ)
        {
            int count = 0;
            int self = -1;
            for (int dz = -1; dz <= 0; dz++)
            {
                for (int dx = -1; dx <= 0; dx++)
                {
                    float offsetX = dx + 0.5f;
                    float offsetZ = dz + 0.5f;
                    bool isSelf = Mathf.Approximately(offsetX, x) && Mathf.Approximately(offsetZ, z);
                    Column member = isSelf ? column : GetSurfaceColumn(cornerX + dx, cornerZ + dz);
                    if (!isSelf && (!member.Exposed || member.ShowsUpperLevel != column.ShowsUpperLevel))
                    {
                        continue;
                    }

                    if (isSelf)
                    {
                        self = count;
                    }

                    _levelBits[count] = 1 << ((dz + 1) * 2 + dx + 1);
                    _levelGrounds[count] = ProjectGroundHeight(member, offsetX, offsetZ);
                    _levelHeights[count] = CellCornerHeight(member, corner, depth, offsetX, offsetZ);
                    count++;
                }
            }

            if (count <= 1 || self < 0)
            {
                _lastLevelMask = count == 1 ? _levelBits[0] : 0;
                return _levelHeights[0];
            }

            // Single-linkage levels: sort by ground, cut wherever the next ground is more than LevelJoinStep higher.
            for (int i = 0; i < count; i++)
            {
                _levelOrder[i] = i;
            }

            for (int i = 1; i < count; i++)
            {
                int key = _levelOrder[i];
                int j = i - 1;
                while (j >= 0 && _levelGrounds[_levelOrder[j]] > _levelGrounds[key])
                {
                    _levelOrder[j + 1] = _levelOrder[j];
                    j--;
                }

                _levelOrder[j + 1] = key;
            }

            int start = 0;
            for (int i = 1; i <= count; i++)
            {
                if (i < count && _levelGrounds[_levelOrder[i]] - _levelGrounds[_levelOrder[i - 1]] <= LevelJoinStep)
                {
                    continue;
                }

                bool containsSelf = false;
                float sum = 0f;
                int bits = 0;
                for (int k = start; k < i; k++)
                {
                    sum += _levelHeights[_levelOrder[k]];
                    bits |= _levelBits[_levelOrder[k]];
                    containsSelf |= _levelOrder[k] == self;
                }

                if (containsSelf)
                {
                    _lastLevelMask = bits;
                    return sum / (i - start);
                }

                start = i;
            }

            return _levelHeights[self];
        }

        private const float LevelJoinStep = 0.16f;
        private readonly int[] _levelBits = new int[4];

        // The cells (EdgeShift bits) of the level the last SharedLevelHeight call joined; 0 when it joined none.
        private int _lastLevelMask;

        // The height a cell's corner is drawn at.
        private float CellVertexHeight(Column column, SurfaceCorner corner, float depth, float x, float z, int cornerX, int cornerZ)
        {
            if (!corner.Split || (corner.FloorJoined && !column.ShowsUpperLevel))
            {
                return CellCornerHeight(column, corner, depth, x, z);
            }

            return SharedLevelHeight(column, corner, depth, x, z, cornerX, cornerZ);
        }

        private readonly float[] _levelGrounds = new float[4];
        private readonly float[] _levelHeights = new float[4];
        private readonly int[] _levelOrder = new int[4];

        private static float CellCornerHeight(Column column, SurfaceCorner corner, float depth, float x, float z)
        {
            if (!corner.Split)
            {
                return corner.Height(depth);
            }

            if (corner.FloorJoined && !column.ShowsUpperLevel)
            {
                return corner.FloorBase + depth * corner.FloorDepthWeight;
            }

            float ground = ProjectGroundHeight(column, x, z);
            // The upper side of a step too tall to bury ends just under its rim, so no snow lip floats at the edge.
            bool upperSide = ground > corner.Low + corner.Step * 0.5f;
            bool upperRim = !corner.CanDrape && upperSide && !corner.RoofJoin;
            float sideDepth = upperSide ? corner.CappedDepth(depth) : depth;
            float height = ground + sideDepth * CellCornerDepthWeight(column, corner, x, z) + (upperRim ? -0.02f : 0.003f);

            // Snow banked on the lower side of a step never stands above the step it leans on.
            return ground <= corner.Low + corner.Step * 0.5f ? Mathf.Min(height, Mathf.Max(ground + 0.003f, corner.Low + corner.Step)) : height;
        }

        private static float CellCornerDepthWeight(Column column, SurfaceCorner corner, float x, float z)
        {
            if (!corner.Split)
            {
                return corner.DepthWeight;
            }

            // The floor beside a shown roof uses its shared corner, whose weight already includes drift.
            if (corner.FloorJoined && !column.ShowsUpperLevel)
            {
                return corner.FloorDepthWeight;
            }

            float weight = column.ShowsUpperLevel ? corner.DepthWeight : corner.FloorDepthWeight;

            // A ledge taller than any snow keeps its riser, and its upper side rounds off toward the rim instead of
            // ending in a full-depth sheet. A step the snow may still bury keeps full depth, so it does not notch
            // against neighboring corners that have already draped.
            bool upperSide = ProjectGroundHeight(column, x, z) > corner.Low + corner.Step * 0.5f;
            if (!corner.CanDrape && upperSide && !corner.RoofJoin)
            {
                weight *= LedgeRimWeight;
            }
            else if (!upperSide)
            {
                // Frozen lake ice at the foot of a pier or dock keeps its shore share (1 anywhere else).
                weight *= corner.LowShare;

                // The lower side banks against the riser: wind-driven snow collects at the foot of a step.
                if (!column.Sheltered && !column.ShowsUpperLevel)
                {
                    weight *= LedgeBankWeight;
                }
            }

            return column.Sheltered ? weight * column.DoorDriftWeight : weight;
        }

        private static SurfaceCorner ResolveCorner(SurfaceCorner corner, float depth)
        {
            if (!corner.Split || !corner.CanDrape || corner.DrapeBase + depth * corner.DrapeWeight < corner.Low + corner.Step + DrapeCover)
            {
                return corner;
            }

            corner.Split = false;
            corner.BaseHeight = corner.DrapeBase;
            corner.Feather = corner.DrapeFeather;
            corner.DepthWeight = corner.DrapeWeight;
            return corner;
        }

        private SurfaceCorner SampleCorner(int x, int z)
        {
            float sum = 0f;
            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;
            float minNormalY = 1f;
            float accumulation = 0f;
            bool hasPavement = false;
            bool sheltered = false;
            bool upper = false;
            bool floor = false;
            int count = 0;
            float floorSum = 0f;
            float floorMin = float.PositiveInfinity;
            float floorMax = float.NegativeInfinity;
            float floorAccumulation = 0f;
            int floorCount = 0;
            float lowestCell = float.PositiveInfinity;
            float highestCell = float.NegativeInfinity;
            bool highestOnTerrain = false;
            for (int dz = -1; dz <= 0; dz++)
            {
                for (int dx = -1; dx <= 0; dx++)
                {
                    Column column = SurfaceAt(x + dx, z + dz);
                    if (!column.Exposed)
                    {
                        continue;
                    }

                    upper |= column.ShowsUpperLevel;
                    floor |= !column.ShowsUpperLevel;

                    // Project the ground normal from the cell center onto the mesh corner. A continuous slope should
                    // give the same corner height from both sides.
                    float height = ProjectGroundHeight(column, dx + 0.5f, dz + 0.5f);
                    lowestCell = Mathf.Min(lowestCell, column.GroundHeight);
                    if (column.GroundHeight > highestCell)
                    {
                        highestCell = column.GroundHeight;
                        highestOnTerrain = column.OnTerrain;
                    }

                    sum += height;
                    min = Mathf.Min(min, height);
                    max = Mathf.Max(max, height);
                    minNormalY = Mathf.Min(minNormalY, column.Normal.y);
                    accumulation += column.Sheltered ? column.DoorDriftWeight : 1f;
                    if (!column.ShowsUpperLevel)
                    {
                        floorSum += height;
                        floorMin = Mathf.Min(floorMin, height);
                        floorMax = Mathf.Max(floorMax, height);
                        floorAccumulation += column.Sheltered ? column.DoorDriftWeight : 1f;
                        floorCount++;
                    }

                    hasPavement |= column.Pavement;
                    sheltered |= column.Sheltered;
                    count++;
                }
            }

            if (count == 0)
            {
                return default;
            }

            float ledgeHeight = minNormalY >= 0.85f ? (hasPavement && !sheltered ? 0.28f : 0.12f) : 0.22f;
            bool ledge = max - min > ledgeHeight;

            // A drop down to a floor that lies under a roof level with the upper side is not an edge of the snow: the
            // upper side carries on onto the roof's cap (the Woods bunker's earth roof over its doorway drift).
            bool roofJoin = false;
            if (ledge && !upper)
            {
                for (int dz = -1; dz <= 0 && !roofJoin; dz++)
                {
                    for (int dx = -1; dx <= 0 && !roofJoin; dx++)
                    {
                        Column column = SurfaceAt(x + dx, z + dz);
                        roofJoin = column.Exposed && column.GroundHeight < max - UpperLevelRise && RoofFlush(column, max);
                    }
                }
            }

            float nearestGap = FeatherDistance;
            float shelterWeight = 1f;
            float mound = 0f;
            if (!ledge)
            {
                nearestGap = NearestFeatherGap(x, z, upper, sum / count);
                SampleGroundEdge(x, z, sum / count, out shelterWeight, out mound);
            }

            // Buried edges let the original ground and objects provide the visible silhouette.
            float feather = ledge ? 0f : FeatherWeight(nearestGap);

            // The sea bed beside frozen sea ice: no snow, and the skin sinks under the bed so it does not show through
            // the open water (FrozenIce.IsUnderSea).
            if (FrozenIce.IsUnderSea(_origin.x + x * CellWidth, _origin.z + z * CellWidth, _origin.y + lowestCell))
            {
                feather = 0f;
            }

            // Snow on the upper side of a drop thins toward the rim over a reach that wanders with noise, and a raised
            // surface holds less snow than the ground.
            float worldX = _origin.x + x * CellWidth;
            float worldZ = _origin.z + z * CellWidth;
            float raisedShare = 0f;

            // The sea's ice field also covers the beach, so beach terrain up to 2.5 m above the water read as a deck
            // and held a flat 12 cm band with a step at both ends. Only decks and props count as decks; terrain keeps
            // the ground rules.
            bool lakeDeck = !upper && !highestOnTerrain && FrozenIce.IsLakeDeck(worldX, worldZ, _origin.y + highestCell);
            if (lakeDeck)
            {
                // Pier decks and props over the frozen lake hold one thin, even cover. The rim rule left decks 37 cm
                // above the ice almost bare and heaped 40 cm on decks 29 cm above it (FrozenLake.cs).
                raisedShare = 1f;

                // The cover thins over the last cell before a drop to the ice instead of ending in a 12 cm face.
                if (!ledge)
                {
                    feather = Mathf.Min(feather, FeatherWeight(NearestDeckDrop(x, z, highestCell)));
                }
            }
            else if (!upper && count > 0 && minNormalY >= 0.90f)
            {
                // A split corner reads only the cells on its upper side; the lower side is not on the raised surface.
                float rimDistance = CornerRimDistance(x, z, ledge ? min + (max - min) * 0.5f : float.NegativeInfinity);
                if (!ledge)
                {
                    feather = Mathf.Min(feather, RimFeather(rimDistance, x, z));
                }

                raisedShare = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(RaisedFullReach, RaisedFadeReach, rimDistance));
            }

            float ground = Mathf.Lerp(min, sum / count, feather);
            float variation = GroundVariation(x * CellWidth, z * CellWidth);

            // A shown roof is not snow the floor can join.
            bool floorJoined = ledge && upper && floor && floorMax - floorMin <= ledgeHeight;
            float floorFeather = floorJoined ? FeatherWeight(NearestFeatherGap(x, z, false)) : 0f;
            float floorWeight = floorJoined ? floorFeather * variation * (floorAccumulation / floorCount) : variation;
            float depthWeight = feather * variation * (accumulation / count);
            if (SuppressIndoorSnow)
            {
                depthWeight *= shelterWeight;
            }

            // On frozen lake ice: a bank at the shore feathering to nothing over a few metres (FrozenLake.cs). Corner
            // heights are relative to the area origin; the ice field works in world heights.
            float iceShare = FrozenIce.SnowShare(worldX, worldZ, _origin.y + lowestCell);
            depthWeight *= iceShare;

            var corner = new SurfaceCorner
            {
                HasGround = true,
                Split = ledge,
                BaseHeight = ground + Mathf.Lerp(-0.06f, 0.006f, feather),
                Feather = feather,
                DepthWeight = ledge ? variation : depthWeight * (1f + mound * MoundBoost),
                FloorDepthWeight = floorWeight,
                FloorJoined = floorJoined,
                FloorBase = floorJoined ? Mathf.Lerp(floorMin, floorSum / floorCount, floorFeather) + Mathf.Lerp(-0.06f, 0.006f, floorFeather) : 0f,
                Step = max - min,
                Low = min,
                RaisedShare = raisedShare,
                LowShare = iceShare,
                RoofJoin = roofJoin
            };

            // A step lower than the deepest possible snow can be buried. Prepare the surface it would have then, built
            // exactly like an unsplit corner. A shown roof never drapes onto the floor beside it.
            if (ledge && !(upper && floor) && count >= 2 && max - min < MaxDrapeStep)
            {
                float drapeGap = NearestFeatherGap(x, z, upper);
                SampleGroundEdge(x, z, sum / count, out float drapeShelter, out float drapeMound);
                float drapeFeather = FeatherWeight(drapeGap);
                float drapeWeight = drapeFeather * variation * (accumulation / count);
                if (SuppressIndoorSnow)
                {
                    drapeWeight *= drapeShelter;
                }

                corner.CanDrape = true;
                corner.DrapeFeather = drapeFeather;
                corner.DrapeBase = Mathf.Lerp(min, sum / count, drapeFeather) + Mathf.Lerp(-0.06f, 0.006f, drapeFeather);

                corner.DrapeWeight = drapeWeight * (1f + drapeMound * MoundBoost) * iceShare;

                // The step must be lower than the snow this corner actually receives, not the storm maximum.
                corner.DrapeDepth = variation * (accumulation / count);
            }

            // A deck edge never drapes onto the ice: its upper side ends just under the rim, like a tall ledge.
            if (lakeDeck)
            {
                corner.CanDrape = false;
            }

            return corner;
        }

        // Distance from a deck corner to the nearest snowy cell that lies clearly below the deck (the ice, or the
        // ground beside a dock). FeatherDistance when there is none within the feather scan.
        private float NearestDeckDrop(int x, int z, float deckHeight)
        {
            const float drop = 0.10f;
            foreach (var offset in FeatherOffsets)
            {
                Column column = SurfaceAt(x + offset.X, z + offset.Z);
                if (column.Exposed && !column.ShowsUpperLevel && column.GroundHeight < deckHeight - drop)
                {
                    return offset.Distance;
                }
            }

            return FeatherDistance;
        }

        // Corner setup reads each surface column dozens of times through the feather scan. Resolving the chunk and its
        // feather halo once turns about 30,000 lookups per chunk into about 1,200. The rim field (RimCells) reaches
        // further than the feather scan (FeatherCells), so it sets the margin.
        private const int SurfaceCacheMargin = RimCells + 2;
        private const int SurfaceCacheWidth = CornerWidth + 2 * SurfaceCacheMargin;
        private readonly Column[] _surfaceCache = new Column[SurfaceCacheWidth * SurfaceCacheWidth];
        private int _surfaceCacheX;
        private int _surfaceCacheZ;
        private bool _surfaceCacheActive;

        private void FillSurfaceCache(Chunk chunk)
        {
            _surfaceCacheX = chunk.X - 1 - SurfaceCacheMargin;
            _surfaceCacheZ = chunk.Z - 1 - SurfaceCacheMargin;
            for (int z = 0; z < SurfaceCacheWidth; z++)
            {
                for (int x = 0; x < SurfaceCacheWidth; x++)
                {
                    _surfaceCache[z * SurfaceCacheWidth + x] = GetSurfaceColumn(_surfaceCacheX + x, _surfaceCacheZ + z);
                }
            }

            _rawCacheX = chunk.X - 1 - RawCacheMargin;
            _rawCacheZ = chunk.Z - 1 - RawCacheMargin;
            _haloHasMound = false;
            for (int z = 0; z < RawCacheWidth; z++)
            {
                for (int x = 0; x < RawCacheWidth; x++)
                {
                    Column raw = GetColumn(_rawCacheX + x, _rawCacheZ + z);
                    _rawCache[z * RawCacheWidth + x] = raw;
                    _haloHasMound |= raw.MoundStrength > 0f;
                }
            }

            BuildRimDistance();
            _surfaceCacheActive = true;
        }

        // Distance from each cached cell center to the nearest rim on its own level, in meters. A rim cell is a snowy
        // floor cell with a snowy floor neighbor lower by RimDrop or more; it lies half a cell from the edge. Distances
        // spread only between neighbors on one level, so the street below a planter stays unaffected.
        private readonly float[] _rimDistance = new float[SurfaceCacheWidth * SurfaceCacheWidth];

        private void BuildRimDistance()
        {
            const int width = SurfaceCacheWidth;
            for (int z = 0; z < width; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = z * width + x;
                    _rimDistance[i] = float.PositiveInfinity;
                    ref Column cell = ref _surfaceCache[i];
                    if (!OnRimLevel(ref cell))
                    {
                        continue;
                    }

                    for (int dz = -1; dz <= 1 && float.IsPositiveInfinity(_rimDistance[i]); dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if ((dx == 0 && dz == 0) || nx < 0 || nz < 0 || nx >= width || nz >= width)
                            {
                                continue;
                            }

                            ref Column next = ref _surfaceCache[nz * width + nx];
                            // A floor under a roof level with this cell is not a drop: the snow carries on onto the
                            // roof.
                            if (OnRimLevel(ref next) && next.GroundHeight < cell.GroundHeight - RimDrop && !RoofFlush(next, cell.GroundHeight))
                            {
                                _rimDistance[i] = CellWidth * 0.5f;
                                break;
                            }
                        }
                    }
                }
            }

            const float axis = CellWidth;
            const float diagonal = CellWidth * 1.41421356f;
            for (int z = 0; z < width; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    RelaxRim(x, z, x - 1, z, axis);
                    RelaxRim(x, z, x - 1, z - 1, diagonal);
                    RelaxRim(x, z, x, z - 1, axis);
                    RelaxRim(x, z, x + 1, z - 1, diagonal);
                }
            }

            for (int z = width - 1; z >= 0; z--)
            {
                for (int x = width - 1; x >= 0; x--)
                {
                    RelaxRim(x, z, x + 1, z, axis);
                    RelaxRim(x, z, x + 1, z + 1, diagonal);
                    RelaxRim(x, z, x, z + 1, axis);
                    RelaxRim(x, z, x - 1, z + 1, diagonal);
                }
            }
        }

        private static bool OnRimLevel(ref Column column)
        {
            return column.Exposed && column.HasGround && !column.ShowsUpperLevel;
        }

        private void RelaxRim(int x, int z, int nx, int nz, float step)
        {
            const int width = SurfaceCacheWidth;
            if (nx < 0 || nz < 0 || nx >= width || nz >= width)
            {
                return;
            }

            int i = z * width + x;
            int j = nz * width + nx;
            float through = _rimDistance[j] + step;
            if (through >= _rimDistance[i])
            {
                return;
            }

            ref Column cell = ref _surfaceCache[i];
            ref Column next = ref _surfaceCache[j];
            if (OnRimLevel(ref cell) && OnRimLevel(ref next) && Mathf.Abs(cell.GroundHeight - next.GroundHeight) < RimDrop)
            {
                _rimDistance[i] = through;
            }
        }

        // Distance from mesh corner (x, z) to the nearest rim on its level: the mean over its cells, which is exact for
        // a straight rim (0.25 m one cell in, 0.5 m two cells in). A split corner reads only its cells above upperFrom
        // and takes the nearest. Beyond the cache, or with no rim in reach, it reports 2 m.
        private float CornerRimDistance(int x, int z, float upperFrom)
        {
            bool split = !float.IsNegativeInfinity(upperFrom);
            float sum = 0f;
            float nearest = float.PositiveInfinity;
            int count = 0;
            for (int dz = -1; dz <= 0; dz++)
            {
                for (int dx = -1; dx <= 0; dx++)
                {
                    int i = x + dx - _surfaceCacheX;
                    int j = z + dz - _surfaceCacheZ;
                    if (i < 0 || j < 0 || i >= SurfaceCacheWidth || j >= SurfaceCacheWidth)
                    {
                        continue;
                    }

                    ref Column cell = ref _surfaceCache[j * SurfaceCacheWidth + i];
                    if (!OnRimLevel(ref cell) || (split && cell.GroundHeight <= upperFrom))
                    {
                        continue;
                    }

                    float distance = Mathf.Min(_rimDistance[j * SurfaceCacheWidth + i], 2f);
                    sum += distance;
                    nearest = Mathf.Min(nearest, distance);
                    count++;
                }
            }

            return count == 0 ? 2f : split ? nearest : sum / count;
        }

        // The ground-edge scan reads raw columns up to 2.1 m away. Outside Interchange it only looks for mound sources,
        // so a chunk with none nearby skips the scan entirely.
        private const int RawCacheMargin = ShelterTaperCells + 2;
        private const int RawCacheWidth = CornerWidth + 2 * RawCacheMargin;
        private readonly Column[] _rawCache = new Column[RawCacheWidth * RawCacheWidth];
        private int _rawCacheX;
        private int _rawCacheZ;
        private bool _haloHasMound;

        private Column RawAt(int x, int z)
        {
            int i = x - _rawCacheX;
            int j = z - _rawCacheZ;
            return _surfaceCacheActive && i >= 0 && j >= 0 && i < RawCacheWidth && j < RawCacheWidth ? _rawCache[j * RawCacheWidth + i] : GetColumn(x, z);
        }

        private Column SurfaceAt(int x, int z)
        {
            int i = x - _surfaceCacheX;
            int j = z - _surfaceCacheZ;
            return _surfaceCacheActive && i >= 0 && j >= 0 && i < SurfaceCacheWidth && j < SurfaceCacheWidth ? _surfaceCache[j * SurfaceCacheWidth + i] : GetSurfaceColumn(x, z);
        }

        // Distance to the nearest cell this corner's level cannot join: bare ground, or the other level. Thins snow
        // toward a rim. The reach varies between 55 and 95 cm along the edge, so the line where the snow meets the bare
        // surface wanders instead of following one engineered bevel. At a 75 cm reach the corners one, two and three
        // cells in carry about 20, 65 and 95 % of the depth.
        private float RimFeather(float rimDistance, int x, int z)
        {
            float noise = Mathf.PerlinNoise((_origin.x + x * CellWidth) * 1.3f + 17.3f, (_origin.z + z * CellWidth) * 1.3f + 5.1f);
            float reach = Mathf.Lerp(RimReachMin, RimReachMax, noise);
            return Mathf.SmoothStep(0f, 1f, rimDistance / reach);
        }

        // A cell whose stored open roof (Column.HasRoof) lies level with this snow: the ground carries on onto the
        // roof's raised cap there, so it is neither a gap nor a drop.
        private static bool RoofFlush(Column column, float level)
        {
            return column.HasRoof && Mathf.Abs(column.RoofHeight - level) < RoofJoinTolerance;
        }

        // level: the corner's ground height, for the roof join; NaN where it does not apply.
        private float NearestFeatherGap(int x, int z, bool upperLevel, float level = float.NaN)
        {
            foreach (var offset in FeatherOffsets)
            {
                Column column = SurfaceAt(x + offset.X, z + offset.Z);

                if (!upperLevel && !float.IsNaN(level) && RoofFlush(column, level))
                {
                    continue;
                }

                if (!column.Exposed || column.ShowsUpperLevel != upperLevel)
                {
                    return offset.Distance;
                }
            }

            return FeatherDistance;
        }

        private static float FeatherWeight(float nearestGap)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.18f, FeatherDistance, nearestGap));
        }

        private void SampleGroundEdge(int x, int z, float height, out float shelterWeight, out float mound)
        {
            shelterWeight = 1f;
            mound = 0f;
            if (_surfaceCacheActive && !SuppressIndoorSnow && !_haloHasMound)
            {
                return;
            }

            foreach (var offset in GroundEdgeOffsets)
            {
                float moundFalloff = offset.Distance < MoundReach ? DoorDriftFalloff(offset.Distance, MoundReach, 1f) : 0f;
                bool checkMound = moundFalloff > mound;
                bool checkShelter = SuppressIndoorSnow && shelterWeight == 1f;
                if (!checkMound && !checkShelter)
                {
                    break;
                }

                Column neighbor = RawAt(x + offset.X, z + offset.Z);
                if ((!neighbor.Sheltered && neighbor.MoundStrength <= 0f) || !neighbor.HasGround || Mathf.Abs(neighbor.GroundHeight - height) > 0.22f)
                {
                    continue;
                }

                if (checkShelter && neighbor.Sheltered)
                {
                    shelterWeight = Mathf.Min(shelterWeight, ShelterSnowWeight(offset.Distance));
                }
                else if (checkMound && neighbor.MoundStrength > 0f)
                {
                    mound = Mathf.Max(mound, neighbor.MoundStrength * moundFalloff);
                }
            }
        }

        private static float ShelterSnowWeight(float distance)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.10f, ShelterTaperReach, distance));
        }

        private float GroundVariation(float x, float z)
        {
            return Mathf.Lerp(0.82f, 1f, Mathf.PerlinNoise((_origin.x + x) * 0.43f, (_origin.z + z) * 0.43f));
        }

        private static float ProjectGroundHeight(Column column, float sampleOffsetX, float sampleOffsetZ)
        {
            float correction = CellWidth * (column.Normal.x * sampleOffsetX + column.Normal.z * sampleOffsetZ) / Mathf.Max(0.35f, column.Normal.y);
            return column.GroundHeight + Mathf.Clamp(correction, -0.22f, 0.22f);
        }

        private static float Slope(SurfaceCorner a, SurfaceCorner b, float depth)
        {
            return a.HasGround && b.HasGround && !a.Split && !b.Split ? Mathf.Clamp(b.Height(depth) - a.Height(depth), -0.35f, 0.35f) : 0f;
        }

        public void Dispose()
        {
            RestoreDecals();
            DisposeStairSnow();
            foreach (var chunk in _active)
            {
                ReleaseChunk(chunk);
            }

            _active.Clear();
            _expiredChunks.Clear();
            _sampling = null;
            _chunks.Clear();
            DisposeGroundPreview();
            _raisedDustOnly.Clear();
            _groundJoinedSupports.Clear();
            ClearStairFlights();
            _rockSupports.Clear();
            _outdoorProps.Clear();
            _pavementSupports.Clear();
            _surfaceTops.Clear();
            _groundReferences.Clear();
            _largeSupports.Clear();
            _lakeDeckSupports.Clear();
            _softSurfaces.Clear();
            _supportRenderers.Clear();
            _supportBounds.Clear();
            ClearPropSurfaceCache();
            _prints?.Dispose();
            Object.Destroy(_material);
        }

        private static void ReleaseChunk(Chunk chunk)
        {
            ReleaseChunkMesh(chunk);
            chunk.Active = false;
        }

        private static void ReleaseChunkMesh(Chunk chunk)
        {
            if (chunk.Object != null)
            {
                Object.Destroy(chunk.Object);
            }

            if (chunk.Mesh != null)
            {
                Object.Destroy(chunk.Mesh);
            }

            chunk.Object = null;
            chunk.Mesh = null;
            chunk.RaisedRendered = false;
            chunk.GrowthOffset = 0f;
        }

        private struct Column
        {
            public bool Sampled;
            public bool Exposed;
            public bool HasGround;
            public bool Sheltered;
            public bool Pavement;

            // The floor is the terrain itself (or the ice that stands in for it on a frozen lake).
            public bool OnTerrain;
            public float MoundStrength;
            public float GroundHeight;
            public float DoorDriftWeight;
            public Vector3 Normal;
            public bool HasUpperLevel;
            public float UpperHeight;

            // The cell lies on a measured stair flight: its ground is the flight's slope (VoxelStairFlights.cs).
            public bool StairOwned;

            // A walkable surface the ground search passed over, more than 10 cm above the ground it chose.
            public bool HasTop;
            public float TopHeight;

            // Woods bunker stairwells and trenches (VoxelBunkerTrench.cs): BunkerPart when the search met any part of a
            // bunker, BunkerTrench when the open cell lies on or through the bunker's inner shell.
            public bool BunkerPart;
            public bool BunkerTrench;

            // An open roof over this sheltered floor on a terrain map, stored but never shown (see RoofFlush).
            public bool HasRoof;
            public float RoofHeight;
            public Vector3 UpperNormal;
            public bool ShowsUpperLevel;

            // Resolved lazily from neighbors, then cached like doorway drift.
            public bool SurfaceGroundResolved;
            public float SurfaceGround;
            public bool PitFilled;
            public Vector3 SurfaceNormal;
            public bool BaseGroundResolved;
            public bool BaseShaped;
            public float BaseGround;
            public byte BaseKind;
            public byte ShapeKind;
            public byte ShelterKind;
            public float CoverRise;
            public byte EdgeResolved;
            public float EdgeEast;
            public float EdgeNorth;
            public bool DriftFieldResolved;
            public float DriftField;
        }

        private struct SurfaceCorner
        {
            public bool HasGround;
            public bool Split;
            public float BaseHeight;
            public float Feather;
            public float DepthWeight;
            public float FloorDepthWeight;
            public bool FloorJoined;
            public float FloorBase;

            // A split corner also carries the surface it would have if snow covered the step.
            public float Step;
            public float Low;
            public bool CanDrape;

            // The lower side of this split lies under a roof level with the upper side (RoofFlush): the upper side
            // keeps its full depth up to the edge, where the roof's cap takes over.
            public bool RoofJoin;
            public float DrapeBase;
            public float DrapeFeather;
            public float DrapeWeight;
            public float DrapeDepth;

            // Share of RaisedSurfaceDepth's cap that applies: 1 near a rim, 0 on open ground.
            public float RaisedShare;

            // Share of the depth the lower side of a split corner keeps: the frozen lake's shore share, 1 elsewhere.
            public float LowShare;

            public float CappedDepth(float depth) => RaisedShare > 0f ? Mathf.Lerp(depth, Mathf.Min(depth, RaisedSurfaceDepth), RaisedShare) : depth;

            public float Height(float depth) => BaseHeight + CappedDepth(depth) * DepthWeight;
        }

        private struct CellVertices
        {
            public int A;
            public int B;
            public int C;
            public int D;
        }

        private sealed class Chunk
        {
            public readonly Vector2Int Key;
            public readonly int X;
            public readonly int Z;
            public int NextSample;
            public float Depth;
            public bool Active;
            public Column[] Columns;
            public Dictionary<int, RaisedSnowPatch> RaisedSurfaces;
            public int RaisedNextSample;
            public bool RaisedDirty;
            public bool RaisedRendered;
            public bool RaisedReady => RaisedNextSample == ChunkWidth * ChunkWidth;
            public SurfaceCorner[] Corners;
            public SurfaceCorner[] PreparedCorners;
            public Vector2[] OutlineShifts;
            public bool DriftComplete;
            private bool _surfacePrepared;

            // Clearing it restarts the sliced preparation from the top.
            public bool SurfacePrepared
            {
                get => _surfacePrepared;
                set
                {
                    _surfacePrepared = value;
                    if (!value)
                    {
                        PrepareStep = 0;
                    }
                }
            }

            public Mesh Mesh;
            public GameObject Object;
            public float GrowthStarted;
            public float GrowthInset;
            public float GrowthOffset;
            public List<SnowImpression> Footprints;
            public bool FootprintsDirty;

            public float FootprintRefresh = float.PositiveInfinity;

            // GPU prints: the cells the last build refined for ground prints, so a new stamp over them needs no
            // rebuild.
            public bool[] BuiltDetail;

            public float[] RefinedAt;

            // Surface preparation ahead of publishing (PrepareSurfaceSlice).
            public bool PublishQueued;
            public int PrepareStep;

            // Searched for stair flights (VoxelStairFlights.cs); flights stay registered for the raid.
            public bool StairsScanned;
            public int StairScanCursor;
            public int StairScanSeeds;
            public int StairScanRays;

            public Chunk(Vector2Int key)
            {
                Key = key;
                X = key.x * ChunkWidth;
                Z = key.y * ChunkWidth;
            }

            public int ColumnIndex(int x, int z) => (z - Z) * ChunkWidth + x - X;
        }
    }
}
