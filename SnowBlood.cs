using System.Collections.Generic;
using System.Reflection;
using DeferredDecals;
using HarmonyLib;
using UnityEngine;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        [HarmonyPatch]
        private static class SnowBloodPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var renderer = typeof(DeferredDecalRenderer);
                var arguments = new[] { typeof(Vector3), typeof(Vector3) };
                yield return AccessTools.DeclaredMethod(renderer,
                    nameof(DeferredDecalRenderer.EmitBloodOnEnvironment), arguments);
                yield return AccessTools.DeclaredMethod(renderer,
                    nameof(DeferredDecalRenderer.EmitBleeding), arguments);
            }

            private static void Prefix(ref Vector3 __0, Vector3 __1)
            {
                if (__1.y >= 0.55f)
                {
                    _plugin?._voxelSnow?.MoveBloodToSnowSurface(ref __0);
                }
            }
        }
    }

    internal sealed partial class VoxelSnowArea
    {
        internal void MoveBloodToSnowSurface(ref Vector3 position)
        {
            Vector3 local = position - _origin;
            if (!TryGetSnowHeights(local, out float ground, out float top)
                || Mathf.Abs(local.y - ground) > 0.075f || top <= local.y)
            {
                return;
            }

            position.y = _origin.y + top;
        }
    }
}
