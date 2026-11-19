using UnityEngine;

namespace WinterOutbreak
{
    /// <summary>
    /// One wind state shared by ground drift, plumes, haze and the fog pulse, so every blowing-snow effect moves the
    /// same way at the same moment.
    /// </summary>
    internal sealed class StormWind
    {
        private readonly System.Random _random = new System.Random();
        private readonly float _seedA;
        private readonly float _seedB;
        private readonly float _seedC;

        private float _clock;
        private const float HistoryStep = 0.1f;
        private const float UpwindReach = 40f;
        private readonly float[] _gustHistory = new float[160];
        private int _historyHead;
        private float _historyTimer;
        private readonly float[] _rhythmSeeds = new float[6];
        private float _nextGust;
        private float _gustStart;
        private float _gustPeak;
        private float _gustHoldEnd;
        private float _gustEnd;
        private float _gustAmplitude;
        private bool _gustIsBig;
        private float _frontArrival = -1f;
        private float _frontDuration;

        /// <summary>
        /// Unit horizontal vector the storm blows toward, matching the native flakes.
        /// </summary>
        public Vector3 Prevailing { get; private set; } = Vector3.right;

        /// <summary>
        /// Current direction, veering a few degrees around the prevailing one.
        /// </summary>
        public Vector3 Direction { get; private set; } = Vector3.right;

        /// <summary>
        /// Horizontal unit vector to the right of <see cref="Direction"/>.
        /// </summary>
        public Vector3 Side { get; private set; } = Vector3.back;

        /// <summary>
        /// Storm level from 0 (calm) to 1 (blizzard), before gusts.
        /// </summary>
        public float Severity { get; private set; }

        /// <summary>
        /// Current gust envelope from 0 to about 1.
        /// </summary>
        public float Gust { get; private set; }

        /// <summary>
        /// Severity combined with gusts and slow flutter, 0 to about 1.6.
        /// </summary>
        public float Strength { get; private set; }

        /// <summary>
        /// How irregular the motion is right now, 0 to 1.
        /// </summary>
        public float Turbulence { get; private set; }

        /// <summary>
        /// Speed in m/s at which loose snow travels near the ground.
        /// </summary>
        public float SnowSpeed { get; private set; }

        /// <summary>
        /// Metres the air has travelled along the prevailing direction this raid.
        /// </summary>
        public float Advected { get; private set; }

        /// <summary>
        /// True for exactly one update when a large gust starts.
        /// </summary>
        public bool BigGustBegan { get; private set; }

        /// <summary>
        /// 0 to 1 while a gust-front snow cloud is passing the camera.
        /// </summary>
        public float FrontPassing { get; private set; }

        public StormWind()
        {
            _seedA = Range(0f, 200f);
            _seedB = Range(0f, 200f);
            _seedC = Range(0f, 200f);
            for (int i = 0; i < _rhythmSeeds.Length; i++)
            {
                _rhythmSeeds[i] = Range(0f, 500f);
            }
        }

        public void Update(Vector2 weatherWind, float severity, float gustRate, float gustScale, float deltaTime)
        {
            deltaTime = Mathf.Clamp(deltaTime, 0f, 0.1f);
            _clock += deltaTime;
            BigGustBegan = false;
            Severity = Mathf.MoveTowards(Severity, Mathf.Clamp01(severity), deltaTime * 0.1f);

            if (weatherWind.sqrMagnitude > 0.0001f)
            {
                var prevailing = new Vector3(weatherWind.x, 0f, weatherWind.y).normalized;
                Prevailing = Vector3.Slerp(Prevailing, prevailing, Mathf.Clamp01(deltaTime * 0.5f)).normalized;
            }

            UpdateGust(gustRate, gustScale, deltaTime);

            float flutter = Mathf.PerlinNoise(_clock * 0.31f, _seedA) - 0.5f;
            float slowFlutter = Mathf.PerlinNoise(_clock * 0.07f, _seedB) - 0.5f;
            Turbulence = Mathf.Clamp01(0.25f + Severity * 0.35f + Gust * 0.5f + flutter * 0.4f);

            // Veer up to about 14 degrees, more while gusting, so motion is never perfectly straight
            float veer = (slowFlutter * 0.9f + flutter * 0.5f * Turbulence) * 0.25f;
            Direction = Quaternion.AngleAxis(veer * Mathf.Rad2Deg, Vector3.up) * Prevailing;
            Side = Vector3.Cross(Vector3.up, Direction).normalized;

            Strength = Mathf.Max(0f, Severity * (1f + slowFlutter * 0.35f + flutter * 0.2f) + Gust * (0.25f + Severity * 0.45f));
            SnowSpeed = Mathf.Lerp(2.6f, 8.5f, Severity) * (1f + Gust * 0.75f + flutter * 0.15f);
            Advected += SnowSpeed * deltaTime;

            _historyTimer += deltaTime;
            while (_historyTimer >= HistoryStep)
            {
                _historyTimer -= HistoryStep;
                _historyHead = (_historyHead + 1) % _gustHistory.Length;
                _gustHistory[_historyHead] = Gust;
            }

            if (_frontArrival >= 0f)
            {
                float t = (_clock - _frontArrival) / _frontDuration;
                FrontPassing = t <= 0f || t >= 1f ? 0f : Mathf.Sin(t * Mathf.PI);
                if (t >= 1f)
                {
                    _frontArrival = -1f;
                }
            }
        }

