using System;
using System.Diagnostics;
using UnityEngine;

namespace WinterOutbreak
{
    // The snow area's per-frame entry point and its sampling and rebuild wrappers
    internal sealed partial class VoxelSnowArea
    {
        private const float NearbyRefillSeconds = 1.5f;
        private const float DistantRefillSeconds = 3f;

        private const double PreviewShare = 0.25;
        private const double SamplingShare = 0.55;
        private const double RaisedShare = 0.70;
        private const double DistantRebuildShare = 0.50;
        private const int MaxPublishBacklog = 2;
        private const int MaxRebuildWaitFrames = 2;
        private const int MaxDistantWaitFrames = 5;
        private long _frameStart;
        private long _frameTicks;
        private bool _frameUnlimited;
        private int _rebuildWaitFrames;
        private int _distantWaitFrames;

        internal float LastDepth { get; private set; }

        // Milliseconds of snow work allowed per raid frame
        internal float FrameBudgetMs { get; set; } = 1.5f;

        // Set while the GPU far snow (FarSnow.cs) draws beyond the detailed chunks
        internal bool FarSnowActive { get; set; }

        private static readonly (int X, int Z)[] FourSides = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        private Column SampleColumn(Vector3 position)
        {
            return SampleColumnCore(position);
        }

        public void Update(Vector3 playerPosition, float depth, bool fullyAccumulated, bool preparing)
        {
            LastDepth = depth;
            UpdateStreaming(playerPosition, depth, fullyAccumulated, preparing);
        }

        private void StartFrameBudget(bool preparing)
        {
            _frameStart = Stopwatch.GetTimestamp();
            _frameUnlimited = preparing;
            _frameTicks = (long)(Stopwatch.Frequency * Math.Max(0.25f, FrameBudgetMs) / 1000.0);
        }

        // The moment this frame's share of the budget runs out (share 1 is the whole budget)
        private long FrameShare(double share)
        {
            if (_frameUnlimited)
            {
                return long.MaxValue;
            }

            return _frameStart + (long)(_frameTicks * share);
        }

        private bool HasFrameTime(double share = 1.0)
        {
            return Stopwatch.GetTimestamp() < FrameShare(share);
        }

        /// <summary>
        /// Prepares a chunk's surface in slices before it is rebuilt, and returns true once it can be rebuilt now.
        /// </summary>
        private bool PrepareBeforeRebuild(Chunk chunk, bool wholeBudget = false)
        {
            if (chunk.SurfacePrepared || chunk.Columns == null)
            {
                return true;
            }

            // Footprints near the player get a whole budget's slice so the print is refined within a frame or two
            long slice = wholeBudget ? _frameTicks : _frameTicks / 4;
            long deadline = Math.Max(FrameShare(1.0), Stopwatch.GetTimestamp() + slice);
            PrepareSurfaceSlice(chunk, deadline);
            return false;
        }

        private void RebuildChunk(Chunk chunk, float depth, string reason)
        {
            RebuildChunkCore(chunk, depth);
        }

        private RaisedSnowPatch TimedRaisedSurface(Vector3 ground, bool hasGroundMound)
        {
            return SampleRaisedSurface(ground, hasGroundMound);
        }
    }
}
