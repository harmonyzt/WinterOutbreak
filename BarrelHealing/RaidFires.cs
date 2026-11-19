using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace WinterOutbreak
{
    internal sealed class RaidFires
    {
        private readonly List<Vector3> _lit = new List<Vector3>();
        private float _nextScan;

        /// <summary>
        /// Called once for each newly discovered lit fire.
        /// </summary>
        internal Action<Vector3> FireFound;

        internal IReadOnlyList<Vector3> Lit => _lit;

        internal bool IsNearFire(Player player, float now)
        {
            if (now >= _nextScan)
            {
                _nextScan = now + 1f;
                int known = _lit.Count;
                BarrelDiscovery.ScanNear(player.Position, _lit);
                for (int i = known; i < _lit.Count; i++)
                {
                    FireFound?.Invoke(_lit[i]);
                }
            }

            Vector3 chest = player.Position + Vector3.up;
            int mask = BarrelSettings.EnvironmentLayerMask.Value;
            if (mask == 0)
            {
                mask = LayersMaskController.HighPolyWithTerrainMask;
            }

            foreach (Vector3 fire in _lit)
            {
                if (Vector3.Distance(player.Position, fire) <= BarrelSettings.HealRadius.Value
                    && (!Physics.Linecast(chest, fire, out RaycastHit hit, mask, QueryTriggerInteraction.Ignore)
                        || hit.distance >= Vector3.Distance(chest, fire) - 1f))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Adds a fire the player built.
        /// </summary>
        internal void AddPlaced(Vector3 position)
        {
            foreach (Vector3 fire in _lit)
            {
                if ((fire - position).sqrMagnitude <= 0.5f * 0.5f)
                {
                    return;
                }
            }

            _lit.Add(position);
            FireFound?.Invoke(position);
        }

        internal void Clear()
        {
            _lit.Clear();
            _nextScan = 0f;
            BarrelDiscovery.Reset();
        }
    }
}
