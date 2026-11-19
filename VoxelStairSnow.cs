using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    // Snow on measured stair flights, drawn tread by tread from the flight's own measurements instead of the 25 cm grid
    internal sealed partial class VoxelSnowArea
    {
        private const float StairSnowMaxThickness = 0.10f;
        private const float StairSnowRebuildDepth = 0.005f;
        private const float StairSnowRebuildInterval = 2f;

        private GameObject _stairSnowObject;
        private Mesh _stairSnowMesh;
        private readonly List<float[]> _stairSnowExposure = new List<float[]>();
        private int _stairSnowFlights = -1;
        private float _stairSnowDepth = -1f;
        private float _nextStairSnowBuild;

        private readonly List<Vector3> _stairVertices = new List<Vector3>();
        private readonly List<int> _stairTriangles = new List<int>();
        private readonly List<Color> _stairColors = new List<Color>();

        // Whether a point lies on one of the flight's treads, within that tread's own width
        private static bool TreadCovers(StairFlight flight, Vector2 point, bool withLandings, float margin = 0f)
        {
            Vector2 offset = point - flight.Origin;
            float s = Vector2.Dot(offset, flight.Dir);
            float t = Vector2.Dot(offset, flight.Side);
            float first = flight.Risers[0];
            float last = flight.Risers[flight.Risers.Length - 1];
            int k;
            if (s > first && s < last)
            {
                k = 0;
                while (k < flight.Heights.Length - 1 && s >= flight.Risers[k + 1])
                {
                    k++;
                }
            }
            else if (withLandings && s > first - StairLandingReach && s <= first)
            {
                k = 0;
            }
            else if (withLandings && s >= last && s < last + StairLandingReach)
            {
                k = flight.Heights.Length - 1;
            }
            else
            {
                return false;
            }

            return t > -TreadLeft(flight, k) - margin && t < TreadRight(flight, k) + margin;
        }

        internal void UpdateStairSnow(float depth)
        {
            if (Time.time < _nextStairSnowBuild)
            {
                return;
            }

            if (_stairFlights.Count == _stairSnowFlights && Mathf.Abs(depth - _stairSnowDepth) < StairSnowRebuildDepth)
            {
                return;
            }

            _nextStairSnowBuild = Time.time + StairSnowRebuildInterval;
            _stairSnowFlights = _stairFlights.Count;
            _stairSnowDepth = depth;

            _stairVertices.Clear();
            _stairTriangles.Clear();
            _stairColors.Clear();
            for (int i = 0; i < _stairFlights.Count; i++)
            {
                if (_stairSnowExposure.Count <= i)
                {
                    _stairSnowExposure.Add(MeasureTreadExposure(_stairFlights[i]));
                }

                AddFlightSnow(_stairFlights[i], _stairSnowExposure[i], depth);
            }

            if (_stairVertices.Count == 0)
            {
                if (_stairSnowObject != null)
                {
                    _stairSnowObject.SetActive(false);
                }

                return;
            }

            if (_stairSnowObject == null)
            {
                _stairSnowMesh = new Mesh { name = "Winter Outbreak Stair Snow Mesh" };
                _stairSnowObject = new GameObject("Winter Outbreak Stair Snow");
                _stairSnowObject.AddComponent<MeshFilter>().sharedMesh = _stairSnowMesh;

                var renderer = _stairSnowObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            _stairSnowObject.SetActive(true);
            _stairSnowMesh.Clear();
            _stairSnowMesh.indexFormat = _stairVertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _stairSnowMesh.SetVertices(_stairVertices);
            _stairSnowMesh.SetTriangles(_stairTriangles, 0);
            _stairSnowMesh.SetColors(_stairColors);
            _stairSnowMesh.RecalculateNormals();
            if (_prints != null)
            {
                // No footprints on the slabs and no growth in the shader: they are rebuilt as the depth changes
                var limits = new List<Vector4>(_stairVertices.Count);
                for (int i = 0; i < _stairVertices.Count; i++)
                {
                    limits.Add(Vector4.zero);
                }

                _stairSnowMesh.SetUVs(3, limits);
                _growthBlock ??= new MaterialPropertyBlock();
                _growthBlock.SetFloat(BuiltDepthId, depth);
                _stairSnowObject.GetComponent<MeshRenderer>().SetPropertyBlock(_growthBlock);
            }

            _stairSnowMesh.RecalculateBounds();
        }

        // How open each tread is
        private float[] MeasureTreadExposure(StairFlight flight)
        {
            var exposure = new float[flight.Heights.Length];
            for (int k = 0; k < flight.Heights.Length; k++)
            {
                float s = (flight.Risers[k] + flight.Risers[k + 1]) * 0.5f;
                int open = 0;
                for (int j = 0; j < 3; j++)
                {
                    float t = j == 0 ? -flight.Left * 0.7f : j == 1 ? 0f : flight.Right * 0.7f;
                    Vector2 point = flight.Origin + flight.Dir * s + flight.Side * t;
                    if (!IsSheltered(new Vector3(point.x, flight.Heights[k] + 0.40f, point.y)))
                    {
                        open++;
                    }
                }

                exposure[k] = open / 3f;
            }

            return exposure;
        }

        private void AddFlightSnow(StairFlight flight, float[] exposure, float depth)
        {
            float full = Mathf.Min(depth * 0.45f, flight.Rise * 0.55f, StairSnowMaxThickness);
            if (full < 0.005f)
            {
                return;
            }

            for (int k = 0; k < flight.Heights.Length; k++)
            {
                float thickness = full * Mathf.Lerp(0.3f, 1f, exposure[k]);
                float front = flight.Risers[k];
                float back = flight.Risers[k + 1];
                float y = flight.Heights[k];

                // Profile along the climb
                var profile = new[]
                {
                    new Vector2(front - 0.015f, y - 0.015f),
                    new Vector2(front - 0.015f, y + thickness * 0.45f),
                    new Vector2(front + thickness * 0.6f, y + thickness),
                    new Vector2(back, y + thickness * 1.2f)
                };

                AddTreadSlab(flight, profile, y, thickness, exposure[k], TreadLeft(flight, k), TreadRight(flight, k));
            }

            // Snow banked against the first riser, meeting the first tread's snow
            float bottom = flight.Bottom;
            float bank = Mathf.Min(depth * 0.8f, flight.Heights[0] - bottom + full);
            if (bank > 0.01f)
            {
                float first = flight.Risers[0];
                var foot = new[]
                {
                    new Vector2(first - 0.45f, bottom - 0.01f),
                    new Vector2(first - 0.25f, bottom + bank * 0.75f),
                    new Vector2(first - 0.015f, bottom + bank)
                };

                AddTreadSlab(flight, foot, bottom, bank, 1f, TreadLeft(flight, 0), TreadRight(flight, 0));
            }
        }

        private static float TreadLeft(StairFlight flight, int k)
        {
            return flight.TreadLeft != null && k < flight.TreadLeft.Length && flight.TreadLeft[k] > 0.1f
                ? flight.TreadLeft[k] : flight.Left;
        }

        private static float TreadRight(StairFlight flight, int k)
        {
            return flight.TreadRight != null && k < flight.TreadRight.Length && flight.TreadRight[k] > 0.1f
                ? flight.TreadRight[k] : flight.Right;
        }

        // Extrudes a profile across the flight's width
        private void AddTreadSlab(StairFlight flight, Vector2[] profile, float floor, float thickness, float exposure,
            float leftWidth, float rightWidth)
        {
            float left = -leftWidth + 0.02f;
            float right = rightWidth - 0.02f;
            float bevel = Mathf.Min(thickness * 0.8f, (right - left) * 0.25f);
            float[] rows = { left, left + bevel, right - bevel, right };
            int start = _stairVertices.Count;
            var color = new Color(1f, 1f, 1f, exposure);

            for (int r = 0; r < rows.Length; r++)
            {
                bool outer = r == 0 || r == rows.Length - 1;
                for (int p = 0; p < profile.Length; p++)
                {
                    Vector2 point = flight.Origin + flight.Dir * profile[p].x + flight.Side * rows[r];
                    float height = outer ? floor - 0.01f : profile[p].y;
                    _stairVertices.Add(new Vector3(point.x, height, point.y));
                    _stairColors.Add(color);
                }
            }

            int count = profile.Length;
            for (int r = 0; r < rows.Length - 1; r++)
            {
                for (int p = 0; p < count - 1; p++)
                {
                    int a = start + r * count + p;
                    int b = a + 1;
                    int c = a + count;
                    int d = c + 1;

                    // Side points left of the climb when looking up the flight, so this winding faces up
                    _stairTriangles.Add(a);
                    _stairTriangles.Add(c);
                    _stairTriangles.Add(b);
                    _stairTriangles.Add(b);
                    _stairTriangles.Add(c);
                    _stairTriangles.Add(d);
                }
            }
        }

        private void DisposeStairSnow()
        {
            if (_stairSnowObject != null)
            {
                Object.Destroy(_stairSnowObject);
            }

            if (_stairSnowMesh != null)
            {
                Object.Destroy(_stairSnowMesh);
            }

            _stairSnowObject = null;
            _stairSnowMesh = null;
            _stairSnowExposure.Clear();
            _stairSnowFlights = -1;
        }
    }
}
