using UnityEngine;

namespace WinterOutbreak
{
    // Edge wrapping
    internal sealed partial class VoxelSnowArea
    {
        private const int EdgeSearchSteps = 4;
        private const float EdgeShiftLimit = 0.11f;
        private const float EdgeHeightStep = 0.08f;
        private const byte EdgeEastResolved = 1, EdgeNorthResolved = 2;
        private const int EdgeModeHeight = 1, EdgeModeCover = 2;

        internal long EdgeSearches;
        internal long EdgeFound;
        internal long EdgeKept;
        internal long EdgeSearchTicks;
        internal long EdgeSearchCalls;

        // Where the ground changes between cell (x, z) and its neighbour (x + dx, z + dz)
        private float GetEdgeCrossing(int x, int z, int dx, int dz)
        {
            if (dx < 0 || dz < 0)
            {
                float back = GetEdgeCrossing(x + dx, z + dz, -dx, -dz);
                return float.IsNaN(back) ? back : 1f - back;
            }

            Chunk owner = GetChunk(x, z);
            if (owner?.Columns == null)
            {
                return float.NaN;
            }

            int index = owner.ColumnIndex(x, z);
            byte bit = dx > 0 ? EdgeEastResolved : EdgeNorthResolved;
            Column cached = owner.Columns[index];
            if ((cached.EdgeResolved & bit) != 0)
            {
                return dx > 0 ? cached.EdgeEast : cached.EdgeNorth;
            }

            // An edge beside a cell that is not sampled yet is not cached: it is searched once both sides exist.
            if (!cached.Sampled || !GetColumn(x + dx, z + dz).Sampled)
            {
                return float.NaN;
            }

            long searchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            float t = SearchEdge(x, z, x + dx, z + dz);
            EdgeSearchTicks += System.Diagnostics.Stopwatch.GetTimestamp() - searchStarted;
            EdgeSearchCalls++;
            ref Column column = ref owner.Columns[index];
            if (dx > 0)
            {
                column.EdgeEast = t;
            }
            else
            {
                column.EdgeNorth = t;
            }

            column.EdgeResolved |= bit;
            return t;
        }

        private struct EdgeSide
        {
            public bool Floor;
            public float Y;
            public bool Covered;
        }

        private EdgeSide SideOf(Column column)
        {
            return new EdgeSide
            {
                Floor = column.HasGround,
                Y = _origin.y + column.GroundHeight,
                Covered = column.HasGround && column.Sheltered
            };
        }