        private void UpdateGust(float gustRate, float gustScale, float deltaTime)
        {
            if (_clock >= _nextGust && Severity > 0.05f)
            {
                _gustIsBig = Severity > 0.45f && _random.NextDouble() < 0.28f + Severity * 0.3f;
                _gustAmplitude = (_gustIsBig ? Range(0.85f, 1.1f) : Range(0.3f, 0.7f)) * gustScale;
                _gustStart = _clock;
                _gustPeak = _clock + (_gustIsBig ? Range(1.6f, 2.8f) : Range(0.8f, 1.8f));
                _gustHoldEnd = _gustPeak + (_gustIsBig ? Range(2.5f, 5f) : Range(0.6f, 2.5f));
                _gustEnd = _gustHoldEnd + Range(2.5f, 6f);

                float calm = Mathf.Lerp(28f, 7f, Severity) / Mathf.Max(gustRate, 0.1f);
                _nextGust = _gustEnd + calm * Range(0.4f, 1.3f);
                BigGustBegan = _gustIsBig && _gustAmplitude > 0.05f;
            }

            if (_clock < _gustStart || _clock >= _gustEnd)
            {
                Gust = Mathf.MoveTowards(Gust, 0f, 0.5f * deltaTime);
                return;
            }

            float envelope = _clock < _gustPeak
                ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(_gustStart, _gustPeak, _clock))
                : _clock < _gustHoldEnd
                    ? 1f - 0.15f * Mathf.PerlinNoise(_clock * 1.7f, _seedC)
                    : Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(_gustHoldEnd, _gustEnd, _clock));
            Gust = envelope * _gustAmplitude;
        }

        /// <summary>
        /// Records when a gust-front cloud spawned upwind will reach the camera.
        /// </summary>
        public void ScheduleFront(float distance, float depth)
        {
            float speed = Mathf.Max(1f, SnowSpeed);
            _frontArrival = _clock + Mathf.Max(0f, distance - depth * 0.5f) / speed;
            _frontDuration = Mathf.Max(1.5f, depth / speed);
        }

        /// <summary>
        /// Dense airborne-snow pockets as a 0..1 field that travels downwind, so visibility gets worse as a pocket
        /// passes and recovers after it.
        /// </summary>
        public float Pocket(Vector3 position)
        {
            float along = Vector3.Dot(position, Prevailing) - Advected;
            float across = Vector3.Dot(position, Vector3.Cross(Vector3.up, Prevailing));
            float n = Mathf.PerlinNoise(along * 0.011f + _seedA, across * 0.018f + _seedB);
            float detail = Mathf.PerlinNoise(along * 0.04f + _seedC, across * 0.05f);
            float pocket = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.42f, 0.78f, n * 0.8f + detail * 0.25f));
            return pocket * Mathf.Clamp01(0.35f + Strength * 0.65f);
        }

        /// <summary>
        /// Patches of loose snow on the move, 0..1, so drifting varies across the ground instead of covering it in one
        /// even layer.
        /// </summary>
        public float DriftField(Vector3 position)
        {
            float along = Vector3.Dot(position, Prevailing) - Advected * 0.85f;
            float across = Vector3.Dot(position, Vector3.Cross(Vector3.up, Prevailing));
            float n = Mathf.PerlinNoise(along * 0.045f + _seedB, across * 0.075f + _seedC);
            return Mathf.Clamp01((n - 0.25f) * 1.8f);
        }

        public float Range(float min, float max)
        {
            return Mathf.Lerp(min, max, (float)_random.NextDouble());
        }

        /// <summary>
        /// The gust as felt at <paramref name="position"/>.
        /// </summary>
        public float GustAt(Vector3 position, Vector3 center)
        {
            float along = Vector3.Dot(position - center, Prevailing) + UpwindReach;
            float jitter = (Mathf.PerlinNoise(position.x * 0.035f + _seedB, position.z * 0.035f + _seedC) - 0.5f) * 3f;
            float delay = Mathf.Clamp((along / Mathf.Max(2f, SnowSpeed)) + jitter, 0f,
                (_gustHistory.Length - 2) * HistoryStep);
            int steps = Mathf.RoundToInt(delay / HistoryStep);
            int index = ((_historyHead - steps) % _gustHistory.Length + _gustHistory.Length) % _gustHistory.Length;
            return _gustHistory[index];
        }

        /// <summary>
        /// Each effect's own slow rise and fall, 0.25 to about 1.6, cycling over roughly 20 to 50 s and independent of
        /// the other effects, so they do not all peak together.
        /// </summary>
        public float Rhythm(int channel)
        {
            float seed = _rhythmSeeds[channel % _rhythmSeeds.Length];
            float slow = Mathf.PerlinNoise(_clock * 0.03f + seed, seed * 0.37f);
            float quick = Mathf.PerlinNoise(_clock * 0.11f + seed * 1.7f, seed);
            return Mathf.Lerp(0.25f, 1.6f, Mathf.SmoothStep(0f, 1f, slow * 0.75f + quick * 0.35f));
        }

        public bool Chance(float probability)
        {
            return _random.NextDouble() < probability;
        }
    }
}
