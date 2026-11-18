using UnityEngine;

namespace WinterOutbreak
{
    // Doorway drift as one smooth field.
    internal sealed partial class VoxelSnowArea
    {
        private const int DriftFieldCells = 9;
        private const float DriftFieldReach = DriftFieldCells;
        private const int DriftRegionWidth = ChunkWidth + 2 * DriftFieldCells;

        // A doorway threshold may step up or down this much
        private const float DriftLevelRise = 0.35f;
        private const float DriftSpreadRise = 0.10f;
        private const float DriftWallProbe = 0.40f;

        private readonly float[] _driftDistance = new float[DriftRegionWidth * DriftRegionWidth];
        private readonly float[] _driftLevel = new float[DriftRegionWidth * DriftRegionWidth];
        private readonly byte[] _driftKind = new byte[DriftRegionWidth * DriftRegionWidth];
        private readonly int[] _driftHeap = new int[DriftRegionWidth * DriftRegionWidth * 8];
        private int _driftHeapCount;

        private const byte DriftNone = 0, DriftOpen = 1, DriftSheltered = 2;

        private static readonly int[] DriftStepX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] DriftStepZ = { 0, 0, 1, -1, 1, -1, 1, -1 };
        private static readonly float[] DriftStepCost = { 1f, 1f, 1f, 1f, 1.4142f, 1.4142f, 1.4142f, 1.4142f };

        private float GetDriftField(int x, int z)
        {
            Chunk owner = GetChunk(x, z);
            if (owner?.Columns == null)
            {
                return 0f;
            }

            int index = owner.ColumnIndex(x, z);
            if (!owner.Columns[index].DriftFieldResolved)
            {
                ComputeDriftField(owner);
            }

            return owner.Columns[index].DriftField;
        }

        private void ComputeDriftField(Chunk chunk)
        {
            int startX = chunk.X - DriftFieldCells;
            int startZ = chunk.Z - DriftFieldCells;
            const int width = DriftRegionWidth;

            bool complete = true;
            for (int j = 0; j < width; j++)
            {
                for (int i = 0; i < width; i++)
                {
                    int r = j * width + i;
                    Column column = GetColumn(startX + i, startZ + j);
                    _driftDistance[r] = float.PositiveInfinity;
                    _driftLevel[r] = column.GroundHeight;
                    complete &= column.Sampled;
                    if (!column.Sampled || !column.HasGround || column.Normal.y < 0.65f)
                    {
                        _driftKind[r] = DriftNone;
                        continue;
                    }

                    // The level is the ground the snow is drawn on
                    if (TryGetBaseGround(startX + i, startZ + j, out float level))
                    {
                        _driftLevel[r] = level;
                    }

                    if (column.Sheltered)
                    {
                        _driftKind[r] = DriftSheltered;
                    }
                    else
                    {
                        _driftKind[r] = column.Exposed ? DriftOpen : DriftNone;
                    }
                }
            }

            // sheltered floor next to open floor on the same level, nothing solid between them.
            _driftHeapCount = 0;
            for (int j = 0; j < width; j++)
            {
                for (int i = 0; i < width; i++)
                {
                    int r = j * width + i;
                    if (_driftKind[r] != DriftSheltered)
                    {
                        continue;
                    }

                    float best = float.PositiveInfinity;
                    for (int s = 0; s < 8; s++)
                    {
                        int ni = i + DriftStepX[s], nj = j + DriftStepZ[s];
                        if (ni < 0 || nj < 0 || ni >= width || nj >= width)
                        {
                            continue;
                        }

                        int n = nj * width + ni;
                        float half = DriftStepCost[s] * 0.5f;
                        if (_driftKind[n] != DriftOpen || half >= best
                            || Mathf.Abs(_driftLevel[n] - _driftLevel[r]) > DriftLevelRise)
                        {
                            continue;
                        }

                        Vector3 from = GroundPoint(startX + i, startZ + j, _driftLevel[r] + DriftWallProbe);
                        Vector3 to = GroundPoint(startX + ni, startZ + nj, _driftLevel[n] + DriftWallProbe);
                        
                        // Doors snow does not blow in under a closed one
                        if (!Physics.Linecast(from, to, _doorMask, QueryTriggerInteraction.Ignore))
                        {
                            best = half;
                        }
                    }

                    if (best < float.PositiveInfinity)
                    {
                        _driftDistance[r] = best;
                        HeapPush(r);
                    }
                }
            }
            
            while (_driftHeapCount > 0)
            {
                int r = HeapPop();
                float d = _driftDistance[r];
                if (d >= DriftFieldReach)
                {
                    continue;
                }

                int i = r % width, j = r / width;
                for (int s = 0; s < 8; s++)
                {
                    int ni = i + DriftStepX[s], nj = j + DriftStepZ[s];
                    if (ni < 0 || nj < 0 || ni >= width || nj >= width)
                    {
                        continue;
                    }

                    int n = nj * width + ni;
                    if (_driftKind[n] != DriftSheltered || Mathf.Abs(_driftLevel[n] - _driftLevel[r]) > DriftSpreadRise)
                    {
                        continue;
                    }

                    // drift cannot slip through a corner.
                    if (s >= 4)
                    {
                        int a = j * width + ni, b = nj * width + i;
                        if (_driftKind[a] != DriftSheltered && _driftKind[b] != DriftSheltered)
                        {
                            continue;
                        }
                    }

                    float candidate = d + DriftStepCost[s];
                    if (candidate < _driftDistance[n])
                    {
                        _driftDistance[n] = candidate;
                        HeapPush(n);
                    }
                }
            }

            chunk.DriftComplete = complete;
            
            for (int z = 0; z < ChunkWidth; z++)
            {
                for (int x = 0; x < ChunkWidth; x++)
                {
                    int r = (z + DriftFieldCells) * width + x + DriftFieldCells;
                    float d = _driftDistance[r];
                    float drift = d < DriftFieldReach && HasSnowAround(r, width)
                        ? MaxSmoothDrift * (1f - Mathf.SmoothStep(0f, 1f, d / DriftFieldReach))
                        : 0f;
                    ref Column column = ref chunk.Columns[chunk.ColumnIndex(chunk.X + x, chunk.Z + z)];
                    
                    column.DriftField = drift < 0.01f ? 0f : drift;
                    column.DriftFieldResolved = true;
                }
            }
        }

