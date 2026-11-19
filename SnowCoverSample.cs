using System.Collections.Generic;
using UnityEngine;

namespace WinterOutbreak
{
    internal struct SnowCoverSample
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public float Cover;
        public bool Valid => Normal.y > 0f;

        public SnowCoverSample(Vector3 position, Vector3 normal)
        {
            Position = position;
            Normal = normal;
            Cover = 0f;
        }

        public bool Fits(SnowCoverSample neighbor, float tolerance)
        {
            if (!Valid || !neighbor.Valid || Vector3.Dot(Normal, neighbor.Normal) < 0.75f)
            {
                return false;
            }

            Vector3 separation = neighbor.Position - Position;
            return Mathf.Abs(Vector3.Dot(Normal, separation)) <= tolerance
                && Mathf.Abs(Vector3.Dot(neighbor.Normal, separation)) <= tolerance;
        }

        // Both grids reserve a one-point halo around their rendered samples
        public float Feather(SnowCoverSample[] samples, int index, int width, float tolerance)
        {
            if (!Fits(samples[index - 1], tolerance) || !Fits(samples[index + 1], tolerance)
                || !Fits(samples[index - width], tolerance) || !Fits(samples[index + width], tolerance))
            {
                return 0f;
            }

            int diagonals = 0;
            for (int row = -width; row <= width; row += width * 2)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    if (Fits(samples[index + row + side], tolerance))
                    {
                        diagonals++;
                    }
                }
            }

            return Mathf.SmoothStep(0.18f, 1f, diagonals * 0.25f);
        }
    }

    internal static class SnowGrain
    {
        private const int Sides = 6;
        public const int IndicesPerGrain = Sides * 3;

        public static void Add(Vector3 point, Vector3 normal, float radius,
            List<Vector3> vertices, List<Vector3> normals, List<int> triangles)
        {
            Vector3 tangent = Vector3.Cross(normal, Vector3.forward).normalized;
            Vector3 bitangent = Vector3.Cross(normal, tangent);
            int center = vertices.Count;
            vertices.Add(point + normal * (radius * 0.30f));
            normals.Add(normal);

            for (int side = 0; side < Sides; side++)
            {
                float angle = side * (2f * Mathf.PI / Sides);
                Vector3 offset = tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle);
                vertices.Add(point + offset * radius + normal * (radius * 0.08f));
                normals.Add(normal);
                triangles.Add(center);
                triangles.Add(center + 1 + side);
                triangles.Add(center + 1 + (side + 1) % Sides);
            }
        }
    }
}
