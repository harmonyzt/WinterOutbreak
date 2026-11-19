using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Configuration;
using EFT.Ballistics;
using UnityEngine;
using UnityEngine.AI;
using WaterSSR;

namespace WinterOutbreak
{
    // Snow samplers take the terrain as their floor reference
    internal static class FrozenIce
    {
        internal static readonly List<IceField> Fields = new List<IceField>();

        // Shore bank for voxel snow
        internal const float ShoreReach = 5f;

        internal sealed class IceField
        {
            public Vector2 Origin;
            public Vector2 InvA;
            public Vector2 InvB;
            public int CellsA;
            public int CellsB;
            public float[] Distance;
            public float Top;

            // Sea ice (FrozenSea.cs)
            public bool Sea;

            // The bullet collider under the ice
            public Collider Floor;
            public Collider[] CellFloors;

            public Collider FloorAt(float x, float z)
            {
                if (CellFloors == null)
                {
                    return Floor;
                }

                float dx = x - Origin.x, dz = z - Origin.y;
                int i = Mathf.FloorToInt((dx * InvA.x + dz * InvA.y) * CellsA);
                int j = Mathf.FloorToInt((dx * InvB.x + dz * InvB.y) * CellsB);
                return i >= 0 && j >= 0 && i < CellsA && j < CellsB ? CellFloors[j * CellsA + i] : null;
            }

            // Metres to the shore, bilinear between cell centres
            public float[] Shelter;

            public bool TrySample(float x, float z, out float distance)
            {
                return Sample(Distance, x, z, out distance);
            }

            public bool TrySampleShelter(float x, float z, out float shelter)
            {
                shelter = 0f;
                return Shelter != null && Sample(Shelter, x, z, out shelter);
            }

            private bool Sample(float[] values, float x, float z, out float distance)
            {
                float dx = x - Origin.x, dz = z - Origin.y;
                float u = (dx * InvA.x + dz * InvA.y) * CellsA - 0.5f;
                float v = (dx * InvB.x + dz * InvB.y) * CellsB - 0.5f;
                distance = 0f;
                if (u < -0.5f || v < -0.5f || u > CellsA - 0.5f || v > CellsB - 0.5f)
                {
                    return false;
                }

                int i0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, CellsA - 1), j0 = Mathf.Clamp(Mathf.FloorToInt(v), 0, CellsB - 1);
                int i1 = Mathf.Min(i0 + 1, CellsA - 1), j1 = Mathf.Min(j0 + 1, CellsB - 1);
                float fu = Mathf.Clamp01(u - i0), fv = Mathf.Clamp01(v - j0);
                float a = Mathf.Lerp(values[j0 * CellsA + i0], values[j0 * CellsA + i1], fu);
                float b = Mathf.Lerp(values[j1 * CellsA + i0], values[j1 * CellsA + i1], fu);
                distance = Mathf.Lerp(a, b, fv);
                return true;
            }
        }

        // Multiplier for voxel snow depth at a mesh corner: 1 anywhere but on the ice surface
        internal static float SnowShare(float x, float z, float y)
        {
            for (int i = 0; i < Fields.Count; i++)
            {
                IceField field = Fields[i];

                // Under the sea's water line (the sea bed beside the ice's colliders): no snow
                if (IsUnderSea(field, x, z, y))
                {
                    return 0f;
                }

                if (Mathf.Abs(y - field.Top) > 0.05f || !field.TrySample(x, z, out float distance))
                {
                    continue;
                }

                float reach = ShoreReach * Mathf.Lerp(0.6f, 1.4f, Mathf.PerlinNoise(x * 0.07f + 3.1f, z * 0.07f + 8.7f));
                return 1f - Mathf.SmoothStep(0f, 1f, distance / reach);
            }

            return 1f;
        }

        // True for a point on the sea bed near the frozen coast (world coordinates)
        internal static bool IsUnderSea(float x, float z, float y)
        {
            for (int i = 0; i < Fields.Count; i++)
            {
                if (IsUnderSea(Fields[i], x, z, y))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsUnderSea(IceField field, float x, float z, float y)
        {
            return field.Sea && y < field.Top - 0.05f && field.TrySample(x, z, out float distance) && distance < 120f;
        }

        // True for a point on the frozen surface itself (world coordinates)
        internal static bool IsIceTop(float x, float z, float y)
        {
            for (int i = 0; i < Fields.Count; i++)
            {
                IceField field = Fields[i];
                if (Mathf.Abs(y - field.Top) < 0.03f && field.TrySample(x, z, out _))
                {
                    return true;
                }
            }

            return false;
        }

        // Things lying in the ice no higher than this (floating logs, a sunken boat's rim, boards on the surface) are
        // sampled as ice
        internal const float FrozenInRise = 0.20f;

        // True for a point on a pier deck, jetty or other prop standing over the lake (world coordinates)
        internal static bool IsLakeDeck(float x, float z, float y)
        {
            for (int i = 0; i < Fields.Count; i++)
            {
                IceField field = Fields[i];
                if (y > field.Top + 0.05f && y < field.Top + 2.5f
                    && field.TrySample(x, z, out float distance) && distance > 1.5f)
                {
                    return true;
                }
            }

            return false;
        }

        // True where someone standing at this point is on ice with (almost) no snow
        internal static bool IsBareIce(Vector3 position)
        {
            for (int i = 0; i < Fields.Count; i++)
            {
                IceField field = Fields[i];
                if (Mathf.Abs(position.y - field.Top) < 0.4f && field.TrySample(position.x, position.z, out _))
                {
                    // The sea's field covers the whole coast, beach included: it only counts over its ice colliders
                    if (field.Sea && field.FloorAt(position.x, position.z) == null)
                    {
                        continue;
                    }

                    return SnowShare(position.x, position.z, field.Top) < 0.25f;
                }
            }

            return false;
        }

        internal static bool RaiseTerrain(Vector3 position, ref RaycastHit terrain, ref bool hasTerrain)
        {
            for (int i = 0; i < Fields.Count; i++)
            {
                Collider floor = Fields[i].FloorAt(position.x, position.z);
                if (floor == null || !floor.enabled)
                {
                    continue;
                }

                Bounds b = floor.bounds;
                if (position.x < b.min.x || position.x > b.max.x || position.z < b.min.z || position.z > b.max.z)
                {
                    continue;
                }

                var ray = new Ray(new Vector3(position.x, b.max.y + 1f, position.z), Vector3.down);
                if (floor.Raycast(ray, out var hit, b.size.y + 2f) && (!hasTerrain || hit.point.y > terrain.point.y))
                {
                    terrain = hit;
                    hasTerrain = true;
                    return true;
                }
            }

            return false;
        }
    }

    // Frozen lake
    public sealed partial class WinterOutbreakPlugin
    {
        private const float IceThickness = 0.3f;
        private const float IceWallHeight = 4f;
        private const float IceGridStep = 1.5f;
        private const float IceTextureMetres = 6f;
        // The drawn ice sits this far above the collider top, so voxel snow feathered to zero depth (which rests on the
        // collider, up to 6 mm above it) stays hidden under the ice instead of flickering through it
        private const float IceLift = 0.02f;
        private const float BackdropNavReach = 12f;
        private const float IceSprinkles = 0.6f;
        private const float IceDeepReach = 40f;
        // Woods' swamp ponds are small (mostly under 40 m across), so their ice darkens over 6 m instead of 40 m
        private const float SwampDeepReach = 6f;
        private const string SwampPath = "Water/Swamp/";

        // Woods' river runs along the map's edge: frozen to look at only
        private const string RiverPath = "Water/River/";
        private const float RiverDeepReach = 15f;
        private const float RiverGridStep = 3f;

        // Still ponds on other maps that freeze like Woods' swamp: walkable ice and no new walls
        private static readonly Dictionary<string, string[]> MapPonds = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "shoreline", new[] { "Shoreline_Lake_Water_", "Shoreline_Stump_Water_", "Shoreline_GardenPond_", "Shoreline_River_Cascade_" } },
            { "lighthouse", new[] { "WATER/swamps/" } }
        };
        private const string IceBundleFile = "lakeice.bundle";
        private const string IceShaderPath = "assets/shaders/lakeice.shader";

