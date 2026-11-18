using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    internal sealed class TrackedBootSnow
    {
        private const float HalfLength = 0.18f;
        private const float HalfWidth = 0.095f;
        private const float Lifetime = 20f;
        private const int MaxPrints = 24;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int TintId = Shader.PropertyToID("_TintColor");

        private readonly int _floorMask = LayersMaskController.HighPolyCollider | LayersMaskController.LowPolyColliderLayerMask | LayersMaskController.TerrainMask;
        private readonly List<Print> _prints = new List<Print>();
        private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();
        private readonly Texture2D _texture;
        private readonly Material _material;
        private readonly Mesh _mesh;

        public TrackedBootSnow()
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null || !shader.isSupported)
            {
                shader = GroundSnowDrift.FindSnowParticleShader();
            }

            if (shader == null)
            {
                return;
            }

            _texture = CreateSoleTexture();
            _material = new Material(shader)
            {
                mainTexture = _texture
            };
            _mesh = new Mesh
            {
                name = "Winter Outbreak Tracked Boot Snow"
            };
            _mesh.vertices = new[]
            {
                new Vector3(-HalfWidth, 0f, -HalfLength), new Vector3(-HalfWidth, 0f, HalfLength), new Vector3(HalfWidth, 0f, -HalfLength),
                new Vector3(HalfWidth, 0f, HalfLength)
            };
            _mesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.right, Vector2.one };
            _mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            _mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            _mesh.RecalculateNormals();
        }

        public void LeavePrint(Vector3 foot, Vector3 forward, float strength, float now)
        {
            if (_mesh == null || !Physics.Raycast(foot + Vector3.up * 0.38f, Vector3.down, out var floor, 0.90f, _floorMask, QueryTriggerInteraction.Ignore)
                              || floor.normal.y < 0.65f || floor.point.y > foot.y + 0.18f)
            {
                return;
            }

            Vector3 tangent = Vector3.ProjectOnPlane(forward, floor.normal).normalized;
            Vector3 side = Vector3.Cross(floor.normal, tangent);
            if (tangent.sqrMagnitude < 0.01f || !HasFloorUnder(floor.point + tangent * HalfLength) || !HasFloorUnder(floor.point - tangent * HalfLength)
                || !HasFloorUnder(floor.point + side * HalfWidth) || !HasFloorUnder(floor.point - side * HalfWidth))
            {
                return;
            }

            if (_prints.Count >= MaxPrints)
            {
                Object.Destroy(_prints[0].Renderer.gameObject);
                _prints.RemoveAt(0);
            }

            var printObject = new GameObject("Winter Outbreak Indoor Boot Print");
            printObject.transform.SetPositionAndRotation(floor.point + floor.normal * 0.009f, Quaternion.LookRotation(tangent, floor.normal));
            printObject.AddComponent<MeshFilter>().sharedMesh = _mesh;

            var renderer = printObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            SetOpacity(renderer, strength * 0.65f);
            _prints.Add(new Print(renderer, now + Lifetime, strength * 0.65f));
        }

        private bool HasFloorUnder(Vector3 point)
        {
            // Keep a print on its own stair tread or floor plane.
            return Physics.Raycast(point + Vector3.up * 0.06f, Vector3.down, out var floor, 0.12f, _floorMask, QueryTriggerInteraction.Ignore) && floor.normal.y >= 0.65f;
        }

        public void Update(float now)
        {
            for (int i = _prints.Count - 1; i >= 0; i--)
            {
                Print print = _prints[i];
                float remaining = print.Expires - now;
                if (remaining <= 0f || print.Renderer == null)
                {
                    if (print.Renderer != null)
                    {
                        Object.Destroy(print.Renderer.gameObject);
                    }

                    _prints.RemoveAt(i);
                }
                else if (remaining < 6f)
                {
                    SetOpacity(print.Renderer, print.Opacity * remaining / 6f);
                }
            }
        }

        private void SetOpacity(MeshRenderer renderer, float alpha)
        {
            var color = new Color(0.92f, 0.95f, 0.98f, alpha);
            _properties.SetColor(ColorId, color);
            _properties.SetColor(TintId, color);
            renderer.SetPropertyBlock(_properties);
        }

        private static Texture2D CreateSoleTexture()
        {
            const int width = 48;
            const int height = 72;
            var pixels = new Color32[width * height];
            for (int z = 0; z < height; z++)
            {
                float along = (z + 0.5f) / height * 2f - 1f;
                float soleWidth = Mathf.Lerp(0.57f, 0.86f, Mathf.InverseLerp(-0.7f, 0.6f, along));
                for (int x = 0; x < width; x++)
                {
                    float across = ((x + 0.5f) / width * 2f - 1f) / soleWidth;
                    float edge = Mathf.Clamp01((1f - across * across - along * along) * 4.5f);
                    float grain = Mathf.Lerp(0.30f, 0.95f, Mathf.PerlinNoise(x * 0.43f, z * 0.33f));
                    float tread = Mathf.Lerp(0.35f, 1f, Mathf.InverseLerp(0.2f, 0.5f, Mathf.Repeat((z + Mathf.Abs(across) * 2f) / 7f, 1f)));
                    float alpha = Mathf.SmoothStep(0f, 1f, edge) * grain * tread;
                    pixels[z * width + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
            {
                name = "Winter Outbreak Boot Snow Sole",
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        public void Dispose()
        {
            foreach (var print in _prints)
            {
                if (print.Renderer != null)
                {
                    Object.Destroy(print.Renderer.gameObject);
                }
            }

            Object.Destroy(_mesh);
            Object.Destroy(_material);
            Object.Destroy(_texture);
        }

        private readonly struct Print
        {
            public readonly MeshRenderer Renderer;
            public readonly float Expires;
            public readonly float Opacity;

            public Print(MeshRenderer renderer, float expires, float opacity)
            {
                Renderer = renderer;
                Expires = expires;
                Opacity = opacity;
            }
        }
    }
}
