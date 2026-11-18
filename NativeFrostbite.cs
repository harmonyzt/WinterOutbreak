using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        [HarmonyPatch(typeof(ActiveHealthController.Effect), "NextState")]
        private static class NativeFrostbitePatch
        {
            private static bool Prefix(ActiveHealthController.Effect __instance)
            {
                if (_plugin == null || !(__instance is ActiveHealthController.Frostbite)
                    || __instance.State != EEffectState.None)
                {
                    return true;
                }

                // Keep the effect out of the local effect list
                var player = Singleton<GameWorld>.Instance?.MainPlayer;
                return player == null || !ReferenceEquals(player.ActiveHealthController, __instance.HealthController);
            }
        }
    }
}