        // Baked ice maps in lakeice.bundle: material property and asset path
        private static readonly string[,] IceTexturePaths =
        {
            { "_MaskTex", "assets/textures/lakeice/lakeice_mask.png" },
            { "_NormalTex", "assets/textures/lakeice/lakeice_normal.png" },
            { "_DetailTex", "assets/textures/lakeice/lakeice_detail.png" },
            { "_NoiseTex", "assets/textures/lakeice/lakeice_noise.png" }
        };

        private static Shader _iceShader;
        private static readonly Dictionary<string, Texture2D> _iceTextures = new Dictionary<string, Texture2D>();
        private static bool _iceShaderLoadAttempted;
        private readonly List<Material> _iceMaterials = new List<Material>();
        private readonly List<float> _iceDeepReach = new List<float>();
        private readonly List<Renderer> _iceRenderers = new List<Renderer>();
        private readonly List<KeyValuePair<FrozenIce.IceField, Texture2D>> _iceShoreTextures =
            new List<KeyValuePair<FrozenIce.IceField, Texture2D>>();
        private static readonly int SnowColourId = Shader.PropertyToID("_SnowColor");

        // The ice's own snow
        private static readonly Vector4 IceSnowColour = new Vector4(0.80f, 0.82f, 0.84f, 1f);
        private readonly Dictionary<Camera, UnityEngine.Rendering.CommandBuffer> _iceRedrawBuffers =
            new Dictionary<Camera, UnityEngine.Rendering.CommandBuffer>();
        private Camera.CameraCallback _iceRedrawCallback;
        private readonly Plane[] _iceFrustum = new Plane[6];

        // The same targets EFT's snow pass writes (GBuffer0-2 and the lighting buffer)
        private static readonly UnityEngine.Rendering.RenderTargetIdentifier[] IceTargetsHdr =
        {
            UnityEngine.Rendering.BuiltinRenderTextureType.GBuffer0,
            UnityEngine.Rendering.BuiltinRenderTextureType.GBuffer1,
            UnityEngine.Rendering.BuiltinRenderTextureType.GBuffer2,
            UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget
        };

        private static readonly UnityEngine.Rendering.RenderTargetIdentifier[] IceTargetsLdr =
        {
            UnityEngine.Rendering.BuiltinRenderTextureType.GBuffer0,
            UnityEngine.Rendering.BuiltinRenderTextureType.GBuffer1,
            UnityEngine.Rendering.BuiltinRenderTextureType.GBuffer2,
            UnityEngine.Rendering.BuiltinRenderTextureType.GBuffer3
        };

        private Fixed<bool> _frozenLakeEnabled;
        private Fixed<string> _frozenLakeMaps;
        private Fixed<string> _frozenLakeSurfaces;
        private Fixed<string> _frozenLakeNoWalkLine;
        private Fixed<bool> _frozenSwamp;
        private Fixed<bool> _frozenRiver;
        private ConfigEntry<float> _iceRelief;

        private bool _frozenLakeApplied;
        private float _frozenLakeRetryAt;
        private readonly List<Renderer> _hiddenWater = new List<Renderer>();
        private readonly List<Collider> _disabledWaterColliders = new List<Collider>();
        private readonly List<Behaviour> _mutedWaterAudio = new List<Behaviour>();
        private readonly List<Bounds> _lakeAudioBounds = new List<Bounds>();
        private float _lakeAudioRecheckAt;
        private readonly List<GameObject> _iceObjects = new List<GameObject>();
        private readonly List<UnityEngine.Object> _iceAssets = new List<UnityEngine.Object>();

        private void BindFrozenLakeSettings()
        {
            _frozenLakeEnabled = new Fixed<bool>(true);
            _frozenLakeMaps = new Fixed<string>("woods,shoreline,lighthouse");
            _frozenLakeSurfaces = new Fixed<string>("Water/Lake/");
            _frozenSwamp = new Fixed<bool>(true);

            // 0.3 by default: at full strength the ripples read as moving water
            _iceRelief = BindSetting(PerformanceSection, "Ice Relief", 0.3f,
                "Ripples and ridges in the ice's shading. 0 turns them off, which is a little faster. "
                + "Applies from the next raid.", 5,
                true, new AcceptableValueRange<float>(0f, 1f), "Frozen Lake");

            BindIceNavMeshSettings();
            BindFrozenSeaSettings();

            _frozenRiver = new Fixed<bool>(true);
            _frozenLakeNoWalkLine = new Fixed<string>("");
        }

        private void UpdateFrozenLake(bool sceneReady)
        {
            UpdateIceNavMesh();
            UpdateSeaIceDetail();

            if (_iceMaterials.Count > 0)
            {
                // Snow on the ice follows the snow depth: current depth over the configured maximum
                float amount = _voxelSnow != null
                    ? Mathf.Clamp01(_voxelSnow.LastDepth / Mathf.Max(0.01f, _snowDepthCentimeters.Value * 0.01f))
                    : 0.5f;
                for (int i = 0; i < _iceMaterials.Count; i++)
                {
                    Material material = _iceMaterials[i];
                    if (material != null)
                    {
                        float deepReach = i < _iceDeepReach.Count ? _iceDeepReach[i] : IceDeepReach;
                        material.SetVector(IceParamsId, new Vector4(amount, FrozenIce.ShoreReach, IceSprinkles, deepReach));
                    }
                }
            }

            // Ambient emitters may only create their sources once the raid runs, so look once more after the freeze
            if (_lakeAudioRecheckAt > 0f && Time.time >= _lakeAudioRecheckAt)
            {
                _lakeAudioRecheckAt = 0f;
                foreach (var bounds in _lakeAudioBounds)
                {
                    MuteLakeAudio(bounds);
                }
            }

            if (_frozenLakeApplied || !sceneReady || _frozenLakeEnabled == null || _raid == null
                || Time.realtimeSinceStartup < _frozenLakeRetryAt || WaitingForHostIce())
            {
                return;
            }

            IceSettings ice = Ice;
            string map = _raid.LocationId ?? "";
            if (!ice.Enabled || !ListContains(ice.Maps, map))
            {
                _frozenLakeApplied = true;
                return;
            }

            try
            {
                if (!ApplyFrozenLake(map))
                {
                    _frozenLakeRetryAt = Time.realtimeSinceStartup + 2f;
                }
            }
            catch (Exception error)
            {
                Logger.LogWarning($"Frozen lake failed, water left as it was: {error}");
                ClearFrozenLake();
                _frozenLakeApplied = true;
            }
        }

