using System.Collections.Generic;
using UnityEngine;

namespace WinterOutbreak
{
    internal sealed partial class VoxelSnowArea
    {
        // Bare ground inside this radius of a fire, feathering back to full snow over MeltFeather
        private const float MeltRadius = 1.6f;
        private const float MeltFeather = 1.4f;
        private const float MeltSeconds = 45f;
        private const float SameFireDistance = 0.75f;
        private const float MeltRefreshSeconds = 1f;

        private readonly List<FireMelt> _fireMelts = new List<FireMelt>();
        private float[] _meltFactors;

        private struct FireMelt
        {
            public Vector2 Center;
            public float Started;
            public float StampedProgress;
            // One outline for the fire's whole melt, so it grows instead of changing shape at each step
            public Vector2 Phases;
            public bool Refined;
        }

        private const float FireMeltStampSeconds = 2f;
        private const float FireMeltTrailSeconds = 0.1f;
        private float _nextFireMeltTrail;
        // Fires burn for the raid: their melt stamps effectively never refill
        private const float FireMeltForever = 100000f;
        private float _nextFireMeltStamp;

        /// <summary>
        /// With GPU prints, a fire's melt is a smooth round stamp in the print texture, growing with the melt, instead of the 25 cm grid melt.
        /// </summary>
        private void UpdateGpuFireMelts()
        {
            // The texture grows the melt about ten times a second (GPU only), so it spreads smoothly
            float now = Time.time;
            if (_prints == null || _fireMelts.Count == 0 || now < _nextFireMeltTrail)
            {
                return;
            }

            _nextFireMeltTrail = now + FireMeltTrailSeconds;
            bool listStep = now >= _nextFireMeltStamp;
            if (listStep)
            {
                _nextFireMeltStamp = now + FireMeltStampSeconds;
            }

            float finalRadius = MeltRadius + MeltFeather * 0.6f;
            for (int i = 0; i < _fireMelts.Count; i++)
            {
                var melt = _fireMelts[i];
                var world = new Vector3(melt.Center.x + _origin.x, _origin.y, melt.Center.y + _origin.z);
                if (!melt.Refined)
                {
                    // A near-zero press over the final patch: refines its cells, draws nothing visible
                    StampMelt(world, finalRadius, 0.001f, FireMeltForever, false, melt.Phases);
                    melt.Refined = true;
                }

                float progress = Mathf.Clamp01((now - melt.Started) / MeltSeconds);
                if (melt.StampedProgress >= 1f)
                {
                    _fireMelts[i] = melt;
                    continue;
                }

                float radius = Mathf.Max(0.3f, MeltRadius * progress + MeltFeather * progress * 0.6f);
                if (listStep || progress >= 1f)
                {
                    StampMelt(world, radius, 0.5f, FireMeltForever, false, melt.Phases);
                    melt.StampedProgress = progress;
                }
                else
                {
                    PressMeltTrail(world, radius, 0.5f, FireMeltForever, melt.Phases);
                }

                _fireMelts[i] = melt;
            }
        }

        /// <summary>
        /// True when this chunk lies well inside the print window, where fires melt on the GPU.
        /// </summary>
        private bool FireMeltOnGpu(Chunk chunk)
        {
            if (_prints == null)
            {
                return false;
            }

            var center = new Vector2((chunk.X + ChunkWidth * 0.5f) * CellWidth + _origin.x,
                (chunk.Z + ChunkWidth * 0.5f) * CellWidth + _origin.z);
            return _prints.ContainsWell(center, ChunkSize + 4f);
        }

        /// <summary>
        /// After the window moves, chunks near fires rebuild so they switch between GPU and grid melt.
        /// </summary>
        private void RefreshChunksNearFires()
        {
            if (_fireMelts.Count == 0)
            {
                return;
            }

            float reach = MeltRadius + MeltFeather + ChunkSize;
            float now = Time.time;
            foreach (var chunk in _active)
            {
                var center = new Vector2((chunk.X + ChunkWidth * 0.5f) * CellWidth, (chunk.Z + ChunkWidth * 0.5f) * CellWidth);
                foreach (var melt in _fireMelts)
                {
                    if ((melt.Center - center).sqrMagnitude <= reach * reach)
                    {
                        chunk.FootprintRefresh = Mathf.Min(chunk.FootprintRefresh, now + 0.2f);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Clears snow around a fire.
        /// </summary>
        public void AddFire(Vector3 position, bool instant)
        {
            Vector3 local = position - _origin;
            var center = new Vector2(local.x, local.z);
            foreach (var melt in _fireMelts)
            {
                if ((melt.Center - center).sqrMagnitude <= SameFireDistance * SameFireDistance)
                {
                    return;
                }
            }

            _fireMelts.Add(new FireMelt
            {
                Center = center,
                Started = instant ? float.NegativeInfinity : Time.time,
                Phases = new Vector2(Random.Range(0f, 6.283f), Random.Range(0f, 6.283f))
            });

            float reach = MeltRadius + MeltFeather + CellWidth;
            int minX = Mathf.FloorToInt((center.x - reach) / ChunkSize);
            int maxX = Mathf.FloorToInt((center.x + reach) / ChunkSize);
            int minZ = Mathf.FloorToInt((center.y - reach) / ChunkSize);
            int maxZ = Mathf.FloorToInt((center.y + reach) / ChunkSize);
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var key = new Vector2Int(x, z);
                    if (_chunks.TryGetValue(key, out var chunk))
                    {
                        chunk.FootprintsDirty = true;
                        chunk.RaisedDirty = true;
                    }

                    MarkPreviewDirty(key);
                }
            }
        }

        /// <summary>
        /// 1 for full snow, 0 for bare ground beside a fire.
        /// </summary>
        private float FireMeltFactor(float x, float z)
        {
            if (_fireMelts.Count == 0)
            {
                return 1f;
            }

            float factor = 1f;
            float now = Time.time;
            var point = new Vector2(x, z);
            foreach (var melt in _fireMelts)
            {
                float distance = Vector2.Distance(point, melt.Center);
                if (distance >= MeltRadius + MeltFeather)
                {
                    continue;
                }

                float progress = Mathf.Clamp01((now - melt.Started) / MeltSeconds);
                float bare = MeltRadius * progress;
                float edge = Mathf.Max(0.05f, MeltFeather * progress);
                float value = Mathf.SmoothStep(0f, 1f, (distance - bare) / edge);
                if (progress < 1f)
                {
                    // Early in the melt the snow thins everywhere in range before the bare patch opens
                    value = Mathf.Min(1f, value + (1f - progress) * 0.6f);
                }

                factor = Mathf.Min(factor, value);
            }

            return factor;
        }

        /// <summary>
        /// True when a chunk is still melting, so it keeps rebuilding until settled.
        /// </summary>
        private bool IsChunkMelting(Chunk chunk)
        {
            if (_fireMelts.Count == 0)
            {
                return false;
            }

            float now = Time.time;
            float minX = chunk.X * CellWidth;
            float minZ = chunk.Z * CellWidth;
            foreach (var melt in _fireMelts)
            {
                if (now - melt.Started >= MeltSeconds)
                {
                    continue;
                }

                float dx = Mathf.Max(0f, Mathf.Max(minX - melt.Center.x, melt.Center.x - minX - ChunkSize));
                float dz = Mathf.Max(0f, Mathf.Max(minZ - melt.Center.y, melt.Center.y - minZ - ChunkSize));
                float reach = MeltRadius + MeltFeather;
                if (dx * dx + dz * dz < reach * reach)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
