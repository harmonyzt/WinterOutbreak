using System;
using UnityEngine;

namespace WinterOutbreak
{
    internal sealed class AnimatedSnowFoot
    {
        private readonly Transform _ankle;
        private readonly Transform _toe;
        private readonly Vector3 _localForward;
        private Vector3 _lastSample;
        private Vector3 _lastPlant;
        private Vector3 _lastDirection;
        private float _lastSampleTime;
        private float _nextPlant;
        private bool _hasPlant;
        private bool _lifted;

        private AnimatedSnowFoot(Transform ankle, Vector3 forward)
        {
            _ankle = ankle;
            _localForward = Quaternion.Inverse(ankle.rotation) * forward;
            foreach (var child in ankle.GetComponentsInChildren<Transform>())
            {
                if (child != ankle && child.name.IndexOf("toe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _toe = child;
                    break;
                }
            }
        }

        public static AnimatedSnowFoot Create(Transform ankle, Vector3 forward)
        {
            return ankle != null ? new AnimatedSnowFoot(ankle, forward) : null;
        }

        public void Lift()
        {
            _lifted = true;
        }

        public float SoleHeight => _ankle != null ? _ankle.position.y : float.PositiveInfinity;

        /// <summary>
        /// Plants this foot because EFT just played a footstep for it.
        /// </summary>
        public bool TryPlantOnStep(float now, int floorMask, out Vector3 point, out Vector3 forward)
        {
            point = default;
            if (!SampleSole(out Vector3 sole, out forward, out float plantHeight)
                || !Physics.Raycast(sole + Vector3.up * 0.05f, Vector3.down, out var floor, 0.40f, floorMask, QueryTriggerInteraction.Ignore) || floor.normal.y < 0.48f
                || sole.y - floor.point.y > plantHeight + 0.07f)
            {
                return false;
            }

            point = floor.point;
            bool moved = (point - _lastPlant).sqrMagnitude > 0.105f * 0.105f;
            bool turned = Vector3.Dot(forward, _lastDirection) < 0.91f;
            if (_hasPlant && !moved && !turned)
            {
                return false;
            }

            _hasPlant = true;
            _lifted = false;
            _lastPlant = point;
            _lastDirection = forward;
            _nextPlant = now + 0.12f;
            return true;
        }

        private bool SampleSole(out Vector3 sole, out Vector3 forward, out float plantHeight)
        {
            sole = default;
            forward = default;
            plantHeight = 0.16f;
            if (_ankle == null)
            {
                return false;
            }

            Vector3 ankle = _ankle.position;
            sole = ankle;
            forward = _ankle.rotation * _localForward;
            if (_toe != null)
            {
                Vector3 toe = _toe.position;
                Vector3 boot = toe - ankle;
                if (boot.x * boot.x + boot.z * boot.z > 0.0025f)
                {
                    forward = boot;
                    sole = Vector3.Lerp(ankle, toe, 0.45f);
                    plantHeight = 0.115f;
                }
            }

            forward.y = 0f;
            forward.Normalize();
            if (_toe == null)
            {
                sole += forward * 0.05f;
            }

            return forward.sqrMagnitude >= 0.5f;
        }

        public bool TryPlant(float now, int floorMask, out Vector3 point, out Vector3 forward)
        {
            point = default;
            forward = default;
            if (_ankle == null)
            {
                return false;
            }

            Vector3 ankle = _ankle.position;
            Vector3 sole = ankle;
            float plantHeight = 0.16f;
            forward = _ankle.rotation * _localForward;

            if (_toe != null)
            {
                Vector3 toe = _toe.position;
                Vector3 boot = toe - ankle;
                if (boot.x * boot.x + boot.z * boot.z > 0.0025f)
                {
                    forward = boot;
                    sole = Vector3.Lerp(ankle, toe, 0.45f);
                    plantHeight = 0.115f;
                }
            }

            forward.y = 0f;
            forward.Normalize();
            if (_toe == null)
            {
                sole += forward * 0.05f;
            }

            Vector3 travel = sole - _lastSample;
            travel.y = 0f;
            float elapsed = now - _lastSampleTime;
            _lastSample = sole;
            _lastSampleTime = now;

            if (!Physics.Raycast(sole + Vector3.up * 0.05f, Vector3.down, out var floor, 0.35f, floorMask, QueryTriggerInteraction.Ignore) || floor.normal.y < 0.48f
                || sole.y - floor.point.y > plantHeight + 0.045f)
            {
                Lift();
                return false;
            }

            if (sole.y - floor.point.y > plantHeight || forward.sqrMagnitude < 0.5f || now < _nextPlant)
            {
                return false;
            }

            point = floor.point;
            bool settled = travel.sqrMagnitude <= 1.1f * 1.1f * elapsed * elapsed;

            return RecordContact(point, forward, settled, now);
        }

        private bool RecordContact(Vector3 point, Vector3 forward, bool settled, float now)
        {
            Vector3 difference = point - _lastPlant;
            bool moved = difference.sqrMagnitude > 0.105f * 0.105f;
            bool turned = Vector3.Dot(forward, _lastDirection) < 0.91f;
            if (_hasPlant && !_lifted && (!(moved || turned) || !settled))
            {
                return false;
            }

            bool stamp = _hasPlant || _lifted;
            _hasPlant = true;
            _lifted = false;
            _lastPlant = point;
            _lastDirection = forward;
            _nextPlant = now + 0.12f;

            return stamp;
        }
    }
}
