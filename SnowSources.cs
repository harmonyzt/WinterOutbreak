using System.Collections.Generic;
using UnityEngine;

namespace WinterOutbreak
{
    /// <summary>
    /// One patch of loose snow on the ground that the wind can pick up.
    /// </summary>
    internal sealed class SnowSource
    {
        public Vector3 Point;
        public Vector3 Normal = Vector3.up;
        public Vector3 Flow;
        public float FlowSpeed = 1f;
        public float Shelter;
        public float Windward;
        public float Crest;
        public bool Obstacle;
        public Vector3 ObstaclePoint;
        public Vector3 ObstacleNormal;
        public bool NearWall;

        /// <summary>
        /// 0 to about 2: how readily the wind lifts snow here (slopes, crests, walls).
        /// </summary>
        public float Exposure;

        /// <summary>
        /// Lasting temperament of this spot, 0 (usually calm) to 1 (usually blowing).
        /// </summary>
        public float Activity;
        public float Period;
        public float Phase;

        /// <summary>
        /// Current activity, ramped smoothly so a spot never switches on or off at once.
        /// </summary>
        public float Level;
        public float StreakBudget;
        public float SheetBudget;
        public float PuffBudget;
        public float EddyBudget;

        public Vector3 EvaluatedFor;
        public bool Evaluated;
        public bool Valid;

        /// <summary>
        /// Ground height at an offset inside the cell, following the sampled slope.
        /// </summary>
        public Vector3 OnGround(Vector3 offset)
        {
            float ny = Mathf.Max(0.5f, Normal.y);
            offset.y = -(Normal.x * offset.x + Normal.z * offset.z) / ny;
            return Point + offset;
        }
    }

    /// <summary>
    /// World-anchored sources of blowing snow.
    /// </summary>
    internal sealed class SnowSources
    {
        private const float StreakFull = 30f;
        private const float ScopeReach = 250f;

        private readonly float Cell;
        private readonly float Radius;
        private readonly float Inner;
        private readonly bool _far;
        private readonly float _spread;
        private readonly int EvaluationsPerFrame;
        private readonly int MaxStreaksPerFrame;
        private readonly int MaxSheetsPerFrame;
        private readonly int MaxPuffsPerFrame;
        private Vector3 _look = Vector3.forward;
        private float _zoom = 1f;

        /// <summary>
        /// The near ring: every effect, fine cells.
        /// </summary>
        public SnowSources() : this(7f, 0f, 42f, false)
        {
        }

        /// <summary>
        /// A ring of sources.
        /// </summary>
        public SnowSources(float cell, float inner, float radius, bool far)
        {
            Cell = cell;
            Inner = inner;
            Radius = radius;
            _far = far;
            _spread = cell / 7f;
            EvaluationsPerFrame = far ? 2 : 4;
            MaxStreaksPerFrame = far ? 0 : 3;
            MaxSheetsPerFrame = far ? 1 : 2;
            MaxPuffsPerFrame = far ? 1 : 2;
        }

        /// <summary>
        /// Optic magnification while aiming, 1 otherwise.
        /// </summary>
        public float Zoom
        {
            set => _zoom = Mathf.Max(1f, value);
        }

        private float Reach => _far && _zoom > 1.5f ? Mathf.Max(Radius, ScopeReach) : Radius;

        private readonly int _groundMask = LayersMaskController.HighPolyWithTerrainNoGrassMask;
        private readonly int _terrainMask = LayersMaskController.TerrainMask;
        private readonly Dictionary<long, SnowSource> _cells = new Dictionary<long, SnowSource>();
        private readonly List<SnowSource> _active = new List<SnowSource>();
        private readonly Queue<SnowSource> _pending = new Queue<SnowSource>();
        private readonly List<long> _stale = new List<long>();
        private float _nextRefresh;
        private int _cursor;
        private float _frontDue;
        private float _vortexChance;

        public void Update(Vector3 center, Vector3 eye, Vector3 look, StormWind wind, WindFlow flow,
            GroundSnowDrift drift, SnowPlumes plumes, float driftIntensity, float plumeIntensity,
            Color light, bool emit, float deltaTime)
        {
            Vector3 flatLook = new Vector3(look.x, 0f, look.z);
            if (flatLook.sqrMagnitude > 0.001f)
            {
                _look = flatLook.normalized;
            }

            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + 0.5f;
                Refresh(center, wind);
            }

