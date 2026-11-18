using System.Collections.Generic;
using UnityEngine;

namespace WinterOutbreak
{
    /// <summary>
    /// Approximate airflow around buildings
    /// </summary>
    internal sealed class WindFlow
    {
        private const float Cell = 3f;
        private const float Lookahead = 12f;
        private const float ShelterReach = 10f;
        private static readonly float[] TurnAngles = { 25f, -25f, 50f, -50f, 75f, -75f };

        private readonly Dictionary<long, Entry> _cache = new Dictionary<long, Entry>();
        private readonly int _mask;

        private struct Entry
        {
            public Vector3 Prevailing;
            public Vector3 Direction;
            public float Speed;
            public float Shelter;
            public float Expires;
        }

        public WindFlow(int mask)
        {
            _mask = mask;
        }
        
        public void Sample(Vector3 ground, StormWind wind, out Vector3 direction, out float speed, out float shelter)
        {
            long key = ((long)Mathf.FloorToInt(ground.x / Cell) << 32) ^ (uint)Mathf.FloorToInt(ground.z / Cell);
            float now = Time.time;
            if (!_cache.TryGetValue(key, out var entry) || entry.Expires < now
                || Vector3.Dot(entry.Prevailing, wind.Prevailing) < 0.97f)
            {
                entry = Compute(ground, wind);
                entry.Expires = now + wind.Range(4f, 7f);
                if (_cache.Count > 6000)
                {
                    _cache.Clear();
                }

                _cache[key] = entry;
            }

            // Keep the current veer on top of the cached, geometry-aware direction.
            float veer = Vector3.SignedAngle(wind.Prevailing, wind.Direction, Vector3.up);
            direction = Quaternion.AngleAxis(veer, Vector3.up) * entry.Direction;
            
            speed = entry.Speed;
            shelter = entry.Shelter;
        }

        private Entry Compute(Vector3 ground, StormWind wind)
        {
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            Vector3 origin = ground + Vector3.up * 0.8f;
            Vector3 prevailing = wind.Prevailing;
            var entry = new Entry { Prevailing = prevailing, Direction = prevailing, Speed = 1f };

            if (Physics.Raycast(origin, -prevailing, out var upwind, ShelterReach, _mask, triggers)
                && upwind.normal.y < 0.6f)
            {
                entry.Shelter = 1f - upwind.distance / ShelterReach;
            }

            if (!Physics.Raycast(origin, prevailing, out var ahead, Lookahead, _mask, triggers) || ahead.normal.y > 0.6f)
            {
                // Open ahead. Walls close on both sides squeeze the flow and speed it up.
                Vector3 side = Vector3.Cross(Vector3.up, prevailing);
                bool left = Physics.Raycast(origin, -side, 6f, _mask, triggers);
                bool right = Physics.Raycast(origin, side, 6f, _mask, triggers);
                entry.Speed = left && right ? 1.25f : 1f;
                return entry;
            }

            float best = ahead.distance;
            Vector3 bestDirection = prevailing;
            float bestAngle = 0f;
            foreach (float angle in TurnAngles)
            {
                Vector3 candidate = Quaternion.AngleAxis(angle, Vector3.up) * prevailing;
                float clear = Physics.Raycast(origin, candidate, out var hit, Lookahead, _mask, triggers) && hit.normal.y < 0.6f
                    ? hit.distance
                    : Lookahead;

                // Prefer smaller turns when the room is similar.
                if (clear > best + 1.5f)
                {
                    best = clear;
                    bestDirection = candidate;
                    bestAngle = Mathf.Abs(angle);
                }

                if (clear >= Lookahead)
                {
                    break;
                }
            }

            if (best < 3f)
            {
                // Boxed in: slide along the wall in front.
                Vector3 slide = Vector3.ProjectOnPlane(prevailing, ahead.normal);
                slide.y = 0f;
                entry.Direction = slide.sqrMagnitude > 0.01f ? slide.normalized : prevailing;
                entry.Speed = 0.4f;
                return entry;
            }

            entry.Direction = bestDirection;
            entry.Speed = Mathf.Lerp(0.55f, 1f, best / Lookahead) * Mathf.Lerp(1f, 0.75f, bestAngle / 75f);
            return entry;
        }

        public void Clear()
        {
            _cache.Clear();
        }
    }
    
    internal sealed class ParticleSteering
    {
        private readonly int _mask;
        private ParticleSystem.Particle[] _buffer;
        private int _cursor;

        public ParticleSteering(int mask)
        {
            _mask = mask;
        }
        
        public void Steer(ParticleSystem system, int checks, float lookahead, float rise)
        {
            if (system == null || system.particleCount == 0)
            {
                return;
            }

            int capacity = system.main.maxParticles;
            if (_buffer == null || _buffer.Length < capacity)
            {
                _buffer = new ParticleSystem.Particle[capacity];
            }

            int count = system.GetParticles(_buffer);
            bool changed = false;
            for (int n = 0; n < checks && n < count; n++)
            {
                if (_cursor >= count)
                {
                    _cursor = 0;
                }

                int index = _cursor++;
                Vector3 velocity = _buffer[index].velocity;
                float speed = velocity.magnitude;
                if (speed < 0.3f)
                {
                    continue;
                }

                Vector3 heading = velocity / speed;
                if (!Physics.Raycast(_buffer[index].position, heading, out var hit, speed * lookahead + 0.3f, _mask,
                    QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                Vector3 turned;
                if (hit.normal.y > 0.6f)
                {
                    // Ground or a roof ahead, skim over it
                    turned = velocity;
                    turned.y = Mathf.Max(turned.y, 0.05f * speed);
                }
                else
                {
                    turned = Vector3.ProjectOnPlane(velocity, hit.normal);
                    if (turned.sqrMagnitude < 0.04f * speed * speed)
                    {
                        // pick a side along the wall
                        turned = Vector3.Cross(hit.normal, Vector3.up) * (index % 2 == 0 ? 1f : -1f) * speed;
                    }

                    turned = turned.normalized * speed * 0.8f + Vector3.up * speed * rise;
                }

                _buffer[index].velocity = turned;
                changed = true;
            }

            if (changed)
            {
                system.SetParticles(_buffer, count);
            }
        }
    }
}
