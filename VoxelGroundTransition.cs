using UnityEngine;

namespace WinterOutbreak
{
    internal sealed partial class VoxelSnowArea
    {
        private const int TransitionDivisions = 4;

        private static bool TryGetGroundTransition(Chunk chunk, int index, Column column, Vector4 heights,
            out GroundTransition transition)
        {
            var a = chunk.Corners[index];
            var b = chunk.Corners[index + CornerWidth];
            var c = chunk.Corners[index + 1];
            var d = chunk.Corners[index + CornerWidth + 1];
            if (a.Feather == 1f && b.Feather == 1f && c.Feather == 1f && d.Feather == 1f)
            {
                transition = default;
                return false;
            }

            var support = new Vector4(
                TransitionSupport(column, a, 0.5f, 0.5f), TransitionSupport(column, b, 0.5f, -0.5f),
                TransitionSupport(column, c, -0.5f, 0.5f), TransitionSupport(column, d, -0.5f, -0.5f));
            var feather = new Vector4(a.Feather, b.Feather, c.Feather, d.Feather);
            transition = new GroundTransition(support, heights - support, feather);
            return true;
        }

        private static float TransitionSupport(Column column, SurfaceCorner corner, float x, float z)
        {
            return corner.Split ? ProjectGroundHeight(column, x, z)
                : corner.BaseHeight - Mathf.Lerp(-0.06f, 0.006f, corner.Feather);
        }

        private readonly struct GroundTransition
        {
            private readonly Vector4 _support;
            private readonly Vector4 _lift;
            private readonly Vector4 _feather;

            public GroundTransition(Vector4 support, Vector4 lift, Vector4 feather)
            {
                _support = support;
                _lift = lift;
                _feather = feather;
            }

            public float Height(float u, float v)
            {
                int x = Mathf.Min((int)(u * TransitionDivisions), TransitionDivisions - 1);
                int z = Mathf.Min((int)(v * TransitionDivisions), TransitionDivisions - 1);
                const float step = 1f / TransitionDivisions;

                // Footprint subdivisions use the same underlying triangles along shared edges
                return InterpolateHeight(
                    Sample(x * step, z * step), Sample(x * step, (z + 1) * step),
                    Sample((x + 1) * step, z * step), Sample((x + 1) * step, (z + 1) * step),
                    u * TransitionDivisions - x, v * TransitionDivisions - z);
            }

            public float Sample(float u, float v)
            {
                float smoothU = Mathf.SmoothStep(0f, 1f, u);
                float smoothV = Mathf.SmoothStep(0f, 1f, v);
                float near = Mathf.Lerp(_lift.x, _lift.z, Ease(u, smoothU, _feather.x, _feather.z));
                float far = Mathf.Lerp(_lift.y, _lift.w, Ease(u, smoothU, _feather.y, _feather.w));
                float left = Ease(v, smoothV, _feather.x, _feather.y);
                float right = Ease(v, smoothV, _feather.z, _feather.w);
                float support = InterpolateHeight(_support.x, _support.y, _support.z, _support.w, u, v);
                return support + Mathf.Lerp(near, far, Mathf.Lerp(left, right, u));
            }

            private static float Ease(float linear, float smooth, float a, float b)
            {
                return Mathf.Lerp(smooth, linear, Mathf.Min(a, b));
            }
        }
    }
}
