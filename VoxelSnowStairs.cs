using UnityEngine;

namespace WinterOutbreak
{
    // Stair builder.
    //
    // Cells next to each other read mostly the same treads, so their planes agree.
    internal sealed partial class VoxelSnowArea
    {
        internal const byte ShapeStairProbe = 6;
        private const int StairProbeHalf = 8;
        private const float StairProbeStep = 0.08f;
        private const float StairProbeMinSlope = 0.25f;
        private const float StairProbeMaxSlope = 1.0f;

        internal long StairProbeCells;
        internal long StairProbeAccepted;

        private readonly float[] _probeS = new float[2 * StairProbeHalf + 1];
        private readonly float[] _probeH = new float[2 * StairProbeHalf + 1];

        private static bool IsLoosePile(Collider collider)
        {
            string name = collider.name;
            string parent = collider.transform.parent != null ? collider.transform.parent.name : "";
            return ContainsIgnoreCase(name, "Pile") || ContainsIgnoreCase(parent, "Pile")
                || ContainsIgnoreCase(name, "timber") || ContainsIgnoreCase(parent, "timber")
                || ContainsIgnoreCase(name, "Berth") || ContainsIgnoreCase(parent, "Berth");
        }

        private static bool ContainsIgnoreCase(string text, string part)
        {
            return text.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool TryProbeStairs(int x, int z, float own, out float ground, out Vector3 normal)
        {
            ground = own;
            normal = Vector3.up;

            // 1. A coarse plane through the base ground around the cell says whether this could be a staircase.
            double n = 0, sx = 0, sz = 0, sh = 0, sxx = 0, szz = 0, sxz = 0, sxh = 0, szh = 0;
            float low = float.PositiveInfinity, high = float.NegativeInfinity;
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
                    low = Mathf.Min(low, h);
                    high = Mathf.Max(high, h);
                    if (_levelCount < _probeLevels.Length)
                    {
                        _probeLevels[_levelCount++] = h;
                    }
                }
            }

            int levels = CountLevels(out float biggestStep);
            _levelCount = 0;
            if (n < 12 || high - low < 0.25f || !(levels >= 3 || (biggestStep >= 0.50f && biggestStep <= 2.50f)))
            {
                return false;
            }

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
            if (slope < StairProbeMinSlope || slope > 2.5f)
            {
                return false;
            }

            // The coarse slope of a staircase sampled mostly at street level overstates the real one.
            slope = Mathf.Min(slope, StairProbeMaxSlope);

            StairProbeCells++;

            var uphill = new Vector2((float)b, (float)c).normalized;
            float baseY = _origin.y + own + (float)a;
            Vector3 center = GroundPoint(x, z, own + (float)a);
            int mask = _solidMask | _fallbackGroundMask;
            int count = 0;
            int ramps = 0;
            float top = _origin.y + own + high + 0.60f;
            for (int i = -StairProbeHalf; i <= StairProbeHalf; i++)
            {
                float s = i * StairProbeStep;
                float expected = baseY + slope * s;
                var from = new Vector3(center.x + uphill.x * s, top, center.z + uphill.y * s);
                if (!Physics.Raycast(from, Vector3.down, out var hit, top - (_origin.y + own + low) + 0.60f, mask,
                        QueryTriggerInteraction.Ignore)
                    || IsGroundObstruction(hit.collider) || (hit.rigidbody != null && !hit.rigidbody.isKinematic))
                {
                    continue;
                }

                // Stacked boards and logs are not stairs; probing them shaped the snow on the sawmill's woodpile into
                // steps.
                if (IsLoosePile(hit.collider))
                {
                    ground = own;
                    normal = Vector3.up;
                    return false;
                }

                // EFT stairs often carry a smooth walking ramp on the low-poly layer. That ramp is exactly the surface deep snow should follow.
                bool walkRamp = hit.normal.y >= 0.55f && hit.normal.y < 0.95f
                    && (_fallbackGroundMask & (1 << hit.collider.gameObject.layer)) != 0;
                if (!walkRamp && hit.normal.y < 0.90f)
                {
                    continue;
                }

                // A hit far below the coarse slope fell through a gap between treads.
                if (hit.point.y < expected - 0.60f)
                {
                    continue;
                }

                _probeS[count] = s;
                _probeH[count] = hit.point.y;
                ramps += walkRamp ? 1 : 0;
                count++;
            }

