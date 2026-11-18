using UnityEngine;

namespace WinterOutbreak
{
    // The snow area's per-frame entry point and its sampling and rebuild wrappers. Development builds also count
    // timings here for the raid summary.
    internal sealed partial class VoxelSnowArea
    {
        private const float NearbyRefillSeconds = 1.5f;
        private const float DistantRefillSeconds = 3f;
        private RebuildPhases _rebuildPhases;

        internal float LastDepth { get; private set; }

        private static readonly (int X, int Z)[] FourSides = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        private struct RebuildPhases
        {
            public long Footprints;
            public long Corners;
            public long Surface;
            public long Raised;
            public long Upload;
            public int GroundVertices;
            public int TotalVertices;
        }

        private Column SampleColumn(Vector3 position)
        {
            return SampleColumnCore(position);
        }

        public void Update(Vector3 playerPosition, float depth, bool fullyAccumulated, bool preparing)
        {
            LastDepth = depth;
            UpdateStreaming(playerPosition, depth, fullyAccumulated, preparing);
        }

        private void RebuildChunk(Chunk chunk, float depth, string reason)
        {
            _rebuildPhases = default;
            RebuildChunkCore(chunk, depth);
        }

        private RaisedSnowPatch TimedRaisedSurface(Vector3 ground, bool hasGroundMound)
        {
            return SampleRaisedSurface(ground, hasGroundMound);
        }
    }
}
