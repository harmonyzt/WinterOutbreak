using EFT;
using EFT.Weather;
using UnityEngine;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private const float GroundContactGrace = 0.18f;
        private static readonly int FootFloorMask = LayersMaskController.HighPolyCollider
            | LayersMaskController.LowPolyColliderLayerMask | LayersMaskController.TerrainMask;
        private Fixed<bool> _footprintsEnabled;
        private Fixed<float> _footprintRefillSeconds;
        private Fixed<int> _maxFootprintsPerChunk;
        private Fixed<bool> _trackSnowIndoors;
        private TrackedBootSnow _trackedBootSnow;
        private float _bootSnowUntil;
        private int _bootSnowSteps;
        private Player _snowInteractionPlayer;
        private AnimatedSnowFoot _leftSnowFoot;
        private AnimatedSnowFoot _rightSnowFoot;
        private bool _snowFeetResolved;
        private Vector3 _lastFootRoot;
        private float _lastGroundedStepTime;
        private float _nextProneSample;
        private float _nextProneRefresh;
        private Vector3 _lastProneCenter;
        private Vector3 _lastProneDirection;
        private Vector3 _lastTrailCenter;
        private Vector3 _lastTrailDirection;
        private float _lastTrailTime;
        private float _proneSince = -1f;
        private const float ProneSettleSeconds = 0.7f;

        private void BindSnowInteractionSettings()
        {
            _footprintsEnabled = new Fixed<bool>(true);
            _footprintRefillSeconds = new Fixed<float>(45f);
            _maxFootprintsPerChunk = new Fixed<int>(32);
            // Boot prints in the snow stay
            _trackSnowIndoors = new Fixed<bool>(false);
        }

        private void UpdateSnowInteraction(Player player, WeatherController weather)
        {
            _trackedBootSnow?.Update(Time.time);
            Player activePlayer = _voxelSnow != null ? player : null;
            if (_snowInteractionPlayer != activePlayer)
            {
                ResetSnowPose();
                _snowInteractionPlayer = activePlayer;
            }
        }

        private void UpdateSnowPose(Player player)
        {
            if (player.HealthController?.IsAlive != true || player.MovementContext == null || _voxelSnow == null)
            {
                ResetSnowPose();
                return;
            }

            if (!_footprintsEnabled.Value)
            {
                _leftSnowFoot = null;
                _rightSnowFoot = null;
                _snowFeetResolved = false;
                _nextProneRefresh = 0f;
                return;
            }

            float now = Time.time;
            if (!player.MovementContext.IsGrounded)
            {
                if (now - _lastGroundedStepTime > GroundContactGrace)
                {
                    _leftSnowFoot?.Lift();
                    _rightSnowFoot?.Lift();
                }

                _nextProneRefresh = 0f;
                return;
            }

            _lastGroundedStepTime = now;
            if (player.MovementContext.IsInPronePose)
            {
                _leftSnowFoot = null;
                _rightSnowFoot = null;
                _snowFeetResolved = false;
                UpdateProneCompression(player);
                return;
            }

            _nextProneRefresh = 0f;
            _proneSince = -1f;
            Vector3 root = player.Position;
            if (!_snowFeetResolved || (root - _lastFootRoot).sqrMagnitude > 1.25f * 1.25f)
            {
                var references = player.GetComponent<EFT.AssetsManager.PlayerPoolObject>()?.FullBodyBipedIk?.references;
                _leftSnowFoot = AnimatedSnowFoot.Create(references?.leftFoot, player.Transform.forward);
                _rightSnowFoot = AnimatedSnowFoot.Create(references?.rightFoot, player.Transform.forward);
                _snowFeetResolved = true;
            }

            _lastFootRoot = root;
            bool outdoors = player.Environment == EnvironmentType.Outdoor;
            UpdateBootPrint(_leftSnowFoot, outdoors, now);
            UpdateBootPrint(_rightSnowFoot, outdoors, now);
        }

        private void UpdateBootPrint(AnimatedSnowFoot boot, bool outdoors, float now)
        {
            if (boot == null || !boot.TryPlant(now, FootFloorMask, out var foot, out var forward))
            {
                return;
            }

            bool compressed = _voxelSnow.StampFootprint(foot, forward, _footprintRefillSeconds.Value);
            UpdateTrackedSnow(foot, forward, outdoors, compressed);
        }

        /// <summary>
        /// Called as EFT plays a walk, run or sprint footstep for a player.
        /// </summary>
        internal void OnSnowStepSound(Player player)
        {
            if (!ReferenceEquals(player, _snowInteractionPlayer) || _voxelSnow == null || !_footprintsEnabled.Value
                || player.MovementContext == null || player.MovementContext.IsInPronePose)
            {
                return;
            }

            AnimatedSnowFoot boot = _leftSnowFoot == null ? _rightSnowFoot
                : _rightSnowFoot == null ? _leftSnowFoot
                : _leftSnowFoot.SoleHeight <= _rightSnowFoot.SoleHeight ? _leftSnowFoot : _rightSnowFoot;
            if (boot == null || !boot.TryPlantOnStep(Time.time, FootFloorMask, out var foot, out var forward))
            {
                return;
            }

            bool compressed = _voxelSnow.StampFootprint(foot, forward, _footprintRefillSeconds.Value);
            UpdateTrackedSnow(foot, forward, player.Environment == EnvironmentType.Outdoor, compressed);
        }

        private void UpdateTrackedSnow(Vector3 foot, Vector3 forward, bool outdoors, bool compressed)
        {
            if (!_trackSnowIndoors.Value || _voxelSnow.SuppressIndoorSnow)
            {
                _bootSnowSteps = 0;
                return;
            }

            float now = Time.time;
            if (outdoors)
            {
                if (compressed)
                {
                    _bootSnowSteps = 4;
                    _bootSnowUntil = now + 7f;
                }

                return;
            }

            if (_bootSnowSteps <= 0 || now >= _bootSnowUntil)
            {
                return;
            }

            float strength = _bootSnowSteps-- * 0.25f;
            if (!compressed)
            {
                _trackedBootSnow ??= new TrackedBootSnow();
                _trackedBootSnow.LeavePrint(foot, forward, strength, now);
            }
        }

        private void UpdateProneCompression(Player player)
        {
            float now = Time.time;
            bool trail = _voxelSnow.GpuPrints;
            if (now < _nextProneSample && !trail)
            {
                return;
            }

            Vector3 root = player.Position;
            var bones = player.PlayerBones;
            Vector3 center = bones?.Pelvis?.position ?? root;
            Vector3 direction = player.LookDirection;
            if (bones?.Ribcage != null && bones.Pelvis != null)
            {
                Vector3 torso = bones.Ribcage.position - center;
                torso.y = 0f;
                if (torso.sqrMagnitude > 0.04f)
                {
                    direction = torso;
                }
            }

            center.y = root.y;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f)
            {
                return;
            }

            direction.Normalize();
            float refill = _footprintRefillSeconds.Value;
            if (_proneSince < 0f)
            {
                _proneSince = now;
            }

            // The body sinks in over a moment as you lie down, instead of the snow dropping 26 cm at once
            float settle = Mathf.SmoothStep(0f, 1f, (now - _proneSince) / ProneSettleSeconds);
            if (trail && (settle < 1f || now - _lastTrailTime > 3f
                || (center - _lastTrailCenter).sqrMagnitude > 0.05f * 0.05f
                || Vector3.Dot(direction, _lastTrailDirection) < 0.996f))
            {
                // Every 5 cm or 5 degrees (and every frame while settling, and every 3 s lying still, so the furrow
                // does not refill under you)
                _voxelSnow.PressProneTrail(center, direction, refill, settle);
                _lastTrailCenter = center;
                _lastTrailDirection = direction;
                _lastTrailTime = now;
            }

            if (now < _nextProneSample)
            {
                return;
            }

            _nextProneSample = now + 0.25f;
            bool moved = (center - _lastProneCenter).sqrMagnitude > 0.40f * 0.40f;
            // Turns refine the mesh sooner with GPU prints, so the furrow has fine cells to press as it swings
            bool turned = Vector3.Dot(direction, _lastProneDirection) < (trail ? 0.95f : 0.86f);
            if (now < _nextProneRefresh && !moved && !turned)
            {
                return;
            }

            if (_voxelSnow.StampProne(center, direction, refill))
            {
                _lastProneCenter = center;
                _lastProneDirection = direction;
                _nextProneRefresh = now + Mathf.Min(6f, refill * 0.25f);
            }
        }

        private void ClearSnowInteraction()
        {
            _trackedBootSnow?.Dispose();
            _trackedBootSnow = null;
            _bootSnowSteps = 0;
            _bootSnowUntil = 0f;
            _snowInteractionPlayer = null;
            ResetSnowPose();
        }

        private void ResetSnowPose()
        {
            _leftSnowFoot = null;
            _rightSnowFoot = null;
            _snowFeetResolved = false;
            _lastGroundedStepTime = 0f;
            _nextProneSample = 0f;
            _nextProneRefresh = 0f;
            _proneSince = -1f;
        }

        [HarmonyLib.HarmonyPatch(typeof(Player), nameof(Player.LateUpdate))]
        private static class SnowPosePatch
        {
            private static void Postfix(Player __instance)
            {
                var plugin = _plugin;
                if (plugin != null && ReferenceEquals(__instance, plugin._snowInteractionPlayer))
                {
                    plugin.UpdateSnowPose(__instance);
                }
            }
        }
    }
}
