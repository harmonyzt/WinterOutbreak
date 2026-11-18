using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace WinterOutbreak
{
    // Measured stair flights kept per map between raids
    internal sealed partial class VoxelSnowArea
    {
        private const float StairTileSize = ChunkWidth * CellWidth;

        private readonly HashSet<long> _cachedStairTiles = new HashSet<long>();
        private readonly HashSet<long> _scannedStairTiles = new HashSet<long>();

        private long StairTileOf(Chunk chunk)
        {
            float x = _origin.x + (chunk.X + ChunkWidth * 0.5f) * CellWidth;
            float z = _origin.z + (chunk.Z + ChunkWidth * 0.5f) * CellWidth;
            return CellKey(Mathf.FloorToInt(x / StairTileSize), Mathf.FloorToInt(z / StairTileSize));
        }

        private bool IsStairTileCached(Chunk chunk)
        {
            return _cachedStairTiles.Count > 0 && _cachedStairTiles.Contains(StairTileOf(chunk));
        }

        private void MarkStairTileScanned(Chunk chunk)
        {
            _scannedStairTiles.Add(StairTileOf(chunk));
        }

        internal static string StairCacheHeader(string map)
        {
            return $"# Winter Outbreak stair flights | map {map} | game {Application.version} | mod {WinterOutbreakPlugin.PluginVersion}";
        }

        // Reads the flights and scanned tiles of an earlier raid on this map. Returns a line for the log.
        internal string LoadStairCache(string path, string header)
        {
            if (!File.Exists(path))
            {
                return "no stair cache for this map yet";
            }

            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0] != header)
            {
                return "stair cache is from another game or mod version; measuring again";
            }

            var invariant = CultureInfo.InvariantCulture;
            int flights = 0;
            int rejected = 0;
            foreach (string line in lines)
            {
                if (line.StartsWith("tile "))
                {
                    string[] parts = line.Split(' ');
                    _cachedStairTiles.Add(CellKey(int.Parse(parts[1], invariant), int.Parse(parts[2], invariant)));
                }
                else if (line.StartsWith("flight "))
                {
                    StairFlight flight = ParseFlight(line, invariant);
                    if (flight != null && RegisterFlight(flight))
                    {
                        flights++;
                    }
                    else
                    {
                        rejected++;
                    }
                }
            }

            return $"stair cache read: {flights} flights, {_cachedStairTiles.Count} scanned tiles"
                + (rejected > 0 ? $", {rejected} unreadable or overlapping flights skipped" : "");
        }

        // Writes every flight of this raid (cached and new) and every tile scanned so far.
        internal string SaveStairCache(string path, string header)
        {
            var tiles = new HashSet<long>(_cachedStairTiles);
            tiles.UnionWith(_scannedStairTiles);
            if (tiles.Count == 0)
            {
                return "stair cache not written: nothing scanned";
            }

            var invariant = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            text.AppendLine(header);
            foreach (long key in tiles)
            {
                int x = (int)(key >> 32);
                int z = (int)(uint)key;
                text.AppendLine(string.Format(invariant, "tile {0} {1}", x, z));
            }

            foreach (var flight in _stairFlights)
            {
                text.AppendLine(FormatFlight(flight, invariant));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text.ToString());
            return $"stair cache written: {_stairFlights.Count} flights, {tiles.Count} scanned tiles";
        }

        // flight <origin x z> <dir x z> <top> <bottom> <left> <right> <rise> | risers | heights | tread left | tread
        // right
        private static string FormatFlight(StairFlight flight, CultureInfo invariant)
        {
            string Numbers(float[] values)
            {
                var parts = new List<string>();
                if (values != null)
                {
                    foreach (float value in values)
                    {
                        parts.Add(value.ToString("R", invariant));
                    }
                }

                return string.Join(" ", parts);
            }

            return string.Format(invariant, "flight {0} {1} {2} {3} {4} {5} {6} {7} {8} | {9} | {10} | {11} | {12}",
                flight.Origin.x.ToString("R", invariant), flight.Origin.y.ToString("R", invariant),
                flight.Dir.x.ToString("R", invariant), flight.Dir.y.ToString("R", invariant),
                flight.Top.ToString("R", invariant), flight.Bottom.ToString("R", invariant),
                flight.Left.ToString("R", invariant), flight.Right.ToString("R", invariant),
                flight.Rise.ToString("R", invariant),
                Numbers(flight.Risers), Numbers(flight.Heights), Numbers(flight.TreadLeft), Numbers(flight.TreadRight));
        }

        private static StairFlight ParseFlight(string line, CultureInfo invariant)
        {
            try
            {
                string[] sections = line.Substring("flight ".Length).Split('|');
                if (sections.Length != 5)
                {
                    return null;
                }

                float[] head = ParseNumbers(sections[0], invariant);
                if (head.Length != 9)
                {
                    return null;
                }

                var flight = new StairFlight
                {
                    Origin = new Vector2(head[0], head[1]),
                    Dir = new Vector2(head[2], head[3]).normalized,
                    Top = head[4],
                    Bottom = head[5],
                    Left = head[6],
                    Right = head[7],
                    Rise = head[8],
                    Risers = ParseNumbers(sections[1], invariant),
                    Heights = ParseNumbers(sections[2], invariant),
                    TreadLeft = ParseNumbers(sections[3], invariant),
                    TreadRight = ParseNumbers(sections[4], invariant)
                };

                if (flight.Risers.Length < 2 || flight.Heights.Length != flight.Risers.Length - 1)
                {
                    return null;
                }

                flight.Side = new Vector2(-flight.Dir.y, flight.Dir.x);
                float run = flight.Risers[flight.Risers.Length - 1] - flight.Risers[0];
                float slope = (flight.Top - flight.Heights[0]) / Mathf.Max(0.1f, run);
                flight.RampNormal = new Vector3(-flight.Dir.x * slope, 1f, -flight.Dir.y * slope).normalized;
                return flight;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static float[] ParseNumbers(string section, CultureInfo invariant)
        {
            var values = new List<float>();
            foreach (string part in section.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                values.Add(float.Parse(part, invariant));
            }

            return values.ToArray();
        }
    }
}
