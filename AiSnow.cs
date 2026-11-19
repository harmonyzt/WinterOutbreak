using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private const float AiStepInterval = 0.10f;
        private const float AiTrackRadius = 30f;
        private const float CorpseSampleInterval = 0.25f;
        private const float CorpseCoverRadius = 26f;
        private const int MaxCorpseCovers = 10;

        private readonly Dictionary<Player, AiStride> _aiStrides = new Dictionary<Player, AiStride>();
        private readonly List<Player> _inactiveAi = new List<Player>();
        private readonly List<AiSnowBody> _snowBodies = new List<AiSnowBody>();
        private Fixed<bool> _aiFootprints;
        private Fixed<bool> _aiBodyImpressions;
        private Fixed<bool> _aiCorpseCover;
        private Fixed<float> _corpseCoverSeconds;
        private float _nextAiStepSample;
        private float _nextCorpseSample;
        private Material _corpseSnowMaterial;
        private bool _triedCorpseSnowMaterial;

        private void BindAiSnowSettings()
        {
            _aiFootprints = new Fixed<bool>(true);
            _aiBodyImpressions = new Fixed<bool>(true);
            _aiCorpseCover = new Fixed<bool>(true);
            _corpseCoverSeconds = new Fixed<float>(60f);
        }

        private void UpdateAiSnow(Player player)
        {
            if (player == null || !_snowDepthEnabled.Value)
            {
                ClearAiSnow();
                return;
            }

            float now = Time.time;
            if (_voxelSnow != null && _aiFootprints.Value)
            {
                if (now >= _nextAiStepSample)
                {
                    _nextAiStepSample = now + AiStepInterval;
                    UpdateAiFootprints(player.Position, now);
                }
            }
            else
            {
                _aiStrides.Clear();
            }

            if (_snowBodies.Count > 0 && now >= _nextCorpseSample)
            {
                _nextCorpseSample = now + CorpseSampleInterval;
                UpdateAiBodies(player.Position, now);
            }
        }

        private void UpdateAiFootprints(Vector3 localPosition, float now)
        {
            // Bots, and in a Fika raid the other human players too
            foreach (Player bot in _raid.AllAlivePlayersList)
            {
                if (bot == null || bot.IsYourPlayer)
                {
                    continue;
                }

                Vector3 position = bot.Position;
                var movement = bot.MovementContext;
                if ((position - localPosition).sqrMagnitude > AiTrackRadius * AiTrackRadius
                    || movement == null || movement.IsInPronePose)
                {
                    continue;
                }

                if (!_aiStrides.TryGetValue(bot, out var stride))
                {
                    stride = new AiStride(position, now);
                    _aiStrides.Add(bot, stride);
                }

                if (stride.Sample(position, movement.IsGrounded, now, out var center, out var forward))
                {
                    Vector3 offset = Vector3.Cross(Vector3.up, forward) * (stride.LeftStep ? -0.115f : 0.115f);
                    float refill = _footprintRefillSeconds.Value;
                    if (!_voxelSnow.StampFootprint(center + offset, forward, refill, aiStep: true))
                    {
                        _voxelSnow.StampFootprint(center + offset * 0.5f, forward, refill, aiStep: true);
                    }
                }
            }

            foreach (var entry in _aiStrides)
            {
                if (entry.Value.LastSample != now)
                {
                    _inactiveAi.Add(entry.Key);
                }
            }

            foreach (Player bot in _inactiveAi)
            {
                _aiStrides.Remove(bot);
            }

            _inactiveAi.Clear();
        }

        private void UpdateAiBodies(Vector3 localPosition, float now)
        {
            int visibleCovers = 0;
            float nearestDistance = CorpseCoverRadius * CorpseCoverRadius;
            AiSnowBody nearestUncoated = null;
            int solids = LayersMaskController.HighPolyCollider | LayersMaskController.LowPolyColliderLayerMask;

            for (int i = _snowBodies.Count - 1; i >= 0; i--)
            {
                AiSnowBody body = _snowBodies[i];
                if (body.Corpse == null || body.Pelvis == null)
                {
                    body.RemoveCover();
                    _snowBodies.RemoveAt(i);
                    continue;
                }

                float distance = (body.Pelvis.position - localPosition).sqrMagnitude;
                if (distance > AiTrackRadius * AiTrackRadius || !_aiCorpseCover.Value)
                {
                    body.RemoveCover();
                }

                if (!body.UpdatePose(now) || distance > AiTrackRadius * AiTrackRadius)
                {
                    continue;
                }

                if (_aiBodyImpressions.Value && !body.HasImpression && _voxelSnow != null
                    && now >= body.NextImpressionAttempt)
                {
                    body.NextImpressionAttempt = now + 1f;
                    int floorMask = solids | LayersMaskController.TerrainMask;
                    if (TryFindBodyContact(body.Pelvis.position, floorMask, out var contact)
                        || TryFindBodyContact(body.TorsoPosition, floorMask, out contact))
                    {
                        Vector3 forward = Vector3.ProjectOnPlane(body.TorsoPosition - body.Pelvis.position, Vector3.up);
                        if (forward.sqrMagnitude < 0.01f)
                        {
                            forward = body.FallDirection;
                        }

                        body.HasImpression = _voxelSnow.StampBody(contact, forward.normalized,
                            Mathf.Max(90f, _footprintRefillSeconds.Value));
                    }
                }

                if (!_aiCorpseCover.Value || now < body.NextCoverAttempt)
                {
                    continue;
                }

                float cover = (now - body.SettledAt) * _preset.Snowfall / _corpseCoverSeconds.Value;
                if (body.Cover != null)
                {
                    visibleCovers++;
                    body.Cover.SetDepth(cover);
                }
                else if (cover >= 0.1f && distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestUncoated = body;
                }
            }

            if (nearestUncoated == null || visibleCovers >= MaxCorpseCovers)
            {
                return;
            }

            TryAddCorpseCover(nearestUncoated, now, solids);
        }

        private bool TryFindBodyContact(Vector3 bone, int floorMask, out Vector3 contact)
        {
            if (Physics.Raycast(bone + Vector3.up * 0.12f, Vector3.down, out var hit, 0.62f, floorMask,
                QueryTriggerInteraction.Ignore) && hit.normal.y >= 0.42f
                && _voxelSnow.TryGetDepth(hit.point, out float depth) && depth >= 0.025f)
            {
                contact = hit.point;
                return true;
            }

            contact = default;
            return false;
        }

        private void TryAddCorpseCover(AiSnowBody body, float now, int solids)
        {
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            Vector3 pelvis = body.Pelvis.position + Vector3.up * 0.45f;
            Vector3 torso = body.TorsoPosition + Vector3.up * 0.45f;
            if (Physics.Raycast(pelvis, Vector3.up, 30f, solids, triggers)
                && Physics.Raycast(torso, Vector3.up, 30f, solids, triggers))
            {
                body.NextCoverAttempt = now + 5f;
                return;
            }

            if (!_triedCorpseSnowMaterial)
            {
                _triedCorpseSnowMaterial = true;
                _corpseSnowMaterial = SnowSurface.CreateSnowMaterial();
                if (_corpseSnowMaterial == null)
                {
                    Logger.LogWarning("AI corpse snow could not find a supported shader.");
                }
            }

            if (_corpseSnowMaterial != null)
            {
                bool suppressIndoorSnow = IsInterchange(_raid?.LocationId) || IsInterchange(_raid?.MainPlayer?.Location);
                body.Cover = CorpseSnowCover.Create(body.Corpse, _corpseSnowMaterial, solids, suppressIndoorSnow);
                body.Cover?.SetDepth((now - body.SettledAt) * _preset.Snowfall / _corpseCoverSeconds.Value);
                AddCorpseCoat(body.Corpse, body.SettledAt);
            }

            body.NextCoverAttempt = body.Cover == null ? now + 10f : 0f;
        }

        private void ClearAiSnow()
        {
            _aiStrides.Clear();
            foreach (AiSnowBody body in _snowBodies)
            {
                body.RemoveCover();
            }

            _snowBodies.Clear();
            Destroy(_corpseSnowMaterial);
            _corpseSnowMaterial = null;
            _triedCorpseSnowMaterial = false;
            _nextAiStepSample = 0f;
            _nextCorpseSample = 0f;
        }

        // Player.OnDead stores CreateCorpse's result in Player.Corpse
        [HarmonyPatch(typeof(Player), nameof(Player.OnDead))]
        private static class AiSnowCorpsePatch
        {
            private static void Postfix(Player __instance, Corpse ___Corpse)
            {
                var plugin = _plugin;
                var corpse = ___Corpse;
                if (plugin == null || plugin._weather == null || !plugin._snowDepthEnabled.Value
                    || (!plugin._aiBodyImpressions.Value && !plugin._aiCorpseCover.Value)
                    || __instance.IsYourPlayer || corpse == null || corpse.TrackableTransform == null)
                {
                    return;
                }

                foreach (var body in plugin._snowBodies)
                {
                    if (ReferenceEquals(body.Corpse, corpse))
                    {
                        return;
                    }
                }

                var chest = __instance.PlayerBones?.Ribcage?.Original;
                var forward = __instance.Transform.forward;
                plugin._snowBodies.Add(new AiSnowBody(corpse, chest, forward, Time.time));
            }
        }

        private sealed class AiStride
        {
            private Vector3 _lastPosition;
            private float _distance;
            private float _lastGrounded = float.NegativeInfinity;

            public float LastSample { get; private set; }
            public bool LeftStep { get; private set; }

            public AiStride(Vector3 position, float now)
            {
                _lastPosition = position;
                LastSample = now;
            }

            public bool Sample(Vector3 position, bool grounded, float now, out Vector3 center, out Vector3 forward)
            {
                Vector3 previous = _lastPosition;
                float elapsed = now - LastSample;
                _lastPosition = position;
                LastSample = now;
                center = default;
                forward = default;

                if (now - _lastGrounded > 0.25f)
                {
                    _distance = 0f;
                }

                if (!grounded)
                {
                    return false;
                }

                _lastGrounded = now;
                Vector3 movement = Vector3.ProjectOnPlane(position - previous, Vector3.up);
                float distance = movement.magnitude;
                if (elapsed > 0.25f || distance > 1.25f || Mathf.Abs(position.y - previous.y) > 0.55f)
                {
                    _distance = 0f;
                    return false;
                }

                if (distance < 0.001f)
                {
                    return false;
                }

                float stride = Mathf.Lerp(0.47f, 0.82f, Mathf.InverseLerp(1f, 5f, distance / elapsed));
                _distance += distance;
                if (_distance < stride)
                {
                    return false;
                }

                center = Vector3.Lerp(position, previous, (_distance - stride) / distance);
                forward = movement / distance;
                _distance %= stride;
                LeftStep = !LeftStep;
                return true;
            }
        }

        private sealed class AiSnowBody
        {
            private readonly Transform _chest;
            private Vector3 _lastPelvis;
            private Vector3 _lastTorso;
            private float _stableSince;

            public readonly Corpse Corpse;
            public readonly Transform Pelvis;
            public readonly Vector3 FallDirection;
            public Vector3 TorsoPosition => _chest != null ? _chest.position : Pelvis.position;
            public float SettledAt { get; private set; } = -1f;
            public bool HasImpression;
            public float NextImpressionAttempt;
            public float NextCoverAttempt;
            public CorpseSnowCover Cover;

            public AiSnowBody(Corpse corpse, Transform chest, Vector3 fallDirection, float now)
            {
                Corpse = corpse;
                Pelvis = corpse.TrackableTransform;
                FallDirection = Vector3.ProjectOnPlane(fallDirection, Vector3.up);
                _chest = chest;
                _lastPelvis = Pelvis.position;
                _lastTorso = TorsoPosition;
                _stableSince = now;
            }

            public bool UpdatePose(float now)
            {
                Vector3 pelvis = Pelvis.position;
                Vector3 torso = TorsoPosition;
                if ((pelvis - _lastPelvis).sqrMagnitude > 0.04f * 0.04f
                    || (torso - _lastTorso).sqrMagnitude > 0.04f * 0.04f)
                {
                    _lastPelvis = pelvis;
                    _lastTorso = torso;
                    _stableSince = now;
                    SettledAt = -1f;
                    HasImpression = false;
                    NextImpressionAttempt = 0f;
                    NextCoverAttempt = 0f;
                    RemoveCover();
                }

                float settleDelay = Corpse.Ragdoll?._isPhysicsDone == true ? CorpseSampleInterval : 0.65f;
                if (now - _stableSince < settleDelay)
                {
                    return false;
                }

                if (SettledAt < 0f)
                {
                    SettledAt = now;
                }

                return true;
            }

            public void RemoveCover()
            {
                Cover?.Dispose();
                Cover = null;
            }
        }
    }
}