            bool onRamp = ramps * 10 >= count * 6;
            if (count < 8 || !SpansBothSides(count) || !(onRamp || HasFlatTreads(count)))
            {
                return false;
            }

            // 3. Fit the nosing line: least squares, drop samples well below it (rear of a tread under a nosing, a
            //    missing collider strip), fit again, then lift the line onto the tread edges.
            if (!FitLine(count, out float p, out float q))
            {
                return false;
            }

            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                float residual = _probeH[i] - (p + q * _probeS[i]);
                if (residual >= -0.10f && residual <= 0.20f)
                {
                    _probeS[kept] = _probeS[i];
                    _probeH[kept] = _probeH[i];
                    kept++;
                }
            }

            if (kept < 7 || !FitLine(kept, out p, out q) || q < StairProbeMinSlope || q > StairProbeMaxSlope)
            {
                return false;
            }

            float lift = 0f;
            for (int i = 0; i < kept; i++)
            {
                lift = Mathf.Max(lift, _probeH[i] - (p + q * _probeS[i]));
            }

            // A walking ramp already lies on the nosings; treads need lifting onto their edges.
            float surface = p + (onRamp ? 0f : Mathf.Min(lift, 0.12f)) - _origin.y;
            if (Mathf.Abs(surface - own) > 1.0f)
            {
                return false;
            }

            ground = surface;
            normal = new Vector3(-q * uphill.x, 1f, -q * uphill.y).normalized;
            StairProbeAccepted++;
            return true;
        }

        private readonly float[] _probeLevels = new float[(2 * StairReach + 1) * (2 * StairReach + 1)];
        private int _levelCount;

        // Distinct height levels among the collected neighbour heights (6 cm clusters), and the biggest gap between
        // neighbouring levels. A curb or a single low step gives two levels and a small gap.
        private int CountLevels(out float biggestStep)
        {
            System.Array.Sort(_probeLevels, 0, _levelCount);
            int levels = _levelCount > 0 ? 1 : 0;
            biggestStep = 0f;
            for (int i = 1; i < _levelCount; i++)
            {
                float gap = _probeLevels[i] - _probeLevels[i - 1];
                if (gap > 0.06f)
                {
                    levels++;
                    biggestStep = Mathf.Max(biggestStep, gap);
                }
            }

            return levels;
        }

        private bool SpansBothSides(int count)
        {
            bool before = false, after = false;
            for (int i = 0; i < count; i++)
            {
                before |= _probeS[i] <= -0.30f;
                after |= _probeS[i] >= 0.30f;
            }

            return before && after;
        }

        // Stairs have flat treads
        private bool HasFlatTreads(int count)
        {
            int runs = 0;
            int best = 0;
            float lastRunHeight = float.NaN;
            int start = 0;
            for (int i = 1; i <= count; i++)
            {
                bool continues = i < count && Mathf.Abs(_probeH[i] - _probeH[i - 1]) <= 0.015f
                    && _probeS[i] - _probeS[i - 1] <= StairProbeStep * 1.5f;
                if (continues)
                {
                    continue;
                }

                if (i - start >= 3)
                {
                    float height = _probeH[start];
                    float rise = float.IsNaN(lastRunHeight) ? 0f : Mathf.Abs(height - lastRunHeight);
                    if (float.IsNaN(lastRunHeight))
                    {
                        runs = 1;
                        lastRunHeight = height;
                    }
                    else if (rise >= 0.08f && rise <= 0.26f)
                    {
                        runs++;
                        lastRunHeight = height;
                    }
                    else if (rise > 0.26f)
                    {
                        // A ledge taller than a riser starts a new count
                        runs = 1;
                        lastRunHeight = height;
                    }

                    best = Mathf.Max(best, runs);
                }

                start = i;
            }

            return best >= 3;
        }

        private bool FitLine(int count, out float p, out float q)
        {
            double n = count, s = 0, h = 0, ss = 0, sh = 0;
            for (int i = 0; i < count; i++)
            {
                s += _probeS[i];
                h += _probeH[i];
                ss += _probeS[i] * _probeS[i];
                sh += _probeS[i] * _probeH[i];
            }

            double det = n * ss - s * s;
            if (det < 1e-6)
            {
                p = q = 0f;
                return false;
            }

            q = (float)((n * sh - s * h) / det);
            p = (float)((h - q * s) / n);
            return true;
        }
    }
}
