using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace WinterOutbreak
{
    // Sections carry a number so the Configuration Manager lists them in this order, and each setting gets a position
    // inside its section
    public sealed partial class WinterOutbreakPlugin
    {
        private const string StormsSection = "1. Storms";
        private const string SurvivalSection = "2. Survival";
        private const string AiVisionSection = "3. AI Vision";
        private const string PerformanceSection = "4. Performance";
        private const string SnowSection = "5. Snow";
        private const string BlowingSnowSection = "6. Blowing Snow";
        private const string AuroraSection = "7. Aurora";

        private Dictionary<ConfigDefinition, string> _orphanedSettings;

        private ConfigEntry<T> BindSetting<T>(string section, string key, T value, string text, int position,
            bool advanced = false, AcceptableValueBase range = null, string oldSection = null, string oldKey = null)
        {
            if (oldSection != null)
            {
                KeepOldValue(oldSection, oldKey ?? key, section, key);
            }

            // The Configuration Manager shows higher Order first, so position 1 becomes the top entry
            var layout = new ConfigurationManagerAttributes { Order = -position, IsAdvanced = advanced };
            return Config.Bind(section, key, value, new ConfigDescription(text, range, layout));
        }

        // Settings that moved to a new section or name keep the player's old value
        private void KeepOldValue(string oldSection, string oldKey, string section, string key)
        {
            if (_orphanedSettings == null)
            {
                _orphanedSettings = Traverse.Create(Config).Property("OrphanedEntries")
                    .GetValue<Dictionary<ConfigDefinition, string>>() ?? new Dictionary<ConfigDefinition, string>();
            }

            var oldDefinition = new ConfigDefinition(oldSection, oldKey);
            var newDefinition = new ConfigDefinition(section, key);

            if (_orphanedSettings.TryGetValue(oldDefinition, out string oldValue))
            {
                if (!_orphanedSettings.ContainsKey(newDefinition))
                {
                    _orphanedSettings[newDefinition] = oldValue;
                }

                _orphanedSettings.Remove(oldDefinition);
            }
        }

        // Our settings are bound feature by feature, so put them back in section order once all are bound
        private void SortSettingsBySection()
        {
            var entries = Traverse.Create(Config).Property("Entries")
                .GetValue<Dictionary<ConfigDefinition, ConfigEntryBase>>();
            if (entries == null)
            {
                return;
            }

            var sorted = new List<KeyValuePair<ConfigDefinition, ConfigEntryBase>>(entries);
            var byBinding = new Dictionary<ConfigDefinition, int>();
            for (int i = 0; i < sorted.Count; i++)
            {
                byBinding[sorted[i].Key] = i;
            }

            sorted.Sort((a, b) =>
            {
                int section = string.CompareOrdinal(a.Key.Section, b.Key.Section);
                return section != 0 ? section : byBinding[a.Key].CompareTo(byBinding[b.Key]);
            });

            entries.Clear();
            foreach (var entry in sorted)
            {
                entries.Add(entry.Key, entry.Value);
            }
        }

        private sealed class ConfigurationManagerAttributes
        {
            public int? Order;
            public bool? IsAdvanced;
        }
    }
}