        private static bool ListContains(string list, string value)
        {
            foreach (string entry in (list ?? "").Split(','))
            {
                string name = entry.Trim();
                if (name == "*" || string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool ApplyFrozenLake(string map)
        {
            var clock = Stopwatch.StartNew();
            IceSettings ice = Ice;
            var filters = (ice.Surfaces ?? "").Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
            if (ice.Swamp && string.Equals(map, "woods", StringComparison.OrdinalIgnoreCase)
                && !filters.Any(f => f.IndexOf(SwampPath, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                filters.Add(SwampPath);
            }

            if (ice.River && string.Equals(map, "woods", StringComparison.OrdinalIgnoreCase)
                && !filters.Any(f => f.IndexOf(RiverPath, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                filters.Add(RiverPath);
            }

            string[] ponds = null;
            if (ice.Swamp && MapPonds.TryGetValue(map, out ponds))
            {
                filters.AddRange(ponds);
            }

            var targets = new List<WaterObject>();
            foreach (var group in FindObjectsOfType<WaterForSSRv3>())
            {
                foreach (var target in group.Targets ?? Array.Empty<WaterObject>())
                {
                    if (target?.Filter != null && target.Renderer != null && target.Filter.sharedMesh != null
                        && filters.Any(f => PathOf(target.Filter.transform).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        targets.Add(target);
                    }
                }
            }

            var seaTargets = FindSeaTargets(map);
            if (targets.Count == 0 && seaTargets.Count == 0)
            {
                return false;
            }

            _frozenLakeApplied = true;
            int lowPoly = LayersMaskController.LowPolyColliderLayer;
            int highPoly = LayerMask.NameToLayer("HighPolyCollider");
            int border = LayerMask.NameToLayer("LevelBorder");
            int player = LayersMaskController.PlayerLayer;
            int wallLayer = border >= 0 && !Physics.GetIgnoreLayerCollision(player, border) ? border : lowPoly;
            int water = LayerMask.NameToLayer("Water");

            foreach (var target in targets)
            {
                if (!TryIceRectangle(target, out Vector3 origin, out Vector3 axisA, out Vector3 axisB))
                {
                    Logger.LogWarning($"Frozen lake: {PathOf(target.Filter.transform)} is not a flat surface; left as water.");
                    continue;
                }

                float waterY = origin.y;
                Bounds bounds = target.Renderer.bounds;
                target.Renderer.enabled = false;
                _hiddenWater.Add(target.Renderer);
                int firstDisabled = _disabledWaterColliders.Count;
                int colliders = DisableWaterColliders(bounds, water);

                // The swamp sits inside the playable map among buildings and bunkers
                string path = PathOf(target.Filter.transform);
                bool pond = ponds != null && ponds.Any(f => path.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
                bool swamp = pond || path.IndexOf(SwampPath, StringComparison.OrdinalIgnoreCase) >= 0;
                bool river = path.IndexOf(RiverPath, StringComparison.OrdinalIgnoreCase) >= 0;
                var walling = swamp ? IceWalls.None : river ? IceWalls.EveryBank : IceWalls.MapEdge;
                var root = new GameObject("Winter Outbreak ice " + target.Filter.name);
                _iceObjects.Add(root);
                float step = river ? RiverGridStep : IceGridStep;
                var borders = pond ? IceBorders.PondOnly : IceBorders.Disable;
                int walls = BuildIceWalls(root, origin, axisA, axisB, waterY, wallLayer, water, walling, borders, step, out int iceCells,
                    out int outsideCells, out int backdropCells, out int bordersDisabled, out var field, out var grid);

                // A pond whose grid finds no water at all
                if (pond && iceCells == 0)
                {
                    RestoreDryPond(target.Renderer, root, firstDisabled);
                    continue;
                }

                FrozenIce.Fields.Add(field);

                string colliderNote;
                if (swamp)
                {
                    int strips = BuildIceStrips(root, origin, axisA, axisB, grid, field, lowPoly, highPoly, true, out var walkRoot);
                    int carved = CarveDeepIce(root, origin, axisA, axisB, grid, waterY, 0.4f);
                    StartIceNavMesh(target.Filter.name, walkRoot, origin, axisA, axisB, grid, field);
                    colliderNote = $"{strips} collider strips over the water, {carved} navmesh carvers under deep ice"
                        + (_iceBotNavMesh.Value ? ", bot navmesh building" : "");
                }
                else if (river)
                {
                    // Bullets and snow still meet the ice (HighPolyCollider
                    int strips = BuildIceStrips(root, origin, axisA, axisB, grid, field, lowPoly, highPoly, false, out _);
                    int carved = CarveDeepIce(root, origin, axisA, axisB, grid, waterY, 0.05f);
                    colliderNote = $"look only, {strips} bullet strips over the water, {carved} navmesh carvers on the bed";
                }
                else
                {
                    BuildIceBox(root, "walk", lowPoly, origin, axisA, axisB, false);
                    field.Floor = BuildIceBox(root, "ballistic", highPoly, origin, axisA, axisB, true);
                    colliderNote = "one collider over the rectangle";
                }

                Material material = CreateIceMaterial(field, out bool custom);
                _iceDeepReach.Add(pond ? PondDeepReach(axisA, axisB) : swamp ? SwampDeepReach : river ? RiverDeepReach : IceDeepReach);
                // Neighbouring river quads overlap where they join
                BuildIceSurface(root, origin + Vector3.up * (IceLift + 0.003f * _hiddenWater.Count), axisA, axisB, material);

                MuteLakeAudio(bounds);
                _lakeAudioBounds.Add(bounds);
                _lakeAudioRecheckAt = Time.time + 20f;
            }

            if (seaTargets.Count > 0)
            {
                // The sea on its own: if it fails, the ponds frozen above stay
                try
                {
                    ApplyFrozenSea(map, seaTargets, lowPoly, highPoly, wallLayer, water);
                }
                catch (Exception error)
                {
                    Logger.LogWarning($"Frozen sea failed, sea left as water: {error}");
                }
            }

            int joined = JoinOverlappingShores();

            int noWalk = BuildNoWalkLine(_iceObjects.FirstOrDefault(), wallLayer, water);
            HookIceRedraw();

            return true;
        }

        private void RestoreDryPond(Renderer renderer, GameObject root, int firstDisabled)
        {
            renderer.enabled = true;
            _hiddenWater.Remove(renderer);

            for (int i = _disabledWaterColliders.Count - 1; i >= firstDisabled; i--)
            {
                if (_disabledWaterColliders[i] != null)
                {
                    _disabledWaterColliders[i].enabled = true;
                }

                _disabledWaterColliders.RemoveAt(i);
            }

            _iceObjects.Remove(root);
            Destroy(root);
        }

        private static float PondDeepReach(Vector3 axisA, Vector3 axisB)
        {
            float shortSide = Mathf.Min(axisA.magnitude, axisB.magnitude);
            return Mathf.Clamp(shortSide * 0.1f, SwampDeepReach, 20f);
        }

        // The water quad's world rectangle
        private static bool TryIceRectangle(WaterObject target, out Vector3 origin, out Vector3 axisA, out Vector3 axisB)
        {
            Bounds local = target.Filter.sharedMesh.bounds;
            Vector3 e = local.extents;
            int flat = e.x <= e.y && e.x <= e.z ? 0 : e.y <= e.z ? 1 : 2;
            int u = flat == 0 ? 1 : 0, v = flat == 2 ? 1 : 2;
            Vector3 eu = Vector3.zero, ev = Vector3.zero;
            eu[u] = e[u];
            ev[v] = e[v];
            Matrix4x4 toWorld = target.Renderer.localToWorldMatrix;
            Vector3 c0 = toWorld.MultiplyPoint3x4(local.center - eu - ev);
            Vector3 c1 = toWorld.MultiplyPoint3x4(local.center + eu - ev);
            Vector3 c3 = toWorld.MultiplyPoint3x4(local.center - eu + ev);
            origin = c0;
            axisA = c1 - c0;
            axisB = c3 - c0;
            return Mathf.Abs(axisA.y) < 0.05f && Mathf.Abs(axisB.y) < 0.05f && axisA.sqrMagnitude > 1f && axisB.sqrMagnitude > 1f;
        }

        private int DisableWaterColliders(Bounds surface, int waterLayer)
        {
            int count = 0;
            foreach (var collider in FindObjectsOfType<Collider>())
            {
                if (collider == null || !collider.enabled || collider.gameObject.layer != waterLayer)
                {
                    continue;
                }

                // The surface's own water collider, or one at the same height lying mostly inside it
                Bounds b = collider.bounds;
                float overlapX = Mathf.Min(b.max.x, surface.max.x) - Mathf.Max(b.min.x, surface.min.x);
                float overlapZ = Mathf.Min(b.max.z, surface.max.z) - Mathf.Max(b.min.z, surface.min.z);
                float area = Mathf.Max(0.01f, b.size.x * b.size.z);
                bool inside = overlapX > 0f && overlapZ > 0f && overlapX * overlapZ / area >= 0.8f
                    && Mathf.Abs(b.max.y - surface.max.y) < 0.15f;
                if (inside)
                {
                    collider.enabled = false;
                    _disabledWaterColliders.Add(collider);
                    count++;
                }
            }

            return count;
        }

        private void BuildIceSurface(GameObject root, Vector3 origin, Vector3 axisA, Vector3 axisB, Material material)
        {
            // Up-facing quad, split into a grid so lighting and fog are evaluated across it
            const int cells = 16;
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            for (int j = 0; j <= cells; j++)
            {
                for (int i = 0; i <= cells; i++)
                {
                    Vector3 p = origin + axisA * (i / (float)cells) + axisB * (j / (float)cells);
                    vertices.Add(p);
                    uvs.Add(new Vector2(p.x / IceTextureMetres, p.z / IceTextureMetres));
                    normals.Add(Vector3.up);
                }
            }

            // Unity's front face for triangle (p0, p1, p2) faces along Cross(p1 - p0, p2 - p0)
            bool upward = Vector3.Cross(axisA, axisB).y > 0f;
            var triangles = new List<int>();
            for (int j = 0; j < cells; j++)
            {
                for (int i = 0; i < cells; i++)
                {
                    int a = j * (cells + 1) + i, b = a + 1, c = a + cells + 1, d = c + 1;
                    if (upward)
                    {
                        triangles.AddRange(new[] { a, b, c, b, d, c });
                    }
                    else
                    {
                        triangles.AddRange(new[] { a, c, b, b, c, d });
                    }
                }
            }

            var mesh = new Mesh { name = "Winter Outbreak ice" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            _iceAssets.Add(mesh);

            var surface = new GameObject("surface");
            surface.transform.SetParent(root.transform, false);
            surface.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = surface.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            _iceRenderers.Add(renderer);
        }

        private static GameObject IceBoxObject(GameObject root, string name, int layer, Vector3 center, Vector3 axisA, Vector3 axisB)
        {
            var box = new GameObject(name) { layer = layer };
            box.SetActive(false);
            box.transform.SetParent(root.transform, false);
            box.transform.SetPositionAndRotation(center, Quaternion.LookRotation(new Vector3(axisB.x, 0f, axisB.z).normalized, Vector3.up));
            return box;
        }

        private Collider BuildIceBox(GameObject root, string name, int layer, Vector3 origin, Vector3 axisA, Vector3 axisB, bool ballistic)
        {
            Vector3 center = origin + (axisA + axisB) * 0.5f;
            var box = IceBoxObject(root, "Winter Outbreak lake ice " + name, layer, center, axisA, axisB);
            var collider = box.AddComponent<BoxCollider>();
            collider.size = new Vector3(axisA.magnitude, IceThickness, axisB.magnitude);
            collider.center = new Vector3(0f, -IceThickness * 0.5f, 0f);
            if (ballistic)
            {
                AddIceBallistic(box);
            }

            box.SetActive(true);
            if (ballistic && box.TryGetComponent<BaseBallistic>(out var surface))
            {
                // Hard steps on bare ice
                surface.SurfaceSound = BaseBallistic.ESurfaceSound.Concrete;
            }

            return collider;
        }

        // Bullets meet the ice like tile
        private void AddIceBallistic(GameObject box)
        {
            var ballistic = box.AddComponent<BallisticCollider>();
            var presets = EFTHardSettings.Instance?.ColliderPresets;
            int index = presets == null ? -1 : Array.FindIndex(presets, p => p != null && p.MaterialType == MaterialType.Tile);
            if (index >= 0)
            {
                presets[index].ApplyPresetValues(ballistic);
                ballistic._presetId = index;
            }
            else
            {
                ballistic.PenetrationLevel = 50f;
                ballistic.PenetrationChance = 0f;
                Logger.LogWarning("Frozen lake: no Tile collider preset found; ice stops bullets without presets.");
            }

            ballistic.TypeOfMaterial = MaterialType.Glass;
        }

        private const byte CellLand = 0, CellIce = 1, CellOutside = 2, CellBackdrop = 3;

        // Opens the lake
        private enum IceWalls
        {
            None, // swamp: inside the map
            MapEdge, // lake: walls where the ice meets ground beyond the map or the far shore
            EveryBank // river: walls along every bank
        }

        // Which of BSG's LevelBorder colliders over the ice come down
        private enum IceBorders
        {
            Disable, // Woods' lake and swamp: every one standing mostly over the ice
            PondOnly, // ponds on other maps: only the pond's own blockers (see DisableLakeBorders)
            Keep // the sea: all stay
        }

        private int BuildIceWalls(GameObject root, Vector3 origin, Vector3 axisA, Vector3 axisB, float waterY, int wallLayer, int waterLayer,
            IceWalls walling, IceBorders borders, float step, out int iceCells, out int outsideCells, out int backdropCells, out int bordersDisabled,
            out FrozenIce.IceField field, out IceGrid grid)
        {
            bool edgeWalls = walling == IceWalls.MapEdge;
            int na = Mathf.Max(1, Mathf.CeilToInt(axisA.magnitude / step));
            int nb = Mathf.Max(1, Mathf.CeilToInt(axisB.magnitude / step));
            var state = new byte[na, nb];
            var groundY = new float[na, nb];
            var onTerrain = new bool[na, nb];
            var hits = new RaycastHit[16];
            int borderLayer = LayerMask.NameToLayer("LevelBorder");
            int mask = Physics.DefaultRaycastLayers & ~(1 << waterLayer) & ~(borderLayer >= 0 ? 1 << borderLayer : 0);
            iceCells = 0;
            outsideCells = 0;
            backdropCells = 0;
            for (int i = 0; i < na; i++)
            {
                for (int j = 0; j < nb; j++)
                {
                    Vector3 p = CellCentre(origin, axisA, axisB, na, nb, i, j);
                    int count = Physics.RaycastNonAlloc(new Vector3(p.x, waterY + 60f, p.z), Vector3.down, hits, 180f, mask,
                        QueryTriggerInteraction.Ignore);
                    float best = float.MaxValue;
                    float y = float.NaN;
                    bool terrain = false;
                    for (int k = 0; k < count; k++)
                    {
                        if (hits[k].distance < best && !hits[k].collider.name.StartsWith("Winter Outbreak", StringComparison.Ordinal))
                        {
                            best = hits[k].distance;
                            y = hits[k].point.y;
                            terrain = hits[k].collider is TerrainCollider;
                        }
                    }

                    groundY[i, j] = y;
                    onTerrain[i, j] = terrain;
                    if (float.IsNaN(y) || y < waterY - 60f)
                    {
                        state[i, j] = CellOutside;
                        outsideCells++;
                    }
                    else if (y < waterY - 0.02f)
                    {
                        state[i, j] = CellIce;
                        iceCells++;
                    }
                }
            }

            grid = new IceGrid { CellsA = na, CellsB = nb, State = state, GroundY = groundY, OnTerrain = onTerrain, WaterY = waterY };

            // Land beside the ice with no navmesh nearby is backdrop (only where the edge gets walls)
            for (int i = 0; i < na && edgeWalls; i++)
            {
                for (int j = 0; j < nb; j++)
                {
                    if (state[i, j] != CellLand || !TouchesIce(state, i, j, na, nb))
                    {
                        continue;
                    }

                    Vector3 p = CellCentre(origin, axisA, axisB, na, nb, i, j);
                    if (!NavMesh.SamplePosition(new Vector3(p.x, groundY[i, j], p.z), out _, BackdropNavReach, NavMesh.AllAreas))
                    {
                        state[i, j] = CellBackdrop;
                        backdropCells++;
                    }
                }
            }

            // The river is the map's edge
            bool keep = walling == IceWalls.EveryBank || borders == IceBorders.Keep
                || (borders == IceBorders.PondOnly && outsideCells > 0);
            bordersDisabled = keep ? 0 : DisableLakeBorders(origin, axisA, axisB, na, nb, state, waterY, borderLayer,
                borders == IceBorders.PondOnly);
            field = BuildShoreField(origin, axisA, axisB, na, nb, state, onTerrain, waterY);
            if (walling == IceWalls.None)
            {
                return 0;
            }

            bool everyBank = walling == IceWalls.EveryBank;

            int walls = 0;
            Vector3 stepA = axisA / na, stepB = axisB / nb;
            // Edges between (i,j) and (i+1,j): lines of constant A, runs along B
            for (int i = 0; i + 1 < na; i++)
            {
                int start = -1;
                float top = waterY;
                for (int j = 0; j <= nb; j++)
                {
                    bool edge = j < nb && IsIceEdge(state[i, j], state[i + 1, j], everyBank);
                    if (edge)
                    {
                        top = Mathf.Max(top, EdgeTop(groundY[i, j], groundY[i + 1, j], waterY));
                        if (start < 0)
                        {
                            start = j;
                        }
                    }
                    else if (start >= 0)
                    {
                        AddWall(root, origin + stepA * (i + 1) + stepB * start, stepB * (j - start), waterY, top, wallLayer);
                        walls++;
                        start = -1;
                        top = waterY;
                    }
                }
            }

            // Edges between (i,j) and (i,j+1): runs along A
            for (int j = 0; j + 1 < nb; j++)
            {
                int start = -1;
                float top = waterY;
                for (int i = 0; i <= na; i++)
                {
                    bool edge = i < na && IsIceEdge(state[i, j], state[i, j + 1], everyBank);
                    if (edge)
                    {
                        top = Mathf.Max(top, EdgeTop(groundY[i, j], groundY[i, j + 1], waterY));
                        if (start < 0)
                        {
                            start = i;
                        }
                    }
                    else if (start >= 0)
                    {
                        AddWall(root, origin + stepB * (j + 1) + stepA * start, stepA * (i - start), waterY, top, wallLayer);
                        walls++;
                        start = -1;
                        top = waterY;
                    }
                }
            }

            return walls;
        }

        private sealed class IceGrid
        {
            public int CellsA;
            public int CellsB;
            public byte[,] State;
            public float[,] GroundY;
            public bool[,] OnTerrain;
            public float WaterY;
        }

        // Cells the swamp's ice colliders cover
        private static bool[,] IceColliderCells(IceGrid grid)
        {
            const int reach = 3;
            var cover = new bool[grid.CellsA, grid.CellsB];
            for (int i = 0; i < grid.CellsA; i++)
            {
                for (int j = 0; j < grid.CellsB; j++)
                {
                    byte s = grid.State[i, j];
                    float rise = grid.GroundY[i, j] - grid.WaterY;
                    cover[i, j] = s == CellIce
                        || (s == CellLand && !grid.OnTerrain[i, j] && rise < 3f && NearIce(grid, i, j, reach))
                        || (s == CellLand && grid.OnTerrain[i, j] && rise < 1f && TouchesIce(grid.State, i, j, grid.CellsA, grid.CellsB));
                }
            }

            return cover;
        }

        private static bool NearIce(IceGrid grid, int i, int j, int reach)
        {
            for (int a = Mathf.Max(0, i - reach); a <= Mathf.Min(grid.CellsA - 1, i + reach); a++)
            {
                for (int b = Mathf.Max(0, j - reach); b <= Mathf.Min(grid.CellsB - 1, j + reach); b++)
                {
                    if (grid.State[a, b] == CellIce)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Merges marked cells into rectangles
        private static List<RectInt> MergeCells(bool[,] cells, int na, int nb)
        {
            var rects = new List<RectInt>();
            var open = new Dictionary<long, RectInt>();
            for (int j = 0; j <= nb; j++)
            {
                var runs = new HashSet<long>();
                for (int i = 0; j < nb && i < na; i++)
                {
                    if (!cells[i, j])
                    {
                        continue;
                    }

                    int start = i;
                    while (i + 1 < na && cells[i + 1, j])
                    {
                        i++;
                    }

                    long key = ((long)start << 32) | (uint)i;
                    runs.Add(key);
                    open[key] = open.TryGetValue(key, out var rect)
                        ? new RectInt(rect.x, rect.y, rect.width, rect.height + 1)
                        : new RectInt(start, j, i - start + 1, 1);
                }

                foreach (long key in open.Keys.ToList())
                {
                    if (!runs.Contains(key))
                    {
                        rects.Add(open[key]);
                        open.Remove(key);
                    }
                }
            }

            return rects;
        }

        private static Vector3 RectCentre(Vector3 origin, Vector3 axisA, Vector3 axisB, IceGrid grid, RectInt rect)
            => origin + axisA * ((rect.x + rect.width * 0.5f) / grid.CellsA) + axisB * ((rect.y + rect.height * 0.5f) / grid.CellsB);

        // Walk and bullet colliders over the swamp's water only, merged into strips
        private int BuildIceStrips(GameObject root, Vector3 origin, Vector3 axisA, Vector3 axisB, IceGrid grid,
            FrozenIce.IceField field, int lowPoly, int highPoly, bool walkable, out Transform walkRoot)
        {
            var rects = MergeCells(IceColliderCells(grid), grid.CellsA, grid.CellsB);
            field.CellFloors = new Collider[grid.CellsA * grid.CellsB];

            // The walkable strips share one parent, which the bot navmesh build marks as its only walkable source
            var walk = new GameObject("walk");
            walk.transform.SetParent(root.transform, false);
            walkRoot = walkable ? walk.transform : null;
            foreach (var rect in rects)
            {
                Vector3 cornerA = axisA * (rect.width / (float)grid.CellsA);
                Vector3 cornerB = axisB * (rect.height / (float)grid.CellsB);
                Vector3 start = origin + axisA * (rect.x / (float)grid.CellsA) + axisB * (rect.y / (float)grid.CellsB);
                if (walkable)
                {
                    BuildIceBox(walk, "walk", lowPoly, start, cornerA, cornerB, false);
                }

                Collider floor = BuildIceBox(root, "ballistic", highPoly, start, cornerA, cornerB, true);
                for (int i = rect.x; i < rect.xMax; i++)
                {
                    for (int j = rect.y; j < rect.yMax; j++)
                    {
                        field.CellFloors[j * grid.CellsA + i] = floor;
                    }
                }
            }

            return rects.Count;
        }

        // Bots walk the pond edges and islands on navmesh that partly lies on the bed under the water
        private static int CarveDeepIce(GameObject root, Vector3 origin, Vector3 axisA, Vector3 axisB, IceGrid grid, float waterY,
            float minDepth)
        {
            var deep = new bool[grid.CellsA, grid.CellsB];
            for (int i = 0; i < grid.CellsA; i++)
            {
                for (int j = 0; j < grid.CellsB; j++)
                {
                    if (grid.State[i, j] != CellIce || grid.GroundY[i, j] >= waterY - minDepth)
                    {
                        continue;
                    }

                    Vector3 bed = CellCentre(origin, axisA, axisB, grid.CellsA, grid.CellsB, i, j);
                    bed.y = grid.GroundY[i, j];
                    deep[i, j] = NavMesh.SamplePosition(bed, out var hit, 0.8f, NavMesh.AllAreas) && hit.position.y < waterY - minDepth;
                }
            }

            var rects = MergeCells(deep, grid.CellsA, grid.CellsB);
            foreach (var rect in rects)
            {
                Vector3 centre = RectCentre(origin, axisA, axisB, grid, rect);
                var carver = new GameObject("Winter Outbreak ice navmesh carver");
                carver.transform.SetParent(root.transform, false);
                // Top 0.4 m under the ice
                carver.transform.SetPositionAndRotation(new Vector3(centre.x, waterY - 3.4f, centre.z),
                    Quaternion.LookRotation(new Vector3(axisB.x, 0f, axisB.z).normalized, Vector3.up));
                var obstacle = carver.AddComponent<NavMeshObstacle>();
                obstacle.shape = NavMeshObstacleShape.Box;
                obstacle.size = new Vector3(axisA.magnitude * rect.width / grid.CellsA, 6f, axisB.magnitude * rect.height / grid.CellsB);
                obstacle.carveOnlyStationary = true;
                obstacle.carving = true;
            }

            return rects.Count;
        }

        private static Vector3 CellCentre(Vector3 origin, Vector3 axisA, Vector3 axisB, int na, int nb, int i, int j)
            => origin + axisA * ((i + 0.5f) / na) + axisB * ((j + 0.5f) / nb);

        private static bool TouchesIce(byte[,] state, int i, int j, int na, int nb)
            => (i > 0 && state[i - 1, j] == CellIce) || (i + 1 < na && state[i + 1, j] == CellIce)
                || (j > 0 && state[i, j - 1] == CellIce) || (j + 1 < nb && state[i, j + 1] == CellIce);

        private static bool IsIceEdge(byte a, byte b, bool everyBank)
        {
            bool wallA = a == CellOutside || a == CellBackdrop || (everyBank && a == CellLand);
            bool wallB = b == CellOutside || b == CellBackdrop || (everyBank && b == CellLand);
            return (a == CellIce && wallB) || (b == CellIce && wallA);
        }

        // Wall top
        private static float EdgeTop(float a, float b, float waterY)
        {
            float top = waterY;
            if (!float.IsNaN(a) && a > top && a < waterY + 40f)
            {
                top = a;
            }

            if (!float.IsNaN(b) && b > top && b < waterY + 40f)
            {
                top = b;
            }

            return top + IceWallHeight;
        }

        // BSG LevelBorder colliders standing mostly over ice are switched off
        private int DisableLakeBorders(Vector3 origin, Vector3 axisA, Vector3 axisB, int na, int nb, byte[,] state, float waterY, int borderLayer,
            bool pondOnly)
        {
            if (borderLayer < 0)
            {
                return 0;
            }

            Vector3 dirA = axisA / axisA.sqrMagnitude, dirB = axisB / axisB.sqrMagnitude;
            var area = new Bounds(origin + (axisA + axisB) * 0.5f,
                new Vector3(Mathf.Abs(axisA.x) + Mathf.Abs(axisB.x), 400f, Mathf.Abs(axisA.z) + Mathf.Abs(axisB.z)));
            var heights = new[] { waterY - 0.2f, waterY + 1f };
            var names = new List<string>();
            var kept = new List<string>();
            int disabled = 0;
            foreach (var collider in FindObjectsOfType<Collider>())
            {
                if (collider == null || !collider.enabled || collider.isTrigger || collider.gameObject.layer != borderLayer
                    || collider.name.StartsWith("Winter Outbreak", StringComparison.Ordinal) || !collider.bounds.Intersects(area))
                {
                    continue;
                }

                Bounds b = collider.bounds;
                if (pondOnly && (b.min.x < area.min.x - 10f || b.max.x > area.max.x + 10f
                    || b.min.z < area.min.z - 10f || b.max.z > area.max.z + 10f))
                {
                    kept.Add(collider.name);
                    continue;
                }

                int inside = 0, overIce = 0;
                for (float x = b.min.x + 0.5f; x < b.max.x; x += 1f)
                {
                    for (float z = b.min.z + 0.5f; z < b.max.z; z += 1f)
                    {
                        bool contained = false;
                        foreach (float y in heights)
                        {
                            var p = new Vector3(x, y, z);
                            if ((collider.ClosestPoint(p) - p).sqrMagnitude < 0.0001f)
                            {
                                contained = true;
                                break;
                            }
                        }

                        if (!contained)
                        {
                            continue;
                        }

                        inside++;
                        float dx = x - origin.x, dz = z - origin.z;
                        int i = Mathf.FloorToInt((dx * dirA.x + dz * dirA.z) * na);
                        int j = Mathf.FloorToInt((dx * dirB.x + dz * dirB.z) * nb);
                        if (i >= 0 && i < na && j >= 0 && j < nb && state[i, j] == CellIce)
                        {
                            overIce++;
                        }
                    }
                }

                if (inside > 0 && overIce * 4 >= inside)
                {
                    collider.enabled = false;
                    _disabledWaterColliders.Add(collider);
                    disabled++;
                    names.Add($"{collider.name} {overIce}/{inside}");
                }
            }

            return disabled;
        }

        // Woods
        private const string WoodsNoWalkLine = "-36.6,351.2; 47.7,288.8; 217.4,361.3 | "
            + "-36.6,351.2; -44.4,319.6; -47.0,284.4; -42.7,249.3; -34.9,221.1; 4.3,207.1; 56.5,212.3; "
            + "104.3,221.1; 160.9,266.8; 211.3,310.8; 217.4,361.3";
        private const float NoWalkSegment = 4f;

        // Invisible walls along [Frozen Lake] NoWalkLine
        private int BuildNoWalkLine(GameObject root, int wallLayer, int waterLayer)
        {
            string setting = (Ice.NoWalkLine ?? "").Trim();
            if (setting.Length == 0)
            {
                setting = string.Equals(_raid?.LocationId, "woods", StringComparison.OrdinalIgnoreCase) ? WoodsNoWalkLine : "";
            }

            if (root == null || FrozenIce.Fields.Count == 0 || setting.Length == 0
                || string.Equals(setting, "none", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            int border = LayerMask.NameToLayer("LevelBorder");
            int mask = Physics.DefaultRaycastLayers & ~(1 << waterLayer) & ~(border >= 0 ? 1 << border : 0);
            int segments = 0;
            int lines = 0;

            // Several lines are separated by '|'
            foreach (string line in setting.Split('|'))
            {
                var points = new List<Vector2>();
                foreach (string pair in line.Split(';'))
                {
                    string[] parts = pair.Split(',');
                    if (parts.Length == 2
                        && float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, Inv, out float x)
                        && float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, Inv, out float z))
                    {
                        points.Add(new Vector2(x, z));
                    }
                }

                if (points.Count < 2)
                {
                    Logger.LogWarning($"Frozen lake: a NoWalkLine line needs at least two 'x,z' points; got '{line.Trim()}'.");
                    continue;
                }

                points = SmoothLine(points);

                // The line's height comes from the frozen surface it crosses (the lake at y -16.6, not the swamp's 7.4)
                float waterY = FrozenIce.Fields[0].Top;
                foreach (var field in FrozenIce.Fields)
                {
                    if (points.Any(p => field.TrySample(p.x, p.y, out float distance) && distance > 0f))
                    {
                        waterY = field.Top;
                        break;
                    }
                }

                for (int k = 0; k + 1 < points.Count; k++)
                {
                    var start = new Vector3(points[k].x, waterY, points[k].y);
                    var end = new Vector3(points[k + 1].x, waterY, points[k + 1].y);
                    Vector3 run = end - start;
                    float highest = waterY;
                    int samples = Mathf.Max(2, Mathf.CeilToInt(run.magnitude / 2f) + 1);
                    for (int n = 0; n < samples; n++)
                    {
                        Vector3 p = Vector3.Lerp(start, end, n / (float)(samples - 1));
                        foreach (var hit in Physics.RaycastAll(new Vector3(p.x, waterY + 80f, p.z), Vector3.down, 120f, mask,
                            QueryTriggerInteraction.Ignore))
                        {
                            if (!hit.collider.name.StartsWith("Winter Outbreak", StringComparison.Ordinal))
                            {
                                highest = Mathf.Max(highest, hit.point.y);
                            }
                        }
                    }

                    if (run.sqrMagnitude > 0.01f)
                    {
                        AddWall(root, start, run, waterY, Mathf.Min(highest, waterY + 40f) + IceWallHeight, wallLayer);
                        segments++;
                    }
                }

                lines++;
            }

            return segments;
        }

        // A Catmull-Rom curve through the points, cut into pieces about NoWalkSegment long, so the line bends smoothly
        // instead of meeting at sharp corners
        private static List<Vector2> SmoothLine(List<Vector2> points)
        {
            if (points.Count < 3)
            {
                return points;
            }

            var smooth = new List<Vector2> { points[0] };
            for (int k = 0; k + 1 < points.Count; k++)
            {
                Vector2 p0 = points[Mathf.Max(0, k - 1)];
                Vector2 p1 = points[k];
                Vector2 p2 = points[k + 1];
                Vector2 p3 = points[Mathf.Min(points.Count - 1, k + 2)];
                int pieces = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(p1, p2) / NoWalkSegment));
                for (int n = 1; n <= pieces; n++)
                {
                    float t = n / (float)pieces;
                    float t2 = t * t, t3 = t2 * t;
                    smooth.Add(0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                        + (3f * p1 - p0 - 3f * p2 + p3) * t3));
                }
            }

            return smooth;
        }

        private static void AddWall(GameObject root, Vector3 start, Vector3 run, float waterY, float top, int layer)
        {
            var wall = new GameObject("Winter Outbreak ice wall") { layer = layer };
            wall.transform.SetParent(root.transform, false);
            Vector3 mid = start + run * 0.5f;
            float bottom = waterY - 0.5f;
            wall.transform.SetPositionAndRotation(new Vector3(mid.x, (bottom + top) * 0.5f, mid.z),
                Quaternion.LookRotation(new Vector3(run.x, 0f, run.z).normalized, Vector3.up));
            var box = wall.AddComponent<BoxCollider>();
            box.size = new Vector3(0.4f, top - bottom, run.magnitude + 0.4f);
        }

        private static readonly int IceParamsId = Shader.PropertyToID("_IceParams");
        private static string _iceShaderStatus = "not loaded";

        // Metres from each ice cell to the nearest land or backdrop cell (two-pass chamfer, 1 / 1.414 cell steps),
        // minus half a cell so a cell beside the shore reads about 0.75 m
        private static FrozenIce.IceField BuildShoreField(Vector3 origin, Vector3 axisA, Vector3 axisB, int na, int nb, byte[,] state,
            bool[,] onTerrain, float waterY)
        {
            const float diagonal = 1.41421356f;
            var d = new float[na * nb];
            for (int j = 0; j < nb; j++)
            {
                for (int i = 0; i < na; i++)
                {
                    // Only terrain (and the far shore) counts as shore
                    byte s = state[i, j];
                    bool shore = (s == CellLand && onTerrain[i, j]) || s == CellBackdrop;
                    d[j * na + i] = shore ? 0f : 1e6f;
                }
            }

            for (int j = 0; j < nb; j++)
            {
                for (int i = 0; i < na; i++)
                {
                    float v = d[j * na + i];
                    if (i > 0) v = Mathf.Min(v, d[j * na + i - 1] + 1f);
                    if (j > 0) v = Mathf.Min(v, d[(j - 1) * na + i] + 1f);
                    if (i > 0 && j > 0) v = Mathf.Min(v, d[(j - 1) * na + i - 1] + diagonal);
                    if (i + 1 < na && j > 0) v = Mathf.Min(v, d[(j - 1) * na + i + 1] + diagonal);
                    d[j * na + i] = v;
                }
            }

            for (int j = nb - 1; j >= 0; j--)
            {
                for (int i = na - 1; i >= 0; i--)
                {
                    float v = d[j * na + i];
                    if (i + 1 < na) v = Mathf.Min(v, d[j * na + i + 1] + 1f);
                    if (j + 1 < nb) v = Mathf.Min(v, d[(j + 1) * na + i] + 1f);
                    if (i + 1 < na && j + 1 < nb) v = Mathf.Min(v, d[(j + 1) * na + i + 1] + diagonal);
                    if (i > 0 && j + 1 < nb) v = Mathf.Min(v, d[(j + 1) * na + i - 1] + diagonal);
                    d[j * na + i] = v;
                }
            }

            float cell = 0.5f * (axisA.magnitude / na + axisB.magnitude / nb);
            for (int k = 0; k < d.Length; k++)
            {
                d[k] = d[k] <= 0f ? 0f : Mathf.Min(500f, Mathf.Max(0f, (d[k] - 0.5f) * cell));
            }

            return new FrozenIce.IceField
            {
                Origin = new Vector2(origin.x, origin.z),
                InvA = new Vector2(axisA.x, axisA.z) / axisA.sqrMagnitude,
                InvB = new Vector2(axisB.x, axisB.z) / axisB.sqrMagnitude,
                CellsA = na,
                CellsB = nb,
                Distance = d,
                Top = waterY
            };
        }

        private static bool LoadIceShader()
        {
            if (_iceShaderLoadAttempted)
            {
                return _iceShader != null;
            }

            _iceShaderLoadAttempted = true;
            string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(WinterOutbreakPlugin).Assembly.Location),
                "Resources", IceBundleFile);
            if (!System.IO.File.Exists(path))
            {
                _iceShaderStatus = IceBundleFile + " not found";
                return false;
            }

            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                _iceShaderStatus = IceBundleFile + " could not be loaded";
                return false;
            }

            // The bundle stays loaded for the session
            _iceShader = bundle.LoadAsset<Shader>(IceShaderPath);
            if (_iceShader == null || !_iceShader.isSupported)
            {
                _iceShaderStatus = "ice shader missing or unsupported on this GPU";
                _iceShader = null;
                return false;
            }

            int missing = 0;
            for (int k = 0; k < IceTexturePaths.GetLength(0); k++)
            {
                var texture = bundle.LoadAsset<Texture2D>(IceTexturePaths[k, 1]);
                if (texture != null)
                {
                    _iceTextures[IceTexturePaths[k, 0]] = texture;
                }
                else
                {
                    missing++;
                }
            }

            _iceShaderStatus = missing == 0 ? "loaded" : $"loaded, {missing} ice textures missing";
            return true;
        }

        private Material CreateIceMaterial(FrozenIce.IceField field, out bool custom)
        {
            custom = LoadIceShader();
            if (!custom)
            {
                return CreateFallbackIceMaterial();
            }

            var texture = new Texture2D(field.CellsA, field.CellsB, TextureFormat.RFloat, false, true)
            {
                name = "Winter Outbreak ice shore",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            WriteShoreTexture(texture, field);
            _iceShoreTextures.Add(new KeyValuePair<FrozenIce.IceField, Texture2D>(field, texture));
            var material = new Material(_iceShader) { name = "Winter Outbreak lake ice" };
            foreach (var pair in _iceTextures)
            {
                material.SetTexture(pair.Key, pair.Value);
            }

            material.SetTexture("_ShoreTex", texture);
            material.SetVector("_Frame0", new Vector4(field.Origin.x, field.Origin.y, field.InvA.x, field.InvA.y));
            material.SetVector("_Frame1", new Vector4(field.InvB.x, field.InvB.y, 0f, 0f));
            material.SetVector(IceParamsId, new Vector4(0.5f, FrozenIce.ShoreReach, IceSprinkles, IceDeepReach));
            material.SetVector("_Relief", new Vector4(Mathf.Clamp01(_iceRelief.Value), 7f, 0.8f, 0.04f));
            material.SetColor(SnowColourId, IceSnowColour);
            _iceAssets.Add(texture);
            _iceAssets.Add(material);
            _iceMaterials.Add(material);
            return material;
        }

        // Kept readable (no makeNoLongerReadable) so overlapping surfaces can rewrite it after joining their shores
        private static void WriteShoreTexture(Texture2D texture, FrozenIce.IceField field)
        {
            var pixels = new Color[field.Distance.Length];
            for (int k = 0; k < pixels.Length; k++)
            {
                pixels[k] = new Color(field.Distance[k], 0f, 0f, 1f);
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
        }

        // Neighbouring water quads overlap where one pond runs into the next (Shoreline's garden ponds, Woods' river
        // pieces)
        private int JoinOverlappingShores()
        {
            var fields = FrozenIce.Fields;
            var changed = new HashSet<FrozenIce.IceField>();

            for (int round = 0; round < 3; round++)
            {
                bool any = false;
                foreach (var field in fields)
                {
                    bool lowered = false;
                    foreach (var other in fields)
                    {
                        if (other != field && Mathf.Abs(other.Top - field.Top) < 0.1f && TakeShorterDistances(field, other))
                        {
                            lowered = true;
                        }
                    }

                    if (lowered)
                    {
                        SpreadShoreDistances(field);
                        changed.Add(field);
                        any = true;
                    }
                }

                if (!any)
                {
                    break;
                }
            }

            foreach (var pair in _iceShoreTextures)
            {
                if (changed.Contains(pair.Key) && pair.Value != null)
                {
                    WriteShoreTexture(pair.Value, pair.Key);
                }
            }

            return changed.Count;
        }

        private static bool TakeShorterDistances(FrozenIce.IceField field, FrozenIce.IceField other)
        {
            Vector2 axisA = field.InvA / field.InvA.sqrMagnitude;
            Vector2 axisB = field.InvB / field.InvB.sqrMagnitude;
            bool lowered = false;

            for (int j = 0; j < field.CellsB; j++)
            {
                for (int i = 0; i < field.CellsA; i++)
                {
                    Vector2 p = field.Origin + axisA * ((i + 0.5f) / field.CellsA) + axisB * ((j + 0.5f) / field.CellsB);
                    int k = j * field.CellsA + i;
                    if (other.TrySample(p.x, p.y, out float distance) && distance < field.Distance[k] - 0.25f)
                    {
                        field.Distance[k] = distance;
                        lowered = true;
                    }
                }
            }

            return lowered;
        }

        // The same two-pass chamfer as BuildShoreField, on distances already in metres
        private static void SpreadShoreDistances(FrozenIce.IceField field)
        {
            int na = field.CellsA, nb = field.CellsB;
            float stepA = 1f / (field.InvA.magnitude * na);
            float stepB = 1f / (field.InvB.magnitude * nb);
            float diagonal = Mathf.Sqrt(stepA * stepA + stepB * stepB);
            float[] d = field.Distance;

            for (int j = 0; j < nb; j++)
            {
                for (int i = 0; i < na; i++)
                {
                    float v = d[j * na + i];
                    if (i > 0) v = Mathf.Min(v, d[j * na + i - 1] + stepA);
                    if (j > 0) v = Mathf.Min(v, d[(j - 1) * na + i] + stepB);
                    if (i > 0 && j > 0) v = Mathf.Min(v, d[(j - 1) * na + i - 1] + diagonal);
                    if (i + 1 < na && j > 0) v = Mathf.Min(v, d[(j - 1) * na + i + 1] + diagonal);
                    d[j * na + i] = v;
                }
            }

            for (int j = nb - 1; j >= 0; j--)
            {
                for (int i = na - 1; i >= 0; i--)
                {
                    float v = d[j * na + i];
                    if (i + 1 < na) v = Mathf.Min(v, d[j * na + i + 1] + stepA);
                    if (j + 1 < nb) v = Mathf.Min(v, d[(j + 1) * na + i] + stepB);
                    if (i + 1 < na && j + 1 < nb) v = Mathf.Min(v, d[(j + 1) * na + i + 1] + diagonal);
                    if (i > 0 && j + 1 < nb) v = Mathf.Min(v, d[(j + 1) * na + i - 1] + diagonal);
                    d[j * na + i] = v;
                }
            }
        }

        private Material CreateFallbackIceMaterial()
        {
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Specular") ?? Shader.Find("Legacy Shaders/Diffuse");
            var texture = new Texture2D(256, 256, TextureFormat.RGBA32, true) { name = "Winter Outbreak ice", wrapMode = TextureWrapMode.Repeat };
            var pixels = new Color32[256 * 256];
            var random = new System.Random(7);
            // Streaky grey-blue ice with a few pale crack lines
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    float n = Mathf.PerlinNoise(x * 0.02f, y * 0.05f) * 0.6f + Mathf.PerlinNoise(x * 0.09f + 13f, y * 0.09f + 7f) * 0.4f;
                    float v = Mathf.Lerp(0.62f, 0.86f, n);
                    pixels[y * 256 + x] = new Color(v * 0.92f, v * 0.97f, Mathf.Min(1f, v * 1.04f), 1f);
                }
            }

            for (int crack = 0; crack < 14; crack++)
            {
                float x = random.Next(256), y = random.Next(256), angle = (float)(random.NextDouble() * Math.PI * 2);
                int length = 40 + random.Next(120);
                for (int s = 0; s < length; s++)
                {
                    angle += (float)(random.NextDouble() - 0.5) * 0.5f;
                    x += Mathf.Cos(angle);
                    y += Mathf.Sin(angle);
                    int px = ((int)x % 256 + 256) % 256, py = ((int)y % 256 + 256) % 256;
                    pixels[py * 256 + px] = new Color(0.93f, 0.96f, 0.98f, 1f);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true);
            var material = new Material(shader) { name = "Winter Outbreak ice", mainTexture = texture, color = new Color(0.86f, 0.92f, 0.96f) };
            if (material.HasProperty("_Glossiness"))
            {
                material.SetFloat("_Glossiness", 0.72f);
            }

            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", 0f);
            }

            _iceAssets.Add(texture);
            _iceAssets.Add(material);
            return material;
        }

        private static readonly string[] WaterSoundWords = { "water", "lake", "shore", "wave", "river", "splash", "stream" };

        // Lapping water kept playing over the ice
        private void MuteLakeAudio(Bounds surface)
        {
            Bounds area = surface;
            area.Expand(new Vector3(60f, 80f, 60f));
            var found = new List<string>();
            int muted = 0;

            foreach (var source in FindObjectsOfType<AudioSource>(true))
            {
                if (source == null || !area.Contains(source.transform.position))
                {
                    continue;
                }

                string clip = source.clip != null ? source.clip.name : "no clip";
                string path = PathOf(source.transform);
                string lowerPath = path.ToLowerInvariant();
                bool pooled = lowerPath.Contains("pool") || lowerPath.Contains("betteraudio");
                bool water = !pooled && (NamesWater(clip) || NamesWater(path));
                found.Add($"{path} [{clip}, loop {source.loop}, playing {source.isPlaying}{(water ? ", muted" : "")}]");
                if (!water)
                {
                    continue;
                }

                if (source.enabled)
                {
                    source.Stop();
                    source.enabled = false;
                    _mutedWaterAudio.Add(source);
                }

                for (var t = source.transform; t != null; t = t.parent)
                {
                    foreach (var behaviour in t.GetComponents<MonoBehaviour>())
                    {
                        string type = behaviour != null ? behaviour.GetType().Name : "";
                        if (behaviour != null && behaviour.enabled && (type.StartsWith("Ambient", StringComparison.Ordinal)
                            || type.EndsWith("Emitter", StringComparison.Ordinal)))
                        {
                            behaviour.enabled = false;
                            _mutedWaterAudio.Add(behaviour);
                        }
                    }
                }

                muted++;
            }
        }

        private static bool NamesWater(string name)
        {
            string lower = (name ?? "").ToLowerInvariant();
            return WaterSoundWords.Any(word => lower.Contains(word)) && !lower.Contains("flake");
        }

        // EFT paints its winter snow over the whole screen in a pass at BeforeReflections that overwrites the GBuffer
        // (SnowWetRenderer), which buried the ice whatever its shader drew
        private void HookIceRedraw()
        {
            if (_iceRedrawCallback != null || _iceRenderers.Count == 0 || _iceMaterials.Count == 0)
            {
                return;
            }

            _iceRedrawCallback = RedrawIceAfterGameSnow;
            Camera.onPreCull += _iceRedrawCallback;
        }

        private void RedrawIceAfterGameSnow(Camera camera)
        {
            if (camera == null || !(camera.CompareTag("MainCamera") || camera.CompareTag("OpticCamera")))
            {
                return;
            }

            try
            {
                if (_iceRedrawBuffers.TryGetValue(camera, out var buffer))
                {
                    camera.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.BeforeReflections, buffer);
                }
                else
                {
                    buffer = new UnityEngine.Rendering.CommandBuffer { name = "Winter Outbreak ice redraw" };
                    _iceRedrawBuffers.Add(camera, buffer);
                }

                buffer.Clear();
                buffer.SetRenderTarget(camera.allowHDR ? IceTargetsHdr : IceTargetsLdr,
                    UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget);

                // Only the ice this camera can see
                GeometryUtility.CalculateFrustumPlanes(camera, _iceFrustum);
                foreach (var renderer in _iceRenderers)
                {
                    bool seen = renderer != null && renderer.enabled && GeometryUtility.TestPlanesAABB(_iceFrustum, renderer.bounds);
                    Material material = seen ? renderer.sharedMaterial : null;
                    int pass = material != null ? material.FindPass("DEFERRED") : -1;
                    if (pass >= 0)
                    {
                        buffer.DrawRenderer(renderer, material, 0, pass);
                    }
                }

                camera.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.BeforeReflections, buffer);
            }
            catch (Exception error)
            {
                Logger.LogWarning($"Frozen lake: ice redraw stopped after an error, the game's snow will cover the ice: {error.Message}");
                UnhookIceRedraw();
            }
        }

        private void UnhookIceRedraw()
        {
            if (_iceRedrawCallback != null)
            {
                Camera.onPreCull -= _iceRedrawCallback;
                _iceRedrawCallback = null;
            }

            foreach (var pair in _iceRedrawBuffers)
            {
                if (pair.Key != null)
                {
                    pair.Key.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.BeforeReflections, pair.Value);
                }

                pair.Value.Release();
            }

            _iceRedrawBuffers.Clear();
            _iceRenderers.Clear();
        }

        private void ClearFrozenLake()
        {
            if (!_frozenLakeApplied && _iceObjects.Count == 0 && _hiddenWater.Count == 0)
            {
                return;
            }

            UnhookIceRedraw();
            ClearIceNavMesh();
            _seaChunks.Clear();

            foreach (var renderer in _hiddenWater)
            {
                if (renderer != null)
                {
                    renderer.enabled = true;
                }
            }

            foreach (var collider in _disabledWaterColliders)
            {
                if (collider != null)
                {
                    collider.enabled = true;
                }
            }

            foreach (var behaviour in _mutedWaterAudio)
            {
                if (behaviour != null)
                {
                    behaviour.enabled = true;
                }
            }

            foreach (var item in _iceObjects)
            {
                if (item != null)
                {
                    Destroy(item);
                }
            }

            foreach (var asset in _iceAssets)
            {
                if (asset != null)
                {
                    Destroy(asset);
                }
            }

            FrozenIce.Fields.Clear();
            _iceShoreTextures.Clear();
            _iceDeepReach.Clear();
            _iceMaterials.Clear();
            _hiddenWater.Clear();
            _disabledWaterColliders.Clear();
            _mutedWaterAudio.Clear();
            _lakeAudioBounds.Clear();
            _lakeAudioRecheckAt = 0f;
            _iceObjects.Clear();
            _iceAssets.Clear();
            _frozenLakeApplied = false;
            _frozenLakeRetryAt = 0f;
        }
    }
}
