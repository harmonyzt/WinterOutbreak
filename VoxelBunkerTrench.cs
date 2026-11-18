using System.Collections.Generic;
using UnityEngine;

namespace WinterOutbreak
{
    // Woods bunker stairwells and trenches keep the game's own winter shading instead of voxel snow.
    internal sealed partial class VoxelSnowArea
    {
        private const string BunkerName = "bunker_forest_02";
        private const string BunkerShellName = "bunker_forest_02_indoor";
        private const int TrenchNeighbours = 4;

        private readonly Dictionary<Collider, byte> _bunkerColliders = new Dictionary<Collider, byte>();
        private Collider _firstSolidHit;
        private Collider _firstRejectedHit;

        private const byte NotBunker = 0, BunkerOuter = 1, BunkerShell = 2;

        private bool IsBunkerPart(Collider collider)
        {
            return BunkerKind(collider) != NotBunker;
        }

        private bool IsBunkerShell(Collider collider)
        {
            return BunkerKind(collider) == BunkerShell;
        }

        private byte BunkerKind(Collider collider)
        {
            if (collider == null)
            {
                return NotBunker;
            }

            if (!_bunkerColliders.TryGetValue(collider, out byte kind))
            {
                string name = collider.name;
                string parent = collider.transform.parent != null ? collider.transform.parent.name : "";
                if (ContainsIgnoreCase(name, BunkerShellName) || ContainsIgnoreCase(parent, BunkerShellName))
                {
                    kind = BunkerShell;
                }
                else if (ContainsIgnoreCase(name, BunkerName) || ContainsIgnoreCase(parent, BunkerName))
                {
                    kind = BunkerOuter;
                }
                else
                {
                    kind = NotBunker;
                }

                _bunkerColliders.Add(collider, kind);
            }

            return kind;
        }

        // Only cells whose search met a bunker pay for the neighbour count.
        private bool IsTrenchHidden(int x, int z, Column column)
        {
            if (!column.BunkerPart)
            {
                return false;
            }

            if (column.BunkerTrench)
            {
                return true;
            }

            int trench = 0;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx != 0 || dz != 0) && GetColumn(x + dx, z + dz).BunkerTrench)
                    {
                        trench++;
                    }
                }
            }

            return trench >= TrenchNeighbours;
        }

        private bool IsTrenchHidden(int x, int z)
        {
            Column column = GetColumn(x, z);
            return column.Sampled && !column.Sheltered && IsTrenchHidden(x, z, column);
        }
    }
}
