using BepInEx.Logging;

namespace WinterOutbreak
{
    internal static class BarrelSettings
    {
        internal static ManualLogSource Log;
        internal static Fixed<float> HealRadius;
        internal static Fixed<float> DelayBeforeHealing;
        internal static Fixed<float> HealRatePerMinute;
        internal static Fixed<string> BarrelNamePattern;
        internal static Fixed<int> EnvironmentLayerMask;
        internal static Fixed<float> ScanRadius;

        internal static void Bind(ManualLogSource log)
        {
            Log = log;
            HealRadius = new Fixed<float>(3f);
            DelayBeforeHealing = new Fixed<float>(2f);
            HealRatePerMinute = new Fixed<float>(25f);
            BarrelNamePattern = new Fixed<string>("bonfire|brazier|barrel_fire");
            EnvironmentLayerMask = new Fixed<int>(0);
            ScanRadius = new Fixed<float>(8f);
        }
    }
}
