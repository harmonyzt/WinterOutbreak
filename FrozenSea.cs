using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using WaterSSR;

namespace WinterOutbreak
{
    // The sea must not freeze over
    public sealed partial class WinterOutbreakPlugin
    {
        // Path parts of each map's sea quads
        private static readonly Dictionary<string, string> MapSeas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "shoreline", "Shoreline_Sea_Water_" },
            { "lighthouse", "WATER/sea/" }
        };

        // Areas where the sea ice is open to walk on
        private static readonly Dictionary<string, string> MapSeaOpenZones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {
                "lighthouse",
                "64,376; 66,380; 71,385; 75,387; 58,409; 43,433; 37,458; 28,503; 31,511; 47,509; 67,507; 87,493; 102,450; 111,437; 113,416; 116,401; 119,399; 124,398; 129,397; 135,395; 141,392; 151,388; 157,384; 164,378; 171,367; 176,356; 176,345; 175,335; 173,321; 171,307; 171,286; 170,258; 161,234; 151,226; 141,226; 119,239; 101,268; 90,297; 84,310; 79,322; 75,331; 71,335; 67,341; 64,347; 64,354; 63,360; 64,366; 64,370"
                + " | "
                + "-33,45; -30,47; -29,49; -41,58; -41,59; -37,62; -42,79; -42,88; -37,94; -31,99; -27,102; -22,102; -12,102; -6,99; 0,97; 6,92; 11,87; 14,82; 19,75; 19,70; 18,63; 10,51; 14,49; 17,46; 19,40; 19,35; 19,30; 18,28; 16,22; 14,18; 11,11; 7,6; 5,0; 0,-4; -7,-9; -15,-13; -23,-15; -30,-15; -39,-15; -40,-15; -46,-8; -47,3; -45,15; -45,22; -45,28; -42,34; -40,38; -36,43"
                + " | "
                + "-63,-99; -64,-92; -64,-88; -64,-83; -62,-76; -62,-67; -59,-61; -56,-53; -51,-47; -45,-43; -37,-41; -29,-39; -18,-38; -10,-38; -4,-38; -5,-51; -2,-61; 0,-70; 1,-76; 2,-80; 2,-86; 4,-90; 5,-95; 5,-99; 6,-106; 7,-111; 7,-115; 6,-120; 7,-127; 6,-132; 5,-142; 4,-152; -1,-162; -7,-170; -9,-170; -18,-170; -27,-166; -36,-161; -42,-156; -47,-152; -51,-144; -53,-135; -54,-130; -56,-124; -58,-117; -59,-114; -60,-111; -62,-107"
                + " | "
                + "-401,-294; -402,-268; -401,-246; -399,-240; -380,-223; -360,-207; -339,-195; -312,-195; -286,-200; -265,-203; -247,-207; -231,-209; -218,-241; -205,-246; -199,-248; -184,-254; -174,-258; -163,-261; -152,-265; -136,-270; -118,-274; -94,-284; -66,-296; -34,-317; -11,-344; -10,-356; -15,-372; -43,-390; -62,-408; -84,-420; -111,-428; -136,-425; -156,-426; -174,-428; -189,-428; -205,-424; -219,-423; -246,-422; -259,-420; -274,-414; -284,-411; -301,-403; -318,-395; -335,-385; -349,-372; -363,-358; -376,-340; -390,-319"
                + " | "
                + "83,-482; 83,-477; 82,-472; 81,-466; 81,-460; 81,-453; 81,-443; 82,-440; 91,-446; 98,-444; 102,-443; 108,-444; 118,-446; 120,-446; 126,-447; 132,-449; 136,-450; 141,-453; 144,-457; 147,-460; 152,-467; 155,-471; 159,-477; 159,-479; 159,-489; 158,-491; 154,-497; 151,-501; 147,-507; 144,-510; 138,-514; 135,-517; 129,-520; 124,-524; 119,-529; 117,-531; 107,-532; 101,-532; 94,-531; 93,-530; 88,-525; 85,-520; 82,-513; 81,-508; 81,-502; 81,-497; 82,-493; 83,-488"
            }
        };

        private const float SeaGridStep = 3f;
        private const float SeaRegionMargin = 120f;
        private const int SeaMaxCells = 600000;
        private const float SeaMeshStep = 0.5f;
        private const float SeaIceLift = 0.05f;
        // The sea ice's colliders stand this far over the water line, 2 cm under the drawn ice like the ponds'
        private const float SeaColliderLift = 0.03f;
        // Water more than this far from any shore counts as open sea
        private const float SeaOpenWater = 50f;
        private const float SeaShelterReach = 40f;
        private const float SeaDeepReach = 12f;
        private const float SeaFloeReach = 18f;
        // Only coast within this distance of the map's walls freezes
        private const float SeaBorderReach = 60f;
        // The ice sheet's broken outer face reaches this far under the water line
        private const float SeaSlabDepth = 0.22f;
        private static readonly Vector4 SeaEdgeParams = new Vector4(1f, -0.01f, 0.7f, 0f);

        // Chunks of 20 x 20 grid cells (about 60 m) for culling and the near and far versions
        private const int SeaChunkCells = 20;
        // Squares this far inside the ice (m) are merged into strips
        private const float SeaMergeInside = 1f;

        private sealed class SeaMeshParts
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector2> Insides = new List<Vector2>();
            public readonly List<int> Triangles = new List<int>();
        }

        private sealed class SeaChunk
        {
            public MeshRenderer Near;
            public MeshRenderer Far;
            public Bounds Bounds;
        }

        private readonly List<SeaChunk> _seaChunks = new List<SeaChunk>();

        private Fixed<bool> _frozenSea;
        private Fixed<float> _seaIceWidth;
        private Fixed<string> _seaOpenZones;
        private ConfigEntry<float> _seaIceDetailDistance;

        private void BindFrozenSeaSettings()
        {
            _frozenSea = new Fixed<bool>(true);
            _seaIceWidth = new Fixed<float>(22f);

            _seaIceDetailDistance = BindSetting(PerformanceSection, "Sea Ice Detail Distance (m)", 90f,
                "Sea ice closer than this has a detailed edge. Lower it on weaker graphics cards; 0 always uses the simple edge.", 4,
                oldSection: "Frozen Lake");

            _seaOpenZones = new Fixed<string>("");
        }

        private List<WaterObject> FindSeaTargets(string map)
        {
            var targets = new List<WaterObject>();
            if (_frozenSea == null || !Ice.Sea || !MapSeas.TryGetValue(map, out string seaPath))
            {
                return targets;
            }

            foreach (var group in FindObjectsOfType<WaterForSSRv3>())
            {
                foreach (var target in group.Targets ?? Array.Empty<WaterObject>())
                {
                    if (target?.Filter != null && target.Renderer != null && target.Renderer.enabled
                        && PathOf(target.Filter.transform).IndexOf(seaPath, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        targets.Add(target);
                    }
                }
            }

            return targets;
        }

        private void ApplyFrozenSea(string map, List<WaterObject> seaTargets, int lowPoly, int highPoly, int wallLayer, int water)
        {
            var clock = Stopwatch.StartNew();

            // The sea is one flat level made of many quads
            Bounds sea = seaTargets[0].Renderer.bounds;
            foreach (var target in seaTargets)
            {
                sea.Encapsulate(target.Renderer.bounds);
            }

            float waterY = sea.max.y;
            if (!TrySeaRegion(sea, out Bounds region, out string regionSource))
            {
                Logger.LogWarning($"Frozen sea on {map}: no map area found beside the sea ({seaTargets.Count} sea quads); left as water.");
                return;
            }

            float step = SeaGridStep;
            while ((region.size.x / step) * (region.size.z / step) > SeaMaxCells)
            {
                step *= 1.25f;
            }

            var origin = new Vector3(region.min.x, waterY, region.min.z);
            var axisA = new Vector3(region.size.x, 0f, 0f);
            var axisB = new Vector3(0f, 0f, region.size.z);
            var root = new GameObject("Winter Outbreak sea ice");
            _iceObjects.Add(root);

            BuildIceWalls(root, origin, axisA, axisB, waterY, wallLayer, water, IceWalls.None, IceBorders.Keep, step, out int waterCells,
                out int outsideCells, out _, out _, out var field, out var grid);
            long gridMs = clock.ElapsedMilliseconds;

            // The grid fits whole cells into the area, so its cells are a little smaller than the step
            float cellA = axisA.x / grid.CellsA;
            float cellB = axisB.z / grid.CellsB;
            field.Sea = true;
            int shoreCells = SeedSeaShores(field, grid, origin, axisA, axisB);
            int coveCells = MeasureSeaShelter(field, grid);

            // Cells well inside the band are ice
            float width = Mathf.Clamp(Ice.SeaWidth, 4f, 80f);
            var edges = new float[grid.CellsA, grid.CellsB];
            int bandCells = 0;
            for (int i = 0; i < grid.CellsA; i++)
            {
                for (int j = 0; j < grid.CellsB; j++)
                {
                    byte state = grid.State[i, j];
                    if (state == CellLand)
                    {
                        continue;
                    }

                    Vector3 centre = CellCentre(origin, axisA, axisB, grid.CellsA, grid.CellsB, i, j);
                    float edge = SeaEdge(field, centre.x, centre.z, width, true);
                    edges[i, j] = edge;
                    if (edge > step * 0.75f)
                    {
                        grid.State[i, j] = CellIce;
                        bandCells++;
                    }
                    else if (state == CellIce)
                    {
                        grid.State[i, j] = CellOutside;
                    }
                }
            }

            FrozenIce.Fields.Add(field);
            int strips = BuildIceStrips(root, origin + Vector3.up * SeaColliderLift, axisA, axisB, grid, field, lowPoly, highPoly,
                true, out _);
            field.Top = waterY + SeaColliderLift;
            string zones = OpenSeaZones(map, root, field, grid, origin, cellA, cellB, wallLayer);
            Material material = CreateIceMaterial(field, out bool custom);
            if (custom)
            {
                material.SetVector("_EdgeParams", SeaEdgeParams);
            }

            _iceDeepReach.Add(SeaDeepReach);
            int triangles = BuildSeaIceMesh(root, field, grid, origin, cellA, cellB, width, waterY + SeaIceLift, material,
                out int farTriangles, out int chunks);
        }

        // Opens the configured areas
        private string OpenSeaZones(string map, GameObject root, FrozenIce.IceField field, IceGrid grid, Vector3 origin,
            float cellA, float cellB, int wallLayer)
        {
            string setting = (Ice.SeaOpenZones ?? "").Trim();
            if (setting.Length == 0)
            {
                MapSeaOpenZones.TryGetValue(map, out setting);
            }

            var zones = ParseSeaZones(setting);
            if (zones.Count == 0)
            {
                return "no open areas";
            }

            int na = grid.CellsA, nb = grid.CellsB;
            var walkable = new bool[na, nb];
            for (int i = 0; i < na; i++)
            {
                for (int j = 0; j < nb; j++)
                {
                    float x = origin.x + (i + 0.5f) * cellA, z = origin.z + (j + 0.5f) * cellB;
                    walkable[i, j] = field.CellFloors[j * na + i] != null && InsideSeaZone(zones, x, z);
                }
            }

            // BSG's walls with their middle inside an area
            int border = LayerMask.NameToLayer("LevelBorder");
            int opened = 0;
            foreach (var collider in FindObjectsOfType<Collider>())
            {
                if (collider == null || !collider.enabled || collider.isTrigger || collider.gameObject.layer != border
                    || collider.name.StartsWith("Winter Outbreak", StringComparison.Ordinal))
                {
                    continue;
                }

                Bounds b = collider.bounds;
                if (Mathf.Max(b.size.x, b.size.z) > 150f || !InsideSeaZone(zones, b.center.x, b.center.z))
                {
                    continue;
                }

                collider.enabled = false;
                _disabledWaterColliders.Add(collider);
                opened++;
            }

            // Walls on the cell edges, merged into runs along each grid line
            float top = grid.WaterY + IceWallHeight;
            int walls = 0;
            for (int i = 0; i <= na; i++)
            {
                int runStart = -1;
                for (int j = 0; j <= nb; j++)
                {
                    bool edge = j < nb && SeaZoneEdge(grid, walkable, i - 1, j, i, j);
                    if (edge && runStart < 0)
                    {
                        runStart = j;
                    }
                    else if (!edge && runStart >= 0)
                    {
                        var start = new Vector3(origin.x + i * cellA, grid.WaterY, origin.z + runStart * cellB);
                        AddWall(root, start, new Vector3(0f, 0f, (j - runStart) * cellB), grid.WaterY, top, wallLayer);
                        walls++;
                        runStart = -1;
                    }
                }
            }

            for (int j = 0; j <= nb; j++)
            {
                int runStart = -1;
                for (int i = 0; i <= na; i++)
                {
                    bool edge = i < na && SeaZoneEdge(grid, walkable, i, j - 1, i, j);
                    if (edge && runStart < 0)
                    {
                        runStart = i;
                    }
                    else if (!edge && runStart >= 0)
                    {
                        var start = new Vector3(origin.x + runStart * cellA, grid.WaterY, origin.z + j * cellB);
                        AddWall(root, start, new Vector3((i - runStart) * cellA, 0f, 0f), grid.WaterY, top, wallLayer);
                        walls++;
                        runStart = -1;
                    }
                }
            }

            return $"{zones.Count} open areas, {opened} BSG walls taken down, {walls} wall segments";
        }

        // An edge between two neighbouring cells needs a wall when one side is ice you can stand on in an open area and
        // the other is neither that nor land
        private static bool SeaZoneEdge(IceGrid grid, bool[,] walkable, int ia, int ja, int ib, int jb)
        {
            bool a = InGrid(grid, ia, ja) && walkable[ia, ja];
            bool b = InGrid(grid, ib, jb) && walkable[ib, jb];
            if (a == b)
            {
                return false;
            }

            int oi = a ? ib : ia, oj = a ? jb : ja;
            return !InGrid(grid, oi, oj) || grid.State[oi, oj] != CellLand;
        }

        private static bool InGrid(IceGrid grid, int i, int j)
        {
            return i >= 0 && j >= 0 && i < grid.CellsA && j < grid.CellsB;
        }

        private static List<Vector2[]> ParseSeaZones(string setting)
        {
            var zones = new List<Vector2[]>();
            if (string.IsNullOrWhiteSpace(setting) || string.Equals(setting.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                return zones;
            }

            foreach (string loop in setting.Split('|'))
            {
                var points = new List<Vector2>();
                foreach (string pair in loop.Split(';'))
                {
                    string[] parts = pair.Split(',');
                    if (parts.Length == 2
                        && float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                        && float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                    {
                        points.Add(new Vector2(x, z));
                    }
                }

                if (points.Count >= 3)
                {
                    zones.Add(points.ToArray());
                }
            }

            return zones;
        }

        // Even-odd test against each loop
        private static bool InsideSeaZone(List<Vector2[]> zones, float x, float z)
        {
            foreach (var loop in zones)
            {
                bool inside = false;
                for (int k = 0, prev = loop.Length - 1; k < loop.Length; prev = k++)
                {
                    Vector2 a = loop[k], b = loop[prev];
                    if ((a.y > z) != (b.y > z) && x < (b.x - a.x) * (z - a.y) / (b.y - a.y) + a.x)
                    {
                        inside = !inside;
                    }
                }

                if (inside)
                {
                    return true;
                }
            }

            return false;
        }

        // The coast to cover
        private static bool TrySeaRegion(Bounds sea, out Bounds region, out string source)
        {
            int border = LayerMask.NameToLayer("LevelBorder");
            bool found = false;
            region = new Bounds();
            source = "LevelBorder colliders";
            foreach (var collider in FindObjectsOfType<Collider>())
            {
                if (collider == null || !collider.enabled || collider.gameObject.layer != border || collider.bounds.size.x > 5000f)
                {
                    continue;
                }

                if (found)
                {
                    region.Encapsulate(collider.bounds);
                }
                else
                {
                    region = collider.bounds;
                    found = true;
                }
            }

            if (!found)
            {
                source = "terrain";
                foreach (var terrain in Terrain.activeTerrains)
                {
                    var bounds = new Bounds(terrain.GetPosition() + terrain.terrainData.size * 0.5f, terrain.terrainData.size);
                    if (found)
                    {
                        region.Encapsulate(bounds);
                    }
                    else
                    {
                        region = bounds;
                        found = true;
                    }
                }
            }

            if (!found)
            {
                return false;
            }

            region.Expand(new Vector3(SeaRegionMargin * 2f, 0f, SeaRegionMargin * 2f));
            float minX = Mathf.Max(region.min.x, sea.min.x), maxX = Mathf.Min(region.max.x, sea.max.x);
            float minZ = Mathf.Max(region.min.z, sea.min.z), maxZ = Mathf.Min(region.max.z, sea.max.z);
            if (maxX - minX < 10f || maxZ - minZ < 10f)
            {
                return false;
            }

            region = new Bounds(new Vector3((minX + maxX) * 0.5f, sea.max.y, (minZ + maxZ) * 0.5f), new Vector3(maxX - minX, 0f, maxZ - minZ));
            return true;
        }

        // The sea's shore is the terrain beside the water within reach of the map's walls
        private static int SeedSeaShores(FrozenIce.IceField field, IceGrid grid, Vector3 origin, Vector3 axisA, Vector3 axisB)
        {
            int border = LayerMask.NameToLayer("LevelBorder");
            int mask = border >= 0 ? 1 << border : 0;
            int na = grid.CellsA, nb = grid.CellsB;
            int shores = 0;

            for (int j = 0; j < nb; j++)
            {
                for (int i = 0; i < na; i++)
                {
                    int k = j * na + i;
                    field.Distance[k] = 1e6f;
                    if (grid.State[i, j] != CellLand || !grid.OnTerrain[i, j] || !BesideWater(grid, i, j))
                    {
                        continue;
                    }

                    // A shore near the map's walls, or beside shallow water with a sea bed under it
                    Vector3 centre = CellCentre(origin, axisA, axisB, na, nb, i, j);
                    bool nearWall = Physics.CheckSphere(new Vector3(centre.x, grid.WaterY, centre.z), SeaBorderReach, mask,
                        QueryTriggerInteraction.Collide);
                    if ((nearWall || BesideShallowWater(grid, i, j)) && BesideOpenWater(grid, origin, axisA, axisB, i, j, mask))
                    {
                        field.Distance[k] = 0f;
                        shores++;
                    }
                }
            }

            SpreadShoreDistances(field);
            for (int k = 0; k < field.Distance.Length; k++)
            {
                field.Distance[k] = Mathf.Min(500f, field.Distance[k]);
            }

            return shores;
        }

        private static bool BesideWater(IceGrid grid, int i, int j)
        {
            for (int a = Mathf.Max(0, i - 1); a <= Mathf.Min(grid.CellsA - 1, i + 1); a++)
            {
                for (int b = Mathf.Max(0, j - 1); b <= Mathf.Min(grid.CellsB - 1, j + 1); b++)
                {
                    if (grid.State[a, b] != CellLand)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Metres from each water cell to the open sea, measured through the water (land is a barrier)
        private static int MeasureSeaShelter(FrozenIce.IceField field, IceGrid grid)
        {
            int na = grid.CellsA, nb = grid.CellsB;
            var shelter = new float[na * nb];
            float stepA = 1f / (field.InvA.magnitude * na), stepB = 1f / (field.InvB.magnitude * nb);
            float diagonal = Mathf.Sqrt(stepA * stepA + stepB * stepB);
            for (int k = 0; k < shelter.Length; k++)
            {
                bool land = grid.State[k % na, k / na] == CellLand;
                shelter[k] = land ? float.PositiveInfinity : field.Distance[k] > SeaOpenWater ? 0f : 1e6f;
            }

            for (int round = 0; round < 4; round++)
            {
                for (int j = 0; j < nb; j++)
                {
                    for (int i = 0; i < na; i++)
                    {
                        int k = j * na + i;
                        if (float.IsPositiveInfinity(shelter[k]))
                        {
                            continue;
                        }

                        float v = shelter[k];
                        if (i > 0) v = Mathf.Min(v, shelter[k - 1] + stepA);
                        if (j > 0) v = Mathf.Min(v, shelter[k - na] + stepB);
                        if (i > 0 && j > 0) v = Mathf.Min(v, shelter[k - na - 1] + diagonal);
                        if (i + 1 < na && j > 0) v = Mathf.Min(v, shelter[k - na + 1] + diagonal);
                        shelter[k] = v;
                    }
                }

                for (int j = nb - 1; j >= 0; j--)
                {
                    for (int i = na - 1; i >= 0; i--)
                    {
                        int k = j * na + i;
                        if (float.IsPositiveInfinity(shelter[k]))
                        {
                            continue;
                        }

                        float v = shelter[k];
                        if (i + 1 < na) v = Mathf.Min(v, shelter[k + 1] + stepA);
                        if (j + 1 < nb) v = Mathf.Min(v, shelter[k + na] + stepB);
                        if (i + 1 < na && j + 1 < nb) v = Mathf.Min(v, shelter[k + na + 1] + diagonal);
                        if (i > 0 && j + 1 < nb) v = Mathf.Min(v, shelter[k + na - 1] + diagonal);
                        shelter[k] = v;
                    }
                }
            }

            // Land reads 0 so it does not bleed into the water cells beside it when sampled
            int coves = 0;
            for (int k = 0; k < shelter.Length; k++)
            {
                if (float.IsPositiveInfinity(shelter[k]))
                {
                    shelter[k] = 0f;
                }
                else
                {
                    shelter[k] = Mathf.Min(500f, shelter[k]);
                    if (shelter[k] > SeaShelterReach)
                    {
                        coves++;
                    }
                }
            }

            field.Shelter = shelter;
            return coves;
        }

        // Beside water with a sea bed less than 30 m down
        private static bool BesideShallowWater(IceGrid grid, int i, int j)
        {
            for (int a = Mathf.Max(0, i - 1); a <= Mathf.Min(grid.CellsA - 1, i + 1); a++)
            {
                for (int b = Mathf.Max(0, j - 1); b <= Mathf.Min(grid.CellsB - 1, j + 1); b++)
                {
                    float bed = grid.GroundY[a, b];
                    if (grid.State[a, b] != CellLand && !float.IsNaN(bed) && bed > grid.WaterY - 30f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Beside a water cell nothing stands over
        private static bool BesideOpenWater(IceGrid grid, Vector3 origin, Vector3 axisA, Vector3 axisB, int i, int j, int borderMask)
        {
            int water = LayerMask.NameToLayer("Water");
            int mask = Physics.DefaultRaycastLayers & ~borderMask & ~(water >= 0 ? 1 << water : 0);
            for (int a = Mathf.Max(0, i - 1); a <= Mathf.Min(grid.CellsA - 1, i + 1); a++)
            {
                for (int b = Mathf.Max(0, j - 1); b <= Mathf.Min(grid.CellsB - 1, j + 1); b++)
                {
                    if (grid.State[a, b] == CellLand)
                    {
                        continue;
                    }

                    Vector3 p = CellCentre(origin, axisA, axisB, grid.CellsA, grid.CellsB, a, b);
                    if (!Physics.Raycast(new Vector3(p.x, grid.WaterY + 400f, p.z), Vector3.down, 400f - 0.05f, mask,
                        QueryTriggerInteraction.Ignore))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // How far a point lies inside the ice (metres, negative in open water)
        private static float SeaEdge(FrozenIce.IceField field, float x, float z, float width, bool fine)
        {
            if (!field.TrySample(x, z, out float distance))
            {
                return -1f;
            }

            // On the shore
            if (distance <= 0.01f)
            {
                return 1f;
            }

            // Elsewhere it stays smooth
            float rough = SeaRoughness(x, z);
            float edge = SeaBandWidth(x, z, width) - distance;

            // Sheltered coves and channels freeze over from bank to bank
            if (field.TrySampleShelter(x, z, out float shelter))
            {
                edge = Mathf.Max(edge, shelter - SeaShelterReach);
            }

            if (edge < 0f && edge > -SeaFloeReach)
            {
                float floe = (Mathf.PerlinNoise(x / 17f + 7.3f, z / 17f + 3.1f) - Mathf.Lerp(0.7f, 0.6f, rough)) * 30f;
                edge = Mathf.Max(edge, floe + edge * 0.15f);
            }

            // Rough stretches add broken bites a metre or two across
            if (fine)
            {
                edge += (Mathf.PerlinNoise(x / 6.1f + 1.7f, z / 6.1f + 9.2f) - 0.5f) * Mathf.Lerp(2.4f, 4f, rough)
                    + (Mathf.PerlinNoise(x / 2.9f + 4.1f, z / 2.9f + 2.6f) - 0.5f) * Mathf.Lerp(0.6f, 1.6f, rough)
                    + (Mathf.PerlinNoise(x / 1.5f + 8.3f, z / 1.5f + 6.9f) - 0.5f) * 1.4f * rough;
            }

            return edge;
        }

        // 0 smooth, 1 broken up, in patches about 70 m across
        private static float SeaRoughness(float x, float z)
        {
            float patch = Mathf.PerlinNoise(x / 70f + 5.5f, z / 70f + 2.2f);
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 0.7f, patch));
        }

        private static float SeaBandWidth(float x, float z, float width)
        {
            float broad = Mathf.PerlinNoise(x / 140f + 0.37f, z / 140f + 0.71f) * 2f - 1f;
            float medium = Mathf.PerlinNoise(x / 45f + 31.1f, z / 45f + 17.4f) * 2f - 1f;
            return Mathf.Max(4f, width * (1f + 0.55f * broad + 0.25f * medium));
        }

        // The ice mesh, in chunks about 60 m square so cameras skip the ones they cannot see (the scope camera most of
        // all)
        private int BuildSeaIceMesh(GameObject root, FrozenIce.IceField field, IceGrid grid, Vector3 origin, float cellA, float cellB,
            float width, float y, Material material, out int farTriangles, out int chunks)
        {
            int na = grid.CellsA, nb = grid.CellsB;
            int split = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(cellA, cellB) / SeaMeshStep));
            var values = new float[split + 1, split + 1];
            var corners = new Vector3[4];
            var cornerValues = new float[4];
            var polygon = new List<Vector3>(8);
            var polygonInside = new List<float>(8);
            int nearTriangles = 0;
            farTriangles = 0;
            chunks = 0;

            for (int ci = 0; ci < na; ci += SeaChunkCells)
            {
                for (int cj = 0; cj < nb; cj += SeaChunkCells)
                {
                    var near = new SeaMeshParts();
                    var far = new SeaMeshParts();
                    for (int i = ci; i < Mathf.Min(na, ci + SeaChunkCells); i++)
                    {
                        for (int j = cj; j < Mathf.Min(nb, cj + SeaChunkCells); j++)
                        {
                            if (IsInlandCell(grid, i, j))
                            {
                                continue;
                            }

                            float x0 = origin.x + i * cellA, z0 = origin.z + j * cellB;
                            if (SeaEdge(field, x0 + cellA * 0.5f, z0 + cellB * 0.5f, width, false) < -8f)
                            {
                                continue;
                            }

                            bool allInside = true;
                            for (int a = 0; a <= split; a++)
                            {
                                for (int b = 0; b <= split; b++)
                                {
                                    float v = SeaEdge(field, x0 + cellA * a / split, z0 + cellB * b / split, width, true);
                                    values[a, b] = v;
                                    allInside &= v > 0f;
                                }
                            }

                            // Corners clockwise seen from above (x right, z up), so the triangles face up
                            SetSquare(corners, x0, z0, cellA, cellB, y);
                            cornerValues[0] = values[0, 0];
                            cornerValues[1] = values[0, split];
                            cornerValues[2] = values[split, split];
                            cornerValues[3] = values[split, 0];
                            if (allInside)
                            {
                                AddSquare(near, corners, cornerValues);
                                AddSquare(far, corners, cornerValues);
                                continue;
                            }

                            TraceSquare(far, corners, cornerValues, polygon, polygonInside);
                            AddNearCell(near, values, split, x0, z0, cellA / split, cellB / split, y, corners, cornerValues,
                                polygon, polygonInside);
                        }
                    }

                    if (near.Triangles.Count == 0)
                    {
                        continue;
                    }

                    var chunk = new GameObject($"sea ice chunk {ci / SeaChunkCells}, {cj / SeaChunkCells}");
                    chunk.transform.SetParent(root.transform, false);
                    var seaChunk = new SeaChunk
                    {
                        Near = BuildSeaChunkRenderer(chunk, "near", near, material),
                        Far = BuildSeaChunkRenderer(chunk, "far", far, material)
                    };
                    seaChunk.Bounds = seaChunk.Near.bounds;
                    seaChunk.Far.enabled = false;
                    _seaChunks.Add(seaChunk);
                    nearTriangles += near.Triangles.Count / 3;
                    farTriangles += far.Triangles.Count / 3;
                    chunks++;
                }
            }

            return nearTriangles;
        }

        // One grid cell of the near version: rows of 0.5 m squares well inside the ice are merged into strips
        private static void AddNearCell(SeaMeshParts parts, float[,] values, int split, float x0, float z0, float sa, float sb,
            float y, Vector3[] corners, float[] cornerValues, List<Vector3> polygon, List<float> polygonInside)
        {
            for (int a = 0; a < split; a++)
            {
                int runStart = -1;
                for (int b = 0; b <= split; b++)
                {
                    bool deep = b < split && values[a, b] > SeaMergeInside && values[a, b + 1] > SeaMergeInside
                        && values[a + 1, b] > SeaMergeInside && values[a + 1, b + 1] > SeaMergeInside;
                    if (deep)
                    {
                        if (runStart < 0)
                        {
                            runStart = b;
                        }

                        continue;
                    }

                    if (runStart >= 0)
                    {
                        SetSquare(corners, x0 + a * sa, z0 + runStart * sb, sa, (b - runStart) * sb, y);
                        cornerValues[0] = values[a, runStart];
                        cornerValues[1] = values[a, b];
                        cornerValues[2] = values[a + 1, b];
                        cornerValues[3] = values[a + 1, runStart];
                        AddSquare(parts, corners, cornerValues);
                        runStart = -1;
                    }

                    if (b == split)
                    {
                        break;
                    }

                    SetSquare(corners, x0 + a * sa, z0 + b * sb, sa, sb, y);
                    cornerValues[0] = values[a, b];
                    cornerValues[1] = values[a, b + 1];
                    cornerValues[2] = values[a + 1, b + 1];
                    cornerValues[3] = values[a + 1, b];
                    TraceSquare(parts, corners, cornerValues, polygon, polygonInside);
                }
            }
        }

        private static void SetSquare(Vector3[] corners, float x, float z, float sizeX, float sizeZ, float y)
        {
            corners[0] = new Vector3(x, y, z);
            corners[1] = new Vector3(x, y, z + sizeZ);
            corners[2] = new Vector3(x + sizeX, y, z + sizeZ);
            corners[3] = new Vector3(x + sizeX, y, z);
        }

        private static void AddSquare(SeaMeshParts parts, Vector3[] corners, float[] cornerValues)
        {
            int first = parts.Vertices.Count;
            for (int k = 0; k < 4; k++)
            {
                parts.Vertices.Add(corners[k]);
                parts.Insides.Add(new Vector2(cornerValues[k], 0f));
            }

            parts.Triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
        }

        // Marching squares on one square
        private static void TraceSquare(SeaMeshParts parts, Vector3[] corners, float[] cornerValues, List<Vector3> polygon,
            List<float> polygonInside)
        {
            polygon.Clear();
            polygonInside.Clear();
            for (int k = 0; k < 4; k++)
            {
                int next = (k + 1) % 4;
                bool inside = cornerValues[k] > 0f;
                if (inside)
                {
                    polygon.Add(corners[k]);
                    polygonInside.Add(cornerValues[k]);
                }

                if (inside != cornerValues[next] > 0f)
                {
                    float t = cornerValues[k] / (cornerValues[k] - cornerValues[next]);
                    polygon.Add(Vector3.Lerp(corners[k], corners[next], t));
                    polygonInside.Add(0f);
                }
            }

            if (polygon.Count < 3)
            {
                return;
            }

            int first = parts.Vertices.Count;
            parts.Vertices.AddRange(polygon);
            foreach (float inside in polygonInside)
            {
                parts.Insides.Add(new Vector2(inside, 0f));
            }

            for (int k = 1; k + 1 < polygon.Count; k++)
            {
                parts.Triangles.Add(first);
                parts.Triangles.Add(first + k);
                parts.Triangles.Add(first + k + 1);
            }

            AddEdgeFaces(parts, polygon, polygonInside);
        }

        // Two neighbouring points on the outer edge (both at 0) are a piece of the edge line
        private static void AddEdgeFaces(SeaMeshParts parts, List<Vector3> polygon, List<float> polygonInside)
        {
            Vector3 centre = Vector3.zero;
            foreach (var point in polygon)
            {
                centre += point;
            }

            centre /= polygon.Count;
            Vector3 down = Vector3.down * (SeaSlabDepth + SeaIceLift);
            for (int k = 0; k < polygon.Count; k++)
            {
                int next = (k + 1) % polygon.Count;
                if (polygonInside[k] != 0f || polygonInside[next] != 0f)
                {
                    continue;
                }

                Vector3 a = polygon[k], b = polygon[next];
                int first = parts.Vertices.Count;
                parts.Vertices.Add(a);
                parts.Vertices.Add(b);
                parts.Vertices.Add(b + down);
                parts.Vertices.Add(a + down);
                for (int v = 0; v < 4; v++)
                {
                    parts.Insides.Add(Vector2.zero);
                }

                // Unity's front face for (a, b, b + down) faces along Cross(b - a, down)
                bool outward = Vector3.Dot(Vector3.Cross(b - a, down), (a + b) * 0.5f - centre) > 0f;
                if (outward)
                {
                    parts.Triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
                }
                else
                {
                    parts.Triangles.AddRange(new[] { first, first + 2, first + 1, first, first + 3, first + 2 });
                }
            }
        }

        private MeshRenderer BuildSeaChunkRenderer(GameObject chunk, string name, SeaMeshParts parts, Material material)
        {
            var mesh = new Mesh { name = "Winter Outbreak sea ice " + name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(parts.Vertices);
            mesh.SetUVs(0, parts.Vertices.Select(p => new Vector2(p.x / IceTextureMetres, p.z / IceTextureMetres)).ToList());
            mesh.SetUVs(1, parts.Insides);
            // Up everywhere, also on the edge faces
            mesh.SetNormals(Enumerable.Repeat(Vector3.up, parts.Vertices.Count).ToList());
            mesh.SetTriangles(parts.Triangles, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            _iceAssets.Add(mesh);

            var surface = new GameObject(name);
            surface.transform.SetParent(chunk.transform, false);
            surface.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = surface.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            _iceRenderers.Add(renderer);
            return renderer;
        }

        // Near version within the configured distance of the main camera, far version beyond it
        private void UpdateSeaIceDetail()
        {
            if (_seaChunks.Count == 0)
            {
                return;
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            Vector3 eye = camera.transform.position;
            float reach = Mathf.Max(0f, _seaIceDetailDistance.Value);
            float reachSqr = reach * reach;
            foreach (var chunk in _seaChunks)
            {
                if (chunk.Near == null || chunk.Far == null)
                {
                    continue;
                }

                bool near = chunk.Bounds.SqrDistance(eye) < reachSqr;
                if (chunk.Near.enabled != near)
                {
                    chunk.Near.enabled = near;
                    chunk.Far.enabled = !near;
                }
            }
        }

        // Terrain with terrain all around: no ice needed under it
        private static bool IsInlandCell(IceGrid grid, int i, int j)
        {
            for (int a = Mathf.Max(0, i - 1); a <= Mathf.Min(grid.CellsA - 1, i + 1); a++)
            {
                for (int b = Mathf.Max(0, j - 1); b <= Mathf.Min(grid.CellsB - 1, j + 1); b++)
                {
                    if (grid.State[a, b] != CellLand || !grid.OnTerrain[a, b])
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
