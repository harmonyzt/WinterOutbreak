using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using EFT.Interactive;
using UnityEngine;

namespace WinterOutbreak
{
    internal static class BarrelDiscovery
    {
        private const float SamePlaceTolerance = 0.5f;

        // Reused between scans
        private const int MaxColliders = 512;
        private static readonly Collider[] Buffer = new Collider[MaxColliders];

        private static bool _warnedSaturated;

        internal static void Reset()
        {
            _warnedSaturated = false;
            Array.Clear(Buffer, 0, Buffer.Length);
        }
        
        internal static void ScanNear(Vector3 origin, List<Vector3> lit)
        {
            if (!TryPattern(BarrelSettings.BarrelNamePattern.Value, out Regex healPattern))
            {
                return;
            }

            var radius = BarrelSettings.ScanRadius.Value;
            var count = Physics.OverlapSphereNonAlloc(origin, radius, Buffer, ~0, QueryTriggerInteraction.Collide);

            if (count >= MaxColliders && !_warnedSaturated)
            {
                _warnedSaturated = true;
                BarrelSettings.Log.LogWarning(
                    $"[BarrelHealing] scan buffer full ({MaxColliders}) - a fire could be missed here. "
                    + "Lower the scan radius if this recurs.");
            }

            var claimed = new HashSet<Transform>();

            for (var i = 0; i < count; i++)
            {
                var collider = Buffer[i];
                Buffer[i] = null;

                if (collider == null)
                {
                    continue;
                }

                // Some props keep the heat trigger and the visible flames under separate objects.
                var flame = collider.GetComponentInParent<FlameDamageTrigger>();
                if (flame != null && flame.isActiveAndEnabled)
                {
                    AddFire(collider.bounds.center, flame.transform, lit);
                }

                Consider(collider.transform, healPattern, claimed, lit);
            }
        }

        private static void Consider(Transform transform, Regex healPattern, HashSet<Transform> claimed,
            List<Vector3> lit)
        {
            var match = TransformNameMatch.MatchingAncestor(transform, healPattern);
            if (match == null || !claimed.Add(match))
            {
                return;
            }

            // Lit or cold is decided by whether there is a fire under it, not by the name.
            var flame = match.GetComponentInChildren<FlameDamageTrigger>();
            var particles = match.GetComponentInChildren<ParticleSystem>(true);
            if ((flame == null || !flame.isActiveAndEnabled) && particles == null)
            {
                return;
            }

            var source = flame != null && flame.isActiveAndEnabled ? flame.transform : particles.transform;
            AddFire(source.position, match, lit);
        }

        private static void AddFire(Vector3 position, Transform match, List<Vector3> lit)
        {
            foreach (var seen in lit)
            {
                if (Vector3.Distance(seen, position) <= SamePlaceTolerance)
                {
                    return;
                }
            }

            lit.Add(position);
        }

        // Compiled once and reused
        private static readonly Dictionary<string, Regex> PatternCache = new Dictionary<string, Regex>();

        private static bool TryPattern(string source, out Regex pattern)
        {
            if (PatternCache.TryGetValue(source, out pattern))
            {
                return pattern != null;
            }

            try
            {
                pattern = new Regex(source, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            catch (System.ArgumentException ex)
            {
                BarrelSettings.Log.LogError("[BarrelHealing] name pattern will not compile: " + ex.Message);
                pattern = null;
            }

            PatternCache[source] = pattern;
            return pattern != null;
        }
    }
}
