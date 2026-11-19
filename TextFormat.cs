using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace WinterOutbreak
{
    // Small text helpers for log messages and saved files
    public sealed partial class WinterOutbreakPlugin
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static string F(float value) => value.ToString("0.###", Inv);

        private static string PathOf(Transform transform)
        {
            if (transform == null)
            {
                return "null";
            }

            var parts = new List<string>();
            for (var t = transform; t != null && parts.Count < 8; t = t.parent)
            {
                parts.Add(t.name);
            }

            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