        private float SearchEdge(int ax, int az, int bx, int bz)
        {
            Column a = GetColumn(ax, az);
            Column b = GetColumn(bx, bz);
            if (!a.Sampled || !b.Sampled)
            {
                return float.NaN;
            }

            EdgeSide sa = SideOf(a);
            EdgeSide sb = SideOf(b);
            int mode;
            if (!sa.Floor && !sb.Floor)
            {
                return float.NaN;
            }
            else if (!sa.Floor || !sb.Floor || Mathf.Abs(sa.Y - sb.Y) >= EdgeHeightStep)
            {
                mode = EdgeModeHeight;
            }
            else if (sa.Covered != sb.Covered)
            {
                mode = EdgeModeCover;
            }
            else if (a.Sheltered && b.Sheltered)
            {
                // Doorway drift ends where the smooth drift field falls below the minimum depth. Interpolate that point
                // between the two cells instead of ending the drift on the cell border.
                float da = GetDriftField(ax, az), db = GetDriftField(bx, bz);
                if ((da >= MinimumDrift) == (db >= MinimumDrift) || Mathf.Approximately(da, db))
                {
                    return float.NaN;
                }

                EdgeSearches++;
                EdgeFound++;
                return Mathf.Clamp((da - MinimumDrift) / (da - db), 0.02f, 0.98f);
            }
            else
            {
                // No physical difference: the boundary comes from shaping, so it stays on the grid.
                return float.NaN;
            }

            EdgeSearches++;
            Vector3 pa = GroundPoint(ax, az, 0f);
            Vector3 pb = GroundPoint(bx, bz, 0f);
            float lo = 0f, hi = 1f;
            for (int i = 0; i < EdgeSearchSteps; i++)
            {
                float mid = (lo + hi) * 0.5f;
                int side = ClassifyEdgePoint(Vector3.Lerp(pa, pb, mid), mode, sa, sb);
                if (side == 0)
                {
                    EdgeKept++;
                    return float.NaN;
                }

                if (side == 1)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            if (lo == 0f || hi == 1f)
            {
                EdgeKept++;
                return float.NaN;
            }

            EdgeFound++;
            return (lo + hi) * 0.5f;
        }

        // 1 when the point behaves like cell A, 2 like cell B, 0 when it matches neither.
        //
        // Sampling finds where the grid's own decision changes.
        private int ClassifyEdgePoint(Vector3 point, int mode, EdgeSide sa, EdgeSide sb)
        {
            Column sample = SampleColumnCore(point);
            if (!sample.HasGround)
            {
                if (sa.Floor == sb.Floor)
                {
                    return 0;
                }

                return sa.Floor ? 2 : 1;
            }

            float y = _origin.y + sample.GroundHeight;
            if (mode == EdgeModeHeight)
            {
                if (!sa.Floor || !sb.Floor)
                {
                    EdgeSide floor = sa.Floor ? sa : sb;
                    bool onFloor = Mathf.Abs(y - floor.Y) <= 0.30f;
                    return onFloor == sa.Floor ? 1 : 2;
                }

                float da = Mathf.Abs(y - sa.Y);
                float db = Mathf.Abs(y - sb.Y);
                if (Mathf.Min(da, db) > Mathf.Max(0.25f, 0.5f * Mathf.Abs(sa.Y - sb.Y)))
                {
                    return 0;
                }

                return da <= db ? 1 : 2;
            }

            if (Mathf.Abs(y - sa.Y) > 0.30f)
            {
                return 0;
            }

            return sample.Sheltered == sa.Covered ? 1 : 2;
        }

        // Sideways offset for the vertex at mesh corner (cx, cz) used by the cells in groupMask, where bit 0 is cell
        // (cx-1, cz-1), bit 1 (cx, cz-1), bit 2 (cx-1, cz) and bit 3 (cx, cz).
        private Vector2 EdgeShift(int cx, int cz, int groupMask)
        {
            if (groupMask == 0 || groupMask == 15)
            {
                return Vector2.zero;
            }

            // Edges of shaped cells stay on the grid
            for (int bit = 0; bit < 4; bit++)
            {
                if ((groupMask & (1 << bit)) != 0
                    && GetColumn(cx - 1 + (bit & 1), cz - 1 + (bit >> 1)).ShapeKind != ShapeNone)
                {
                    return Vector2.zero;
                }
            }

            Vector2 sumX = Vector2.zero, sumZ = Vector2.zero;
            int countX = 0, countZ = 0;
            AddBorder(groupMask, 0, 1, cx - 1, cz - 1, 1, 0, ref sumX, ref countX);
            AddBorder(groupMask, 2, 3, cx - 1, cz, 1, 0, ref sumX, ref countX);
            AddBorder(groupMask, 0, 2, cx - 1, cz - 1, 0, 1, ref sumZ, ref countZ);
            AddBorder(groupMask, 1, 3, cx, cz - 1, 0, 1, ref sumZ, ref countZ);

            Vector2 corner = new Vector2(cx, cz);
            Vector2 offset;
            if (countX > 0 && countZ > 0)
            {
                offset = (sumX + sumZ) / (countX + countZ) - corner;
            }
            else if (countX > 0)
            {
                offset = new Vector2(sumX.x / countX - cx, 0f);
            }
            else if (countZ > 0)
            {
                offset = new Vector2(0f, sumZ.y / countZ - cz);
            }
            else
            {
                return Vector2.zero;
            }

            return new Vector2(Mathf.Clamp(offset.x * CellWidth, -EdgeShiftLimit, EdgeShiftLimit),
                Mathf.Clamp(offset.y * CellWidth, -EdgeShiftLimit, EdgeShiftLimit));
        }

        private void AddBorder(int groupMask, int first, int second, int fromX, int fromZ, int dx, int dz,
            ref Vector2 sum, ref int count)
        {
            bool inFirst = (groupMask & (1 << first)) != 0;
            bool inSecond = (groupMask & (1 << second)) != 0;
            if (inFirst == inSecond)
            {
                return;
            }

            float t = GetEdgeCrossing(fromX, fromZ, dx, dz);
            if (float.IsNaN(t))
            {
                return;
            }

            // Cell units: a cell's centre is at index + 0.5.
            sum += new Vector2(fromX + 0.5f + t * dx, fromZ + 0.5f + t * dz);
            count++;
        }
    }
}