            for (int i = 0; i < EvaluationsPerFrame && _pending.Count > 0; i++)
            {
                Evaluate(_pending.Dequeue(), wind, flow);
            }

            float strength = Mathf.Clamp01(wind.Strength);
            float streakRhythm = wind.Rhythm(0);
            float sheetRhythm = wind.Rhythm(1);
            float puffRhythm = wind.Rhythm(2);
            int streaks = 0;
            int sheets = 0;
            int puffs = 0;
            int count = _active.Count;
            look.y = 0f;
            look = look.sqrMagnitude > 0.001f ? look.normalized : wind.Direction;

            for (int n = 0; n < count; n++)
            {
                if (_cursor >= count)
                {
                    _cursor = 0;
                }

                var source = _active[_cursor++];
                if (!source.Valid)
                {
                    continue;
                }

                float target = emit ? Target(source, center, wind, strength) : 0f;
                float rate = target > source.Level ? 0.6f : 0.25f;
                source.Level = Mathf.MoveTowards(source.Level, target, rate * deltaTime);
                if (source.Level < 0.02f)
                {
                    source.StreakBudget = source.SheetBudget = source.PuffBudget = source.EddyBudget = 0f;
                    continue;
                }

                Vector3 toSource = source.Point - eye;
                float distance = toSource.magnitude;
                float facing = Vector3.Dot(toSource, look) / Mathf.Max(distance, 0.01f);
                float view;
                if (_far)
                {
                    // Far snow takes many seconds to reach the player, so behind the camera only a trickle is kept
                    view = Mathf.Lerp(0.12f, 1f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.2f, 0.5f, facing)));
                }
                else if (distance > 12f && facing < 0f && Vector3.Dot(source.Flow, look) < 0.2f)
                {
                    // Behind the camera and blowing away from the view: a reduced rate, never a blank area
                    view = 0.33f;
                }
                else
                {
                    view = 1f;
                }

                float level = source.Level * strength * view;
                float gust = wind.GustAt(source.Point, center);
                float lift = Mathf.Clamp(source.Exposure, 0.2f, 2f);

                if (!_far)
                {
                    // Full rate to 30 m, easing to half at the ring edge
                    float streakFade = Mathf.Lerp(1f, 0.5f, Mathf.InverseLerp(StreakFull, Radius, distance));
                    source.StreakBudget = Mathf.Min(source.StreakBudget
                        + 0.22f * level * driftIntensity * streakRhythm * streakFade * deltaTime, 1.3f);
                }

                float sheetRate = _far ? 0.16f : 0.07f;
                float puffRate = _far ? 0.3f : 0.25f;
                source.SheetBudget = Mathf.Min(source.SheetBudget
                    + sheetRate * level * driftIntensity * sheetRhythm * (0.5f + lift * 0.5f) * deltaTime, 1.3f);
                source.PuffBudget = Mathf.Min(source.PuffBudget
                    + puffRate * level * plumeIntensity * puffRhythm * lift * deltaTime, 1.3f);
                if (source.Obstacle && !_far)
                {
                    source.EddyBudget = Mathf.Min(source.EddyBudget
                        + 0.35f * level * plumeIntensity * deltaTime, 1.3f);
                }

                if (source.StreakBudget >= 1f && streaks < MaxStreaksPerFrame)
                {
                    source.StreakBudget -= 1f;
                    streaks++;
                    drift.EmitStreak(source, wind, gust, driftIntensity, light);
                }

                if (source.SheetBudget >= 1f && sheets < MaxSheetsPerFrame)
                {
                    source.SheetBudget -= 1f;
                    sheets++;
                    drift.EmitSheet(source, wind, gust, driftIntensity, light, _spread);
                }

                if (source.PuffBudget >= 1f && puffs < MaxPuffsPerFrame)
                {
                    source.PuffBudget -= 1f;
                    puffs++;
                    plumes.EmitPuff(source, wind, gust, plumeIntensity, light, _spread);
                }

