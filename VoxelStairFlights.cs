using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace WinterOutbreak
{
    // Stairs as measured flights, drawn by the ground grid as a smooth slope
    internal sealed partial class VoxelSnowArea
    {
        private const float StairLineStep = 0.02f;
        private const float StairLineReach = 3.0f;
        private const int StairLineSamples = (int)(2f * StairLineReach / StairLineStep) + 1;
        private const float StairFlatTolerance = 0.015f;
        private const float StairMinRise = 0.08f;
        private const float StairMaxRise = 0.26f;
        private const float StairMinTread = 0.18f;
        private const float StairMaxTread = 0.55f;
        private const int StairMinRises = 2;
        private const float StairMinRun = 0.08f;
        private const float StairMaxGap = 0.22f;
        private const float StairSideOffset = 0.25f;
        private const float StairSideStep = 0.05f;
        private const float StairMaxHalfWidth = 3.0f;
        private const float StairMinWidth = 0.6f;
        private const int MaxStairFlights = 1024;
        private const int StairSeedsPerChunk = 10;

        // At most this many rays per chunk scan, so a chunk full of false seeds cannot stall a preparation slice.
        private const int StairRaysPerChunk = 4000;

        private readonly List<StairFlight> _stairFlights = new List<StairFlight>();
        private readonly Dictionary<long, int> _stairCells = new Dictionary<long, int>();
        private readonly HashSet<long> _stairTried = new HashSet<long>();
        private readonly float[] _stairLineY = new float[StairLineSamples];
        private readonly bool[] _stairLineValid = new bool[StairLineSamples];
        private readonly List<StairRun> _stairRuns = new List<StairRun>();

        private long _stairSeeds;
        private long _stairRays;
        private long _stairTicks;
        private int _stairCellCount;

        private struct StairRun
        {
            public float Start;
            public float End;
            public float Height;
        }

        private sealed class StairFlight
        {
            public Vector2 Origin;
            public Vector2 Dir;
            public Vector2 Side;

            // Risers bottom to top (distance along Dir from Origin); tread k lies between riser k and riser k + 1.
            public float[] Risers;
            public float[] Heights;
            public float Top;
            public float Bottom;
            public float Left;
            public float Right;

            // Each tread's own width, 0 where it could not be measured. Steps that wrap around a corner have shorter
            // upper treads, and one width for the whole flight left slabs standing in the air.
            public float[] TreadLeft;
            public float[] TreadRight;
            public float Rise;

            // The slope through the tread noses: from the first tread at the first riser to the top at the last one.
            public Vector3 RampNormal;
        }

        private static long CellKey(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        // On a flight or the ring of cells around it.
        private bool NearStairFlight(int x, int z)
        {
            if (_stairCells.Count == 0)
            {
                return false;
            }

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (_stairCells.ContainsKey(CellKey(x + dx, z + dz)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // At most this much stair measuring per frame, over all chunks
        private const double StairFrameBudgetMs = 2.0;
        private const double CountdownStairFrameBudgetMs = 10.0;
        private bool _stairCountdown;
        private int _stairFrame = -1;
        private long _stairFrameTicks;

        // Called before a chunk's surface is prepared: likely stair cells start a measurement
        private bool ScanChunkForStairs(Chunk chunk)
        {
            if (chunk.StairsScanned || chunk.Columns == null || _stairFlights.Count >= MaxStairFlights)
            {
                return true;
            }

            // Parts of the map measured in an earlier raid keep the flights read from the cache (VoxelStairCache.cs).
            if (IsStairTileCached(chunk))
            {
                chunk.StairsScanned = true;
                return true;
            }

            if (Time.frameCount != _stairFrame)
            {
                _stairFrame = Time.frameCount;
                _stairFrameTicks = 0;
            }

            double budgetMs = _stairCountdown ? CountdownStairFrameBudgetMs : StairFrameBudgetMs;
            long budget = (long)(budgetMs * Stopwatch.Frequency / 1000.0);
            if (_stairFrameTicks >= budget)
            {
                return false;
            }

            long started = Stopwatch.GetTimestamp();
            bool finished = true;
            for (; chunk.StairScanCursor < ChunkWidth * ChunkWidth && chunk.StairScanSeeds < StairSeedsPerChunk
                && chunk.StairScanRays < StairRaysPerChunk; chunk.StairScanCursor++)
            {
                if (_stairFrameTicks + (Stopwatch.GetTimestamp() - started) >= budget)
                {
                    finished = false;
                    break;
                }

                int i = chunk.StairScanCursor;
                int x = chunk.X + i % ChunkWidth;
                int z = chunk.Z + i / ChunkWidth;

                // Every other cell is enough to meet any flight of two or more cells
                if (((x + z) & 1) != 0)
                {
                    continue;
                }

                long key = CellKey(x, z);
                if (_stairCells.ContainsKey(key) || _stairTried.Contains(key))
                {
                    continue;
                }

                // Stairs are shitty, plain terrain never seeds one.
                Column column = chunk.Columns[i];
                if (!column.Sampled || !column.HasGround || (column.OnTerrain && !column.HasTop) || column.BunkerTrench
                    || !LooksLikeStairs(x, z, SeedHeight(column), out Vector2 uphill))
                {
                    continue;
                }

                chunk.StairScanSeeds++;
                _stairSeeds++;
                long raysBefore = _stairRays;
                if (!TryMeasureFlight(x, z, SeedHeight(column), uphill))
                {
                    MarkStairTried(x, z, 1);
                }

                chunk.StairScanRays += (int)(_stairRays - raysBefore);
            }

            long spent = Stopwatch.GetTimestamp() - started;
            _stairTicks += spent;
            _stairFrameTicks += spent;
            if (!finished)
            {
                return false;
            }

            chunk.StairsScanned = true;
            MarkStairTileScanned(chunk);
            return true;
        }

        private void MarkStairTried(int x, int z, int radius)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    _stairTried.Add(CellKey(x + dx, z + dz));
                }
            }
        }

        // The sampled ground climbs like a staircase along a line of cells through this one
        private bool LooksLikeStairs(int x, int z, float own, out Vector2 uphill)
        {
            uphill = Vector2.zero;
            int bestSteps = 0;
            for (int axis = 0; axis < 4; axis++)
            {
                int dx = axis == 0 ? 1 : axis == 1 ? 0 : 1;
                int dz = axis == 0 ? 0 : axis == 1 ? 1 : axis == 2 ? 1 : -1;
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    int steps = CountStairSteps(x, z, dx * sign, dz * sign, own);
                    if (steps > bestSteps)
                    {
                        bestSteps = steps;
                        uphill = new Vector2(dx * sign, dz * sign).normalized;
                    }
                }
            }

            return bestSteps >= 2;
        }

        // The surface a person sees: the walkable top the ground search passed over, or else the ground.
        private static float SeedHeight(Column column)
        {
            return column.HasTop ? column.TopHeight : column.GroundHeight;
        }

        private bool TryGetSeedHeight(int x, int z, out float height)
        {
            Column column = GetColumn(x, z);
            height = SeedHeight(column);
            return column.Sampled && column.HasGround;
        }

        private int CountStairSteps(int x, int z, int dx, int dz, float own)
        {
            float previous;
            if (!TryGetSeedHeight(x - 3 * dx, z - 3 * dz, out previous))
            {
                return 0;
            }

            int steps = 0;
            float first = previous;
            for (int k = -2; k <= 3; k++)
            {
                if (!TryGetSeedHeight(x + k * dx, z + k * dz, out float ground))
                {
                    return 0;
                }

                float rise = ground - previous;
                if (rise >= 0.07f && rise <= 0.30f)
                {
                    steps++;
                }
                else if (Mathf.Abs(rise) > 0.04f)
                {
                    return 0;
                }

                previous = ground;
            }

            float total = previous - first;
            return total >= 0.2f && total <= 1.8f ? steps : 0;
        }

        private bool TryMeasureFlight(int x, int z, float own, Vector2 uphill)
        {
            Vector3 seed = GroundPoint(x, z, own);
            Vector2 dir = uphill;
            var risers = new List<float>();
            var heights = new List<float>();
            float top;
            float bottom;
            if (!TryReadStairLine(seed, dir, 0f, risers, heights, out top, out bottom))
            {
                dir = -dir;
                if (!TryReadStairLine(seed, dir, 0f, risers, heights, out top, out bottom))
                {
                    NoteFlightRejection(seed, "no regular steps on the centre line");
                    return false;
                }
            }

            // Turn the climb square to the risers: compare where the risers cross two lines beside the centre line.
            Vector2 side = new Vector2(-dir.y, dir.x);
            var plusRisers = new List<float>();
            var minusRisers = new List<float>();
            var plusHeights = new List<float>();
            var minusHeights = new List<float>();
            float minusTop = 0f;
            bool sidesRead = TryReadStairLine(seed, dir, StairSideOffset, plusRisers, plusHeights, out float plusTop, out _)
                && TryReadStairLine(seed, dir, -StairSideOffset, minusRisers, minusHeights, out minusTop, out _);
            float skew = sidesRead
                ? MatchRiserSkew(plusRisers, plusHeights, plusTop, minusRisers, minusHeights, minusTop)
                : float.NaN;
            if (float.IsNaN(skew) || Mathf.Abs(skew) >= 1f)
            {
                NoteFlightRejection(seed, sidesRead
                    ? $"side lines disagree (risers at {RiserList(plusRisers)} and {RiserList(minusRisers)}, centre {RiserList(risers)})"
                    : "a side line read no steps");
                return false;
            }

            Vector2 turned = (dir * (2f * StairSideOffset) - side * skew).normalized;
            var turnedRisers = new List<float>();
            var turnedHeights = new List<float>();
            if (TryReadStairLine(seed, turned, 0f, turnedRisers, turnedHeights, out float turnedTop, out float turnedBottom))
            {
                dir = turned;
                side = new Vector2(-dir.y, dir.x);
                risers = turnedRisers;
                heights = turnedHeights;
                top = turnedTop;
                bottom = turnedBottom;
            }

            // Width: sideways along each tread until the surface leaves the tread's height.
            var left = new List<float>();
            var right = new List<float>();
            for (int k = 0; k < heights.Count; k++)
            {
                float s = (risers[k] + risers[k + 1]) * 0.5f;
                left.Add(MeasureTreadSide(seed, dir, -side, s, heights[k]));
                right.Add(MeasureTreadSide(seed, dir, side, s, heights[k]));
            }

            float[] treadLeft = left.ToArray();
            float[] treadRight = right.ToArray();
            left.Sort();
            right.Sort();
            float leftWidth = left[left.Count / 3];
            float rightWidth = right[right.Count / 3];
            if (leftWidth + rightWidth < StairMinWidth)
            {
                NoteFlightRejection(seed, $"too narrow ({leftWidth + rightWidth:F2} m)");
                return false;
            }

            var flight = new StairFlight
            {
                Origin = new Vector2(seed.x, seed.z),
                Dir = dir,
                Side = side,
                Risers = risers.ToArray(),
                Heights = heights.ToArray(),
                Top = top,
                Bottom = bottom,
                Left = leftWidth,
                Right = rightWidth,
                TreadLeft = treadLeft,
                TreadRight = treadRight,
                Rise = (top - heights[0]) / heights.Count
            };

            float run = flight.Risers[flight.Risers.Length - 1] - flight.Risers[0];
            float slope = (top - heights[0]) / Mathf.Max(0.1f, run);
            flight.RampNormal = new Vector3(-dir.x * slope, 1f, -dir.y * slope).normalized;
            if (!RegisterFlight(flight))
            {
                NoteFlightRejection(seed, $"overlaps a registered flight ({risers.Count} risers, bottom {bottom:F3})");
                return false;
            }

            return true;
        }

        // Rays straight down every 2 cm along a line through the seed. Flat stretches are treads; a run of three or
        // more regular steps up (8-26 cm rise, 18-55 cm treads, rises within 5 cm of each other) is the flight.
        private bool TryReadStairLine(Vector3 seed, Vector2 dir, float offset, List<float> risers, List<float> heights,
            out float top, out float bottom)
        {
            risers.Clear();
            heights.Clear();
            top = 0f;
            bottom = 0f;
            Vector2 side = new Vector2(-dir.y, dir.x);
            Vector2 start = new Vector2(seed.x, seed.z) + side * offset;
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            for (int i = 0; i < StairLineSamples; i++)
            {
                float s = -StairLineReach + i * StairLineStep;
                Vector2 p = start + dir * s;
                var origin = new Vector3(p.x, seed.y + 3f, p.y);
                
                // Anything higher than a tread could be along this line (a canopy over a bunker entrance) is passed
                // through, so the treads under it are read.
                float highest = seed.y + Mathf.Abs(s) * 1.1f + 0.4f;
                RaycastHit hit = default;
                bool found = false;
                for (int pass = 0; pass < 3; pass++)
                {
                    _stairRays++;
                    if (!Physics.Raycast(origin, Vector3.down, out hit, origin.y - (seed.y - 3f), _solidMask | _terrainMask,
                        triggers))
                    {
                        break;
                    }

                    if (hit.point.y <= highest)
                    {
                        found = true;
                        break;
                    }

                    origin.y = hit.point.y - 0.05f;
                }

                bool valid = found && hit.normal.y > 0.95f && (hit.rigidbody == null || hit.rigidbody.isKinematic)
                    && !IsGroundObstruction(hit.collider);
                _stairLineValid[i] = valid;
                _stairLineY[i] = valid ? hit.point.y : 0f;
            }

            // Flat runs along the line.
            _stairRuns.Clear();
            int runStart = -1;
            float runFirst = 0f;
            float runSum = 0f;
            for (int i = 0; i <= StairLineSamples; i++)
            {
                bool continues = i < StairLineSamples && _stairLineValid[i] && runStart >= 0
                    && Mathf.Abs(_stairLineY[i] - runFirst) < StairFlatTolerance;
                if (continues)
                {
                    runSum += _stairLineY[i];
                    continue;
                }

                if (runStart >= 0)
                {
                    int count = i - runStart;
                    float length = (count - 1) * StairLineStep;
                    if (length >= StairMinRun)
                    {
                        _stairRuns.Add(new StairRun
                        {
                            Start = -StairLineReach + runStart * StairLineStep,
                            End = -StairLineReach + (i - 1) * StairLineStep,
                            Height = runSum / count
                        });
                    }
                }

                runStart = -1;
                if (i < StairLineSamples && _stairLineValid[i])
                {
                    runStart = i;
                    runFirst = _stairLineY[i];
                    runSum = _stairLineY[i];
                }
            }

            // The longest chain of regular steps up.
            int bestFirst = -1;
            int bestLast = -1;
            for (int first = 0; first < _stairRuns.Count; first++)
            {
                int last = first;
                while (last + 1 < _stairRuns.Count && IsStairStep(_stairRuns[last], _stairRuns[last + 1])
                    && FitsChain(first, last + 1))
                {
                    last++;
                }

                if (last - first > bestLast - bestFirst)
                {
                    bestFirst = first;
                    bestLast = last;
                }
            }

            if (bestFirst < 0 || bestLast - bestFirst < StairMinRises)
            {
                return false;
            }

            // A bottom step sunk into the ground rises less than the others.
            bool shortFoot = false;
            if (bestFirst > 0 && IsStairStep(_stairRuns[bestFirst - 1], _stairRuns[bestFirst]))
            {
                bestFirst--;
                shortFoot = true;
            }

            for (int r = bestFirst; r < bestLast; r++)
            {
                risers.Add((_stairRuns[r].End + _stairRuns[r + 1].Start) * 0.5f);
            }

            for (int r = bestFirst + 1; r < bestLast; r++)
            {
                heights.Add(_stairRuns[r].Height);
            }

            top = _stairRuns[bestLast].Height;
            bottom = _stairRuns[bestFirst].Height;

            // every tread 18-55 cm deep, every rise within 5 cm of the middle one.
            var rises = new List<float>();
            for (int r = shortFoot ? bestFirst + 1 : bestFirst; r < bestLast; r++)
            {
                rises.Add(_stairRuns[r + 1].Height - _stairRuns[r].Height);
            }

            rises.Sort();
            float middle = rises[rises.Count / 2];
            foreach (float rise in rises)
            {
                if (Mathf.Abs(rise - middle) > 0.05f)
                {
                    return false;
                }
            }

            for (int k = 0; k + 1 < risers.Count; k++)
            {
                float depth = risers[k + 1] - risers[k];
                if (depth < StairMinTread || depth > StairMaxTread)
                {
                    return false;
                }
            }

            return heights.Count >= StairMinRises - 1;
        }

        private static bool IsStairStep(StairRun low, StairRun high)
        {
            float rise = high.Height - low.Height;
            return rise >= StairMinRise && rise <= StairMaxRise && high.Start - low.End <= StairMaxGap;
        }

        // Run `next` keeps the chain that starts at run `first` regular: the same rise within 5cm
        private bool FitsChain(int first, int next)
        {
            float firstRise = _stairRuns[first + 1].Height - _stairRuns[first].Height;
            float rise = _stairRuns[next].Height - _stairRuns[next - 1].Height;
            if (Mathf.Abs(rise - firstRise) > 0.05f)
            {
                return false;
            }

            if (next - first < 2)
            {
                return true;
            }

            float lowerRiser = (_stairRuns[next - 2].End + _stairRuns[next - 1].Start) * 0.5f;
            float upperRiser = (_stairRuns[next - 1].End + _stairRuns[next].Start) * 0.5f;
            float tread = upperRiser - lowerRiser;
            return tread >= StairMinTread && tread <= StairMaxTread;
        }

        // How far the risers on the +offset line sit ahead of those on the -offset line. Risers are matched by the
        // height of the tread above them, so a line that starts or stops a step early still pairs up with the other.
        private static float MatchRiserSkew(List<float> plus, List<float> plusHeights, float plusTop,
            List<float> minus, List<float> minusHeights, float minusTop)
        {
            float sum = 0f;
            float low = float.PositiveInfinity;
            float high = float.NegativeInfinity;
            int count = 0;
            for (int i = 0; i < plus.Count; i++)
            {
                float height = i < plusHeights.Count ? plusHeights[i] : plusTop;
                for (int j = 0; j < minus.Count; j++)
                {
                    float other = j < minusHeights.Count ? minusHeights[j] : minusTop;
                    if (Mathf.Abs(height - other) > 0.04f)
                    {
                        continue;
                    }

                    float offset = plus[i] - minus[j];
                    sum += offset;
                    low = Mathf.Min(low, offset);
                    high = Mathf.Max(high, offset);
                    count++;
                    break;
                }
            }

            // The two lines must have read the same treads: every offset small and all of them alike
            if (count < 2 || high - low > 0.08f || Mathf.Abs(sum / count) > 0.3f)
            {
                return float.NaN;
            }

            return sum / count;
        }

        private float MeasureTreadSide(Vector3 seed, Vector2 dir, Vector2 side, float s, float height)
        {
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            Vector2 centre = new Vector2(seed.x, seed.z) + dir * s;
            float reached = 0f;
            for (float t = StairSideStep; t <= StairMaxHalfWidth; t += StairSideStep)
            {
                Vector2 p = centre + side * t;
                _stairRays++;
                if (!Physics.Raycast(new Vector3(p.x, height + 1.5f, p.y), Vector3.down, out var hit, 2f,
                        _solidMask | _terrainMask, triggers)
                    || Mathf.Abs(hit.point.y - height) > 0.03f || hit.normal.y < 0.95f)
                {
                    break;
                }

                reached = t;
            }

            return reached > 0f ? reached + StairSideStep * 0.5f : 0f;
        }

        // How far a flight claims the landings at its foot and head, so the floor there is the floor the stairs start
        // from and not the terrain under a bunker.
        private const float StairLandingReach = 0.5f;
        private const float StairClaimMargin = 0.15f;

        private bool RegisterFlight(StairFlight flight)
        {
            float s0 = flight.Risers[0] - StairLandingReach;
            float s1 = flight.Risers[flight.Risers.Length - 1] + StairLandingReach;

            // Cells whose centre lies on a tread or a landing strip
            var cells = new List<long>();
            int owned = 0;
            float left = flight.Left + StairClaimMargin;
            float right = flight.Right + StairClaimMargin;
            for (int k = 0; k < flight.Heights.Length; k++)
            {
                left = Mathf.Max(left, TreadLeft(flight, k) + StairClaimMargin);
                right = Mathf.Max(right, TreadRight(flight, k) + StairClaimMargin);
            }

            Vector2 a = flight.Origin + flight.Dir * s0 - flight.Side * left;
            Vector2 b = flight.Origin + flight.Dir * s0 + flight.Side * right;
            Vector2 c = flight.Origin + flight.Dir * s1 - flight.Side * left;
            Vector2 d = flight.Origin + flight.Dir * s1 + flight.Side * right;
            float minX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)) - _origin.x;
            float maxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x)) - _origin.x;
            float minZ = Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y)) - _origin.z;
            float maxZ = Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y)) - _origin.z;
            for (int z = Mathf.FloorToInt(minZ / CellWidth); z <= Mathf.FloorToInt(maxZ / CellWidth); z++)
            {
                for (int x = Mathf.FloorToInt(minX / CellWidth); x <= Mathf.FloorToInt(maxX / CellWidth); x++)
                {
                    Vector2 centre = new Vector2(_origin.x + (x + 0.5f) * CellWidth, _origin.z + (z + 0.5f) * CellWidth);
                    if (TreadCovers(flight, centre, true, StairClaimMargin))
                    {
                        long key = CellKey(x, z);
                        cells.Add(key);
                        if (_stairCells.ContainsKey(key))
                        {
                            owned++;
                        }
                    }
                }
            }

            if (cells.Count < 4)
            {
                return false;
            }

            // The same flight measured again from another seed
            int id = _stairFlights.Count;
            if (owned * 3 > cells.Count)
            {
                if (!TryFindShorterReading(flight, cells, out id))
                {
                    return false;
                }

                _stairFlights[id] = flight;
                if (id < _stairSnowExposure.Count)
                {
                    _stairSnowExposure[id] = MeasureTreadExposure(flight);
                }

                _stairSnowFlights = -1;
                _nextStairSnowBuild = 0f;
            }
            else
            {
                _stairFlights.Add(flight);
            }
            var touched = new HashSet<Vector2Int>();
            foreach (long key in cells)
            {
                if (_stairCells.ContainsKey(key))
                {
                    continue;
                }

                int x = (int)(key >> 32);
                int z = (int)(uint)key;
                _stairCells.Add(key, id);
                _stairCellCount++;

                // Cells sampled already take the slope now; later samples take it as they are stored.
                Chunk owner = GetChunk(x, z);
                if (owner?.Columns != null)
                {
                    ApplyStairRamp(x, z, ref owner.Columns[owner.ColumnIndex(x, z)]);
                }

                touched.Add(KeyForCell(x, z));
            }

            var refresh = new HashSet<Vector2Int>();
            foreach (var key in touched)
            {
                for (int kz = key.y - 1; kz <= key.y + 1; kz++)
                {
                    for (int kx = key.x - 1; kx <= key.x + 1; kx++)
                    {
                        refresh.Add(new Vector2Int(kx, kz));
                    }
                }
            }

            foreach (var key in refresh)
            {
                if (!_chunks.TryGetValue(key, out var chunk))
                {
                    continue;
                }

                if (chunk.Columns != null)
                {
                    for (int i = 0; i < chunk.Columns.Length; i++)
                    {
                        ref Column column = ref chunk.Columns[i];
                        column.BaseGroundResolved = false;
                        column.SurfaceGroundResolved = false;
                        column.PitFilled = false;
                        column.EdgeResolved = 0;
                        column.DriftFieldResolved = false;
                    }
                }

                chunk.SurfacePrepared = false;
                if (chunk.Mesh != null)
                {
                    chunk.RaisedDirty = true;
                }
            }

            return true;
        }
        
        private bool TryFindShorterReading(StairFlight flight, List<long> cells, out int id)
        {
            var counts = new Dictionary<int, int>();
            foreach (long key in cells)
            {
                if (_stairCells.TryGetValue(key, out int owner))
                {
                    counts.TryGetValue(owner, out int count);
                    counts[owner] = count + 1;
                }
            }

            id = -1;
            int best = 0;
            foreach (var pair in counts)
            {
                if (pair.Value > best)
                {
                    best = pair.Value;
                    id = pair.Key;
                }
            }

            if (id < 0)
            {
                return false;
            }

            StairFlight old = _stairFlights[id];
            bool sameClimb = Vector2.Dot(old.Dir, flight.Dir) > 0.97f;
            bool sameEnd = Mathf.Abs(old.Top - flight.Top) < 0.05f || Mathf.Abs(old.Bottom - flight.Bottom) < 0.05f;
            return sameClimb && sameEnd && flight.Risers.Length > old.Risers.Length;
        }


        private void ApplyStairRamp(int x, int z, ref Column column)
        {
            if (_stairCells.Count == 0 || !column.Sampled || !column.HasGround || column.StairOwned
                || !_stairCells.TryGetValue(CellKey(x, z), out int id))
            {
                return;
            }

            StairFlight flight = _stairFlights[id];
            Vector2 centre = new Vector2(_origin.x + (x + 0.5f) * CellWidth, _origin.z + (z + 0.5f) * CellWidth)
                - flight.Origin;
            float s = Vector2.Dot(centre, flight.Dir);
            float ramp = RampHeight(flight, s) - _origin.y;
            float tolerance = flight.Rise + 0.06f;
            bool onGround = Mathf.Abs(column.GroundHeight - ramp) <= tolerance;
            bool onTop = !onGround && column.HasTop && Mathf.Abs(column.TopHeight - ramp) <= tolerance;
            if (!onGround && !onTop)
            {
                return;
            }

            // Its grid snow runs on at the foot's level
            bool onSlope = s > flight.Risers[0] && s < flight.Risers[flight.Risers.Length - 1];
            
            // A landing cell is never lowered
            float foot = flight.Bottom - _origin.y;
            bool underOpenStairs = onSlope && column.GroundHeight < foot;
            column.GroundHeight = onSlope ? Mathf.Min(foot, column.GroundHeight) : Mathf.Max(ramp, column.GroundHeight);
            column.Normal = Vector3.up;
            column.OnTerrain = false;
            column.StairOwned = true;
            column.HasUpperLevel = false;
            if (onTop && !underOpenStairs)
            {
                // Ask again at the stairs
                column.Sheltered = IsSheltered(GroundPoint(x, z, ramp) + Vector3.up * 0.40f);
                column.ShelterKind = _lastShelterKind;
                column.CoverRise = _lastCoverRise;
                column.Exposed = !column.Sheltered;
            }
        }
        
        private static float RampHeight(StairFlight flight, float s)
        {
            float s0 = flight.Risers[0];
            float s1 = flight.Risers[flight.Risers.Length - 1];
            if (s <= s0)
            {
                return flight.Bottom;
            }

            if (s >= s1)
            {
                return flight.Top;
            }

            return Mathf.Lerp(flight.Heights[0], flight.Top, Mathf.Clamp01((s - s0) / Mathf.Max(0.1f, s1 - s0)));
        }

        // Only the latest are kept
        private readonly List<(Vector3 Seed, string Reason)> _flightRejections = new List<(Vector3, string)>();

        private static string RiserList(List<float> risers)
        {
            var parts = new List<string>();
            foreach (float s in risers)
            {
                parts.Add(s.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return parts.Count == 0 ? "none" : string.Join(" ", parts);
        }

        private void NoteFlightRejection(Vector3 seed, string reason)
        {
            if (_flightRejections.Count >= 400)
            {
                _flightRejections.RemoveAt(0);
            }

            _flightRejections.Add((seed, reason));
        }

        private void ClearStairFlights()
        {
            _flightRejections.Clear();
            _stairFlights.Clear();
            _stairCells.Clear();
            _stairTried.Clear();
            _stairCellCount = 0;
        }
    }
}
