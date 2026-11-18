using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace WinterOutbreak
{
    /// <summary>
    /// Fika: every player builds the frozen water from the map itself, so with the same settings everyone gets the same
    /// ice, walls and open areas. The host's settings decide (a headless host's too): they travel with the shared
    /// weather, and a client waits for them before it freezes anything. Settings that only change how the ice looks on
    /// one screen and the host-only bot navmesh stay local.
    /// </summary>
    public sealed partial class WinterOutbreakPlugin
    {
        private sealed class IceSettings
        {
            public bool Enabled;
            public string Maps;
            public string Surfaces;
            public bool Swamp;
            public bool River;
            public string NoWalkLine;
            public bool Sea;
            public float SeaWidth;
            public string SeaOpenZones;
        }

        private IceSettings _hostIce;
        private bool _hostIceKnown;
        private float _hostIceWaitStarted = -1f;

        // The settings the freeze uses: the host's once a Fika client has them, otherwise this game's own.
        private IceSettings Ice => _hostIce ?? LocalIceSettings();

        private IceSettings LocalIceSettings()
        {
            return new IceSettings
            {
                Enabled = _frozenLakeEnabled.Value,
                Maps = _frozenLakeMaps.Value ?? "",
                Surfaces = _frozenLakeSurfaces.Value ?? "",
                Swamp = _frozenSwamp.Value,
                River = _frozenRiver.Value,
                NoWalkLine = _frozenLakeNoWalkLine.Value ?? "",
                Sea = _frozenSea.Value,
                SeaWidth = _seaIceWidth.Value,
                SeaOpenZones = _seaOpenZones.Value ?? ""
            };
        }

        // One "key=value" per line; the values themselves use ',', ';' and '|' but never a line break.
        private string IceSettingsText()
        {
            IceSettings ice = LocalIceSettings();
            var text = new StringBuilder();
            text.Append("Enabled=").Append(ice.Enabled).Append('\n');
            text.Append("Maps=").Append(OneLine(ice.Maps)).Append('\n');
            text.Append("Surfaces=").Append(OneLine(ice.Surfaces)).Append('\n');
            text.Append("Swamp=").Append(ice.Swamp).Append('\n');
            text.Append("River=").Append(ice.River).Append('\n');
            text.Append("NoWalkLine=").Append(OneLine(ice.NoWalkLine)).Append('\n');
            text.Append("Sea=").Append(ice.Sea).Append('\n');
            text.Append("SeaWidth=").Append(ice.SeaWidth.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("SeaOpenZones=").Append(OneLine(ice.SeaOpenZones));
            return text.ToString();
        }

        private static string OneLine(string value)
        {
            return (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
        }

        private static IceSettings ParseIceSettings(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in text.Split('\n'))
            {
                int split = line.IndexOf('=');
                if (split > 0)
                {
                    values[line.Substring(0, split)] = line.Substring(split + 1);
                }
            }

            if (!values.ContainsKey("Enabled"))
            {
                return null;
            }

            return new IceSettings
            {
                Enabled = ReadBool(values, "Enabled"),
                Maps = Read(values, "Maps"),
                Surfaces = Read(values, "Surfaces"),
                Swamp = ReadBool(values, "Swamp"),
                River = ReadBool(values, "River"),
                NoWalkLine = Read(values, "NoWalkLine"),
                Sea = ReadBool(values, "Sea"),
                SeaWidth = float.TryParse(Read(values, "SeaWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out float width) ? width : 22f,
                SeaOpenZones = Read(values, "SeaOpenZones")
            };
        }

        private static string Read(Dictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out string value) ? value : "";
        }

        private static bool ReadBool(Dictionary<string, string> values, string key)
        {
            return bool.TryParse(Read(values, key), out bool value) && value;
        }

        // Called with every weather message from the host. An older host sends no ice settings; then this game's own
        // are used straight away instead of waiting out the timeout.
        private void ReceiveHostIce(string text)
        {
            if (_hostIceKnown)
            {
                return;
            }

            _hostIceKnown = true;
            _hostIce = ParseIceSettings(text);
        }

        // A Fika client holds the freeze until the host's settings arrive, for at most the weather timeout.
        private bool WaitingForHostIce()
        {
            if (!CoopSupport.IsClient || _hostIceKnown)
            {
                return false;
            }

            if (_hostIceWaitStarted < 0f)
            {
                _hostIceWaitStarted = Time.time;
            }

            if (Time.time - _hostIceWaitStarted < HostWeatherTimeout)
            {
                return true;
            }

            _hostIceKnown = true;
            Logger.LogWarning("The Fika host sent no frozen water settings in time; freezing with this game's own.");
            return false;
        }

        private void ClearCoopIce()
        {
            _hostIce = null;
            _hostIceKnown = false;
            _hostIceWaitStarted = -1f;
        }
    }
}