                if (source.EddyBudget >= 1f && puffs < MaxPuffsPerFrame)
                {
                    source.EddyBudget -= 1f;
                    puffs++;
                    plumes.EmitEddyPuff(source, wind, plumeIntensity, light);
                }
            }

            if (_far || !emit || plumeIntensity <= 0.001f)
            {
                _frontDue = 0f;
                return;
            }

            // Vortices: rare, from an active open spot with no wall close by
            _vortexChance += deltaTime * 0.035f * strength * plumeIntensity * (1f + wind.GustAt(center, center));
            if (_vortexChance >= 1f)
            {
                _vortexChance = 0f;
                var spot = PickActive(s => !s.NearWall && s.Shelter < 0.2f, wind);
                if (spot != null)
                {
                    plumes.StartVortex(spot.Point, wind, plumeIntensity, light);
                }
            }

            // Gust clouds start from a real upwind source a moment after a big gust begins
            if (wind.BigGustBegan)
            {
                _frontDue = Time.time + wind.Range(0.8f, 4f);
            }

            if (_frontDue > 0f && Time.time >= _frontDue)
            {
                _frontDue = 0f;
                Vector3 prevailing = wind.Prevailing;
                var origin = PickActive(s => Vector3.Dot(s.Point - center, prevailing) < -14f && s.Shelter < 0.4f, wind);
                if (origin != null)
                {
                    plumes.EmitGustFront(origin.Point, center, wind, flow, plumeIntensity, light);
                }
            }
        }

        private static float Target(SnowSource source, Vector3 center, StormWind wind, float strength)
        {
            // Each spot breathes on its own cycle
            float cycle = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0.3f, 0.72f, Mathf.PerlinNoise(Time.time / source.Period + source.Phase, source.Phase)));
            float field = wind.DriftField(source.Point);
            float gust = wind.GustAt(source.Point, center);
            float open = (1f - source.Shelter) * (1f - source.Shelter) * (0.55f + source.Exposure * 0.45f);
            float calm = (0.35f + source.Activity * 0.65f) * (cycle * 0.75f + field * 0.45f);
            return Mathf.Clamp01((calm * Mathf.Lerp(0.4f, 1f, strength) + gust * 0.8f) * open);
        }

        private SnowSource PickActive(System.Predicate<SnowSource> filter, StormWind wind)
        {
            SnowSource best = null;
            float bestScore = 0.05f;
            foreach (var source in _active)
            {
                if (!source.Valid || source.Level < 0.2f || !filter(source))
                {
                    continue;
                }

                float score = source.Level * (0.5f + source.Exposure) * wind.Range(0.6f, 1.4f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = source;
                }
            }

            return best;
        }

        private void Refresh(Vector3 center, StormWind wind)
        {
            _active.Clear();
            int cx = Mathf.FloorToInt(center.x / Cell);
            int cz = Mathf.FloorToInt(center.z / Cell);
            float reachDistance = Reach;
            int reach = Mathf.CeilToInt(reachDistance / Cell);
            // Through an optic the far ring stretches along the aim in a cone a little wider than the view
            float coneCos = Mathf.Cos(Mathf.Min(35f, 40f / _zoom + 6f) * Mathf.Deg2Rad);
            float innerSq = Mathf.Max(0f, Inner - Cell * 0.5f);
            innerSq *= innerSq;
            for (int x = -reach; x <= reach; x++)
            {
                for (int z = -reach; z <= reach; z++)
                {
                    int gx = cx + x;
                    int gz = cz + z;
                    var middle = new Vector3((gx + 0.5f) * Cell, center.y, (gz + 0.5f) * Cell);
                    float dx = middle.x - center.x;
                    float dz = middle.z - center.z;
                    float sq = dx * dx + dz * dz;
                    if (sq < innerSq)
                    {
                        continue;
                    }

                    if (sq > Radius * Radius)
                    {
                        float d = Mathf.Sqrt(sq);
                        if (d > reachDistance || (dx * _look.x + dz * _look.z) / d < coneCos)
                        {
                            continue;
                        }
                    }

                    long key = ((long)gx << 32) ^ (uint)gz;
                    if (!_cells.TryGetValue(key, out var source))
                    {
                        source = Create(gx, gz, center.y);
                        _cells[key] = source;
                        _pending.Enqueue(source);
                    }
                    else if (source.Evaluated && Vector3.Dot(source.EvaluatedFor, wind.Prevailing) < 0.97f)
                    {
                        source.Evaluated = false;
                        _pending.Enqueue(source);
                    }

                    _active.Add(source);
                }
            }

            if (_cells.Count > _active.Count * 3)
            {
                float far = (Reach + 25f) * (Reach + 25f);
                _stale.Clear();
                foreach (var pair in _cells)
                {
                    Vector3 d = pair.Value.Point - center;
                    d.y = 0f;
                    if (pair.Value.Evaluated && d.sqrMagnitude > far)
                    {
                        _stale.Add(pair.Key);
                    }
                }

                foreach (long key in _stale)
                {
                    _cells.Remove(key);
                }
            }
        }

        private SnowSource Create(int gx, int gz, float height)
        {
            // Deterministic per location: the same spot keeps the same temperament all raid
            float a = Hash(gx, gz, 1);
            float b = Hash(gx, gz, 2);
            float c = Hash(gx, gz, 3);
            float d = Hash(gx, gz, 4);
            float e = Hash(gx, gz, 5);
            return new SnowSource
            {
                Point = new Vector3((gx + 0.15f + 0.7f * d) * Cell, height, (gz + 0.15f + 0.7f * e) * Cell),
                Activity = Mathf.SmoothStep(0f, 1f, a),
                Period = Mathf.Lerp(9f, 35f, b),
                Phase = c * 100f
            };
        }

        private void Evaluate(SnowSource source, StormWind wind, WindFlow flow)
        {
            source.Evaluated = true;
            source.EvaluatedFor = wind.Prevailing;
            source.Valid = false;
            if (!SnowSurface.TrySampleGround(source.Point, _terrainMask, _groundMask, out var ground)
                || SnowSurface.IsInIndoorVolume(ground.point))
            {
                return;
            }

            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            source.Valid = true;
            source.Point = ground.point;
            source.Normal = ground.normal;
            flow.Sample(ground.point, wind, out source.Flow, out source.FlowSpeed, out source.Shelter);
            Vector3 dir = source.Flow;

            float upwindY = SampleHeight(ground.point - dir * 4f, ground.point.y);
            float downwindY = SampleHeight(ground.point + dir * 4f, ground.point.y);
            source.Windward = Mathf.Clamp01((ground.point.y - upwindY) / 4f * 4f);
            source.Crest = Mathf.Clamp01((ground.point.y - downwindY - 0.3f) * 0.8f) * Mathf.Clamp01(source.Windward + 0.4f);
            float faceUpwind = Mathf.Clamp01(-Vector3.Dot(ground.normal, dir) * 3f);

            if (_far)
            {
                source.Obstacle = false;
                source.NearWall = true;
                source.Exposure = 0.3f + source.Windward * 0.6f + faceUpwind * 0.3f + source.Crest * 0.9f;
                return;
            }

            source.Obstacle = Physics.Raycast(ground.point + Vector3.up * 0.7f, dir, out var hit, 7f, _groundMask, triggers)
                && Mathf.Abs(hit.normal.y) < 0.45f;
            if (source.Obstacle)
            {
                source.ObstaclePoint = hit.point - dir * 0.8f;
                source.ObstacleNormal = hit.normal;
            }

            source.NearWall = Physics.CheckSphere(ground.point + Vector3.up * 1.4f, 2.6f,
                _groundMask & ~_terrainMask, triggers);
            source.Exposure = 0.3f + source.Windward * 0.6f + faceUpwind * 0.3f + source.Crest * 0.9f
                + (source.Obstacle ? 0.4f : 0f);
        }

        private float SampleHeight(Vector3 position, float fallback)
        {
            return SnowSurface.TrySampleGround(position, _terrainMask, _groundMask, out var hit) ? hit.point.y : fallback;
        }

        private static float Hash(int x, int z, int salt)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)(salt * 83492791);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }

        public void Clear()
        {
            _cells.Clear();
            _active.Clear();
            _pending.Clear();
        }
    }
}