        // Whether a drift cell has enough snow around it to keep.
        private bool HasSnowAround(int r, int width)
        {
            int i = r % width;
            int j = r / width;
            int reached = 0;
            int open = 0;
            for (int s = 0; s < 8; s++)
            {
                int ni = i + DriftStepX[s];
                int nj = j + DriftStepZ[s];
                if (ni < 0 || nj < 0 || ni >= width || nj >= width)
                {
                    continue;
                }

                int n = nj * width + ni;
                if (_driftDistance[n] < DriftFieldReach)
                {
                    reached++;
                }
                else if (_driftKind[n] == DriftOpen && Mathf.Abs(_driftLevel[n] - _driftLevel[r]) <= DriftSpreadRise)
                {
                    open++;
                }
            }

            return reached >= 3 || reached + open >= 5;
        }

        private void RefreshDriftNear(Vector2Int key)
        {
            for (int kz = key.y - 1; kz <= key.y + 1; kz++)
            {
                for (int kx = key.x - 1; kx <= key.x + 1; kx++)
                {
                    if (!_chunks.TryGetValue(new Vector2Int(kx, kz), out var chunk) || chunk.Columns == null
                        || chunk.DriftComplete)
                    {
                        continue;
                    }

                    for (int i = 0; i < chunk.Columns.Length; i++)
                    {
                        chunk.Columns[i].DriftFieldResolved = false;
                    }

                    // Corners read drift; prepare them again on the next rebuild.
                    chunk.SurfacePrepared = false;
                }
            }
        }

        private void HeapPush(int r)
        {
            if (_driftHeapCount >= _driftHeap.Length)
            {
                return;
            }

            int i = _driftHeapCount++;
            _driftHeap[i] = r;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_driftDistance[_driftHeap[parent]] <= _driftDistance[_driftHeap[i]])
                {
                    break;
                }

                (_driftHeap[parent], _driftHeap[i]) = (_driftHeap[i], _driftHeap[parent]);
                i = parent;
            }
        }

        private int HeapPop()
        {
            int top = _driftHeap[0];
            _driftHeap[0] = _driftHeap[--_driftHeapCount];
            int i = 0;
            while (true)
            {
                int left = 2 * i + 1, right = left + 1, smallest = i;
                if (left < _driftHeapCount && _driftDistance[_driftHeap[left]] < _driftDistance[_driftHeap[smallest]])
                {
                    smallest = left;
                }

                if (right < _driftHeapCount && _driftDistance[_driftHeap[right]] < _driftDistance[_driftHeap[smallest]])
                {
                    smallest = right;
                }

                if (smallest == i)
                {
                    break;
                }

                (_driftHeap[smallest], _driftHeap[i]) = (_driftHeap[i], _driftHeap[smallest]);
                i = smallest;
            }

            return top;
        }
    }
}
