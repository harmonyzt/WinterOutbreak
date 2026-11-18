using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace WinterOutbreak
{
    // Bot navmesh on walkable frozen water
    public sealed partial class WinterOutbreakPlugin
    {
        private const float IceLinkSpacing = 3f;
        private const float IceLinkInland = 2f;
        
        // Point links (width 0)
        private const float IceLinkWidth = 0f;
        
        // Unity joins new navmesh and links on a later update, so the self-test waits a moment after adding them.
        private const float IceNavTestDelay = 3f;
        private const int IceNavTestTargets = 5;

        private Fixed<bool> _iceBotNavMesh;
        private readonly List<IceNavMeshJob> _iceNavJobs = new List<IceNavMeshJob>();
        private readonly List<NavMeshDataInstance> _iceNavInstances = new List<NavMeshDataInstance>();
        private readonly List<NavMeshLinkInstance> _iceNavLinks = new List<NavMeshLinkInstance>();

        private sealed class IceNavMeshJob
        {
            public string Name;
            public NavMeshData Data;
            public AsyncOperation Operation;
            public float WaterY;
            public Bounds Footprint;
            public List<IceLinkCandidate> Links;
            public List<Vector3> TestTargets;
            public Stopwatch Clock;
            public long SetupMs;
            public int Sources;
            public bool Finished;
            public List<Vector3> ShoreEnds;
            public List<Vector3> IceEnds;
            public float TestAt;
            public bool Tested;
            public string Summary;
            public NavMeshDataInstance Instance;
            public bool Readded;
        }

        private struct IceLinkCandidate
        {
            public Vector3 Ice;
            public Vector3 Shore;
        }

        private void BindIceNavMeshSettings()
        {
            // Off: bots still get stuck on the ice (experimental, not a setting).
            _iceBotNavMesh = new Fixed<bool>(false);
        }

        // Starts the background build for one frozen surface. walkRoot holds its walkable ice strips.
        private void StartIceNavMesh(string name, Transform walkRoot, Vector3 origin, Vector3 axisA, Vector3 axisB, IceGrid grid,
            FrozenIce.IceField field)
        {
            if (_iceBotNavMesh == null || !_iceBotNavMesh.Value || walkRoot == null)
            {
                return;
            }

            var clock = Stopwatch.StartNew();
            float waterY = grid.WaterY;
            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);

            // The rectangle's footprint, in a thin band around the ice: enough to see the shore and anything standing
            // on the ice, without voxelising the hills around the ponds.
            Vector3[] corners = { origin, origin + axisA, origin + axisB, origin + axisA + axisB };
            float minX = corners.Min(c => c.x) - 2f, maxX = corners.Max(c => c.x) + 2f;
            float minZ = corners.Min(c => c.z) - 2f, maxZ = corners.Max(c => c.z) + 2f;
            var bounds = new Bounds();
            bounds.SetMinMax(new Vector3(minX, waterY - 2f, minZ), new Vector3(maxX, waterY + 3f, maxZ));

            int mask = LayerMask.GetMask("Terrain", "LowPolyCollider", "DoorLowPolyCollider", "LevelBorder");
            var markups = new List<NavMeshBuildMarkup>
            {
                new NavMeshBuildMarkup { root = walkRoot, overrideArea = true, area = 0 }
            };
            var sources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(bounds, mask, NavMeshCollectGeometry.PhysicsColliders, 1, markups, sources);

            var job = new IceNavMeshJob
            {
                Name = name,
                Data = new NavMeshData(settings.agentTypeID) { name = "Winter Outbreak ice navmesh " + name },
                WaterY = waterY,
                Footprint = bounds,
                Links = FindIceLinks(origin, axisA, axisB, grid),
                TestTargets = PickIceTestTargets(origin, axisA, axisB, grid, field),
                Clock = clock,
                Sources = sources.Count
            };
            job.Operation = NavMeshBuilder.UpdateNavMeshDataAsync(job.Data, settings, sources, bounds);
            job.SetupMs = clock.ElapsedMilliseconds;
            _iceNavJobs.Add(job);
        }

        // Where a pond cell meets shore terrain that bots walk on: the ice end sits one cell into the ice, the shore
        // end on the existing navmesh 2 m inland. Thinned to about one link every IceLinkSpacing metres.
        private static List<IceLinkCandidate> FindIceLinks(Vector3 origin, Vector3 axisA, Vector3 axisB, IceGrid grid)
        {
            var links = new List<IceLinkCandidate>();
            float cell = 0.5f * (axisA.magnitude / grid.CellsA + axisB.magnitude / grid.CellsB);
            int every = Mathf.Max(1, Mathf.RoundToInt(IceLinkSpacing / cell));
            int[,] steps = { { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } };

            for (int i = 0; i < grid.CellsA; i++)
            {
                for (int j = 0; j < grid.CellsB; j++)
                {
                    if (grid.State[i, j] != CellIce || (i + j) % every != 0)
                    {
                        continue;
                    }

                    for (int k = 0; k < 4; k++)
                    {
                        int a = i + steps[k, 0], b = j + steps[k, 1];
                        if (a < 0 || b < 0 || a >= grid.CellsA || b >= grid.CellsB || grid.State[a, b] != CellLand
                            || !grid.OnTerrain[a, b] || grid.GroundY[a, b] - grid.WaterY > 1.5f)
                        {
                            continue;
                        }

                        Vector3 ice = CellCentre(origin, axisA, axisB, grid.CellsA, grid.CellsB, i, j);
                        Vector3 land = CellCentre(origin, axisA, axisB, grid.CellsA, grid.CellsB, a, b);
                        Vector3 outward = new Vector3(land.x - ice.x, 0f, land.z - ice.z).normalized;
                        Vector3 guess = ice + outward * (cell * 0.5f + IceLinkInland);
                        guess.y = grid.GroundY[a, b] + 1f;

                        // The map's own navmesh, on the shore and not on a pond bed under the ice.
                        if (!NavMesh.SamplePosition(guess, out var hit, 2f, NavMesh.AllAreas) || hit.position.y < grid.WaterY - 0.05f
                            || hit.position.y > grid.WaterY + 2.5f)
                        {
                            continue;
                        }

                        ice -= outward * cell * 0.5f;
                        ice.y = grid.WaterY;
                        links.Add(new IceLinkCandidate { Ice = ice, Shore = hit.position });
                        break;
                    }
                }
            }

            return links;
        }

        // A few ice cells far from any shore (the middle of the larger ponds), for the self-test after the build.
        private static List<Vector3> PickIceTestTargets(Vector3 origin, Vector3 axisA, Vector3 axisB, IceGrid grid,
            FrozenIce.IceField field)
        {
            var cells = new List<(float Distance, Vector3 Point)>();
            for (int i = 0; i < grid.CellsA; i++)
            {
                for (int j = 0; j < grid.CellsB; j++)
                {
                    float distance = field.Distance[j * grid.CellsA + i];
                    if (grid.State[i, j] == CellIce && distance > 8f)
                    {
                        Vector3 p = CellCentre(origin, axisA, axisB, grid.CellsA, grid.CellsB, i, j);
                        p.y = grid.WaterY;
                        cells.Add((distance, p));
                    }
                }
            }

            // The deepest point of each pond, roughly: take the furthest cells that are at least 30 m apart.
            var picked = new List<Vector3>();
            foreach (var cell in cells.OrderByDescending(c => c.Distance))
            {
                if (picked.All(p => (p - cell.Point).sqrMagnitude > 900f))
                {
                    picked.Add(cell.Point);
                    if (picked.Count >= IceNavTestTargets)
                    {
                        break;
                    }
                }
            }

            return picked;
        }

        // Called every frame while frozen: adds finished builds, their links, and logs a path self-test.
        private void UpdateIceNavMesh()
        {
            foreach (var job in _iceNavJobs)
            {
                if (job.Finished && !job.Tested && job.ShoreEnds != null && Time.realtimeSinceStartup >= job.TestAt)
                {
                    job.Tested = true;
                }

                // Add it back, with its links re-snapped to the new shore navmesh.
                if (job.Finished && job.Instance.valid == false && job.ShoreEnds != null && !job.Readded)
                {
                    job.Readded = true;
                    job.Instance = NavMesh.AddNavMeshData(job.Data);
                    _iceNavInstances.Add(job.Instance);
                    int readded = AddIceLinks(job, true);
                    job.TestAt = Time.realtimeSinceStartup + IceNavTestDelay;
                    job.Tested = false;
                }

                if (job.Finished || job.Operation == null || !job.Operation.isDone)
                {
                    continue;
                }

                job.Finished = true;
                try
                {
                    job.Instance = NavMesh.AddNavMeshData(job.Data);
                    _iceNavInstances.Add(job.Instance);
                    int added = AddIceLinks(job, false);
                    job.TestAt = Time.realtimeSinceStartup + IceNavTestDelay;
                    job.Summary = $"built in {job.Clock.ElapsedMilliseconds} ms ({job.SetupMs} ms on the main thread, {job.Sources} sources); "
                        + $"{added} of {job.Links.Count} shore links added";
                }
                catch (Exception error)
                {
                    Logger.LogWarning($"Frozen lake: bot navmesh for {job.Name} failed: {error.Message}");
                }
            }
        }

        // Adds the job's shore links.
        private int AddIceLinks(IceNavMeshJob job, bool resnapShore)
        {
            var shoreEnds = new List<Vector3>();
            var iceEnds = new List<Vector3>();
            foreach (var candidate in job.Links)
            {
                if (!NavMesh.SamplePosition(candidate.Ice, out var ice, 1.2f, NavMesh.AllAreas)
                    || Mathf.Abs(ice.position.y - job.WaterY) > 0.15f)
                {
                    continue;
                }

                Vector3 shore = candidate.Shore;
                if (resnapShore)
                {
                    if (!NavMesh.SamplePosition(shore + Vector3.up * 0.5f, out var snapped, 1.5f, NavMesh.AllAreas)
                        || snapped.position.y < job.WaterY - 0.05f)
                    {
                        continue;
                    }

                    shore = snapped.position;
                }

                var link = new NavMeshLinkData
                {
                    startPosition = ice.position,
                    endPosition = shore,
                    width = IceLinkWidth,
                    costModifier = -1f,
                    bidirectional = true,
                    area = 0,
                    agentTypeID = 0
                };
                var instance = NavMesh.AddLink(link);
                if (instance.valid)
                {
                    _iceNavLinks.Add(instance);
                    shoreEnds.Add(shore);
                    iceEnds.Add(ice.position);
                }
            }

            job.ShoreEnds = shoreEnds;
            job.IceEnds = iceEnds;
            return shoreEnds.Count;
        }

        private static string PathText(Vector3 from, Vector3 to)
        {
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(from, to, -1, path))
            {
                return "none";
            }

            float length = 0f;
            for (int i = 1; i < path.corners.Length; i++)
            {
                length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            }

            string status = path.status == NavMeshPathStatus.PathComplete ? "ok"
                : path.status == NavMeshPathStatus.PathPartial ? "partial" : "invalid";
            return FormattableString.Invariant($"{status} {length:F0} m");
        }

        private void ClearIceNavMesh()
        {
            foreach (var link in _iceNavLinks)
            {
                NavMesh.RemoveLink(link);
            }

            foreach (var instance in _iceNavInstances)
            {
                NavMesh.RemoveNavMeshData(instance);
            }

            // A build still running is left to finish on its own; its data is simply never added.
            foreach (var job in _iceNavJobs)
            {
                job.Finished = true;
            }

            _iceNavLinks.Clear();
            _iceNavInstances.Clear();
            _iceNavJobs.Clear();
        }
    }
}
