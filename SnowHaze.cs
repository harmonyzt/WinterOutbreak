using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    /// <summary>
    /// Soft sheets of fine airborne snow around the camera. Each card drifts with the wind, wraps around a box centred
    /// on the camera, and fades by distance so cards never pop. Card opacity follows the travelling pocket field, so
    /// visibility gets worse as a dense cloud passes and recovers after it, instead of a uniform white-out.
    /// </summary>
    internal sealed class SnowHaze
    {
        private static readonly int GeometryMask = LayersMaskController.HighPolyWithTerrainNoGrassMask
            & ~LayersMaskController.TerrainMask;
        private const float HalfLength = 55f;
        private const float HalfWidth = 48f;
        private static readonly int TintId = Shader.PropertyToID("_TintColor");
        private static readonly int TexStId = Shader.PropertyToID("_MainTex_ST");

        private readonly Card[] _cards;
        private readonly Material _material;
        private readonly Texture2D _texture;
        private readonly Mesh _mesh;
        private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();
        private readonly GameObject _root;
        private float _master;
        private float _nextIndoorCheck;
        private bool _placed;

        // A roof or deck this far above a card's middle hides it. Tree canopy does not count.
        private const float RoofReach = 8f;
        private readonly System.Collections.Generic.Dictionary<Collider, bool> _treeColliders =
            new System.Collections.Generic.Dictionary<Collider, bool>();

        public bool IsAvailable => _material != null;

        private sealed class Card
        {
            public MeshRenderer Renderer;
            public Transform Transform;
            public Vector2 Offset; // along, across the prevailing wind, relative to the camera
            public float Width;
            public float Height;
            public float Lift; // metres the card's base sits above the feet
            public float Speed; // fraction of the drifting-snow speed
            public float BaseAlpha;
            public Vector2 UvScale;
            public Vector2 Uv;
            public float Visible; // 0 when the card is indoors or behind a roof
            public float Alpha;
        }

        public SnowHaze(int cardCount)
        {
            Shader shader = GroundSnowDrift.FindSnowParticleShader();
            if (shader == null)
            {
                return;
            }

            _texture = SnowTextures.CreateHazeTexture();
            _material = new Material(shader) { mainTexture = _texture };
            GroundSnowDrift.EnableSoftParticles(_material, 0.12f);
            _mesh = CreateCardMesh();
            _root = new GameObject("Winter Outbreak Snow Haze");
            _cards = new Card[cardCount];

            var random = new System.Random();
            float R(float a, float b) => Mathf.Lerp(a, b, (float)random.NextDouble());
            for (int i = 0; i < cardCount; i++)
            {
                // Alternate low wide streaky sheets and taller diffuse clouds.
                bool low = i % 3 != 2;
                var go = new GameObject("Haze Card");
                go.transform.SetParent(_root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderer.enabled = false;

                _cards[i] = new Card
                {
                    Renderer = renderer,
                    Transform = go.transform,
                    Width = low ? R(22f, 34f) : R(26f, 40f),
                    Height = low ? R(3f, 6f) : R(9f, 16f),
                    Lift = low ? R(-0.8f, -0.2f) : R(-1.5f, -0.5f),
                    Speed = low ? R(0.9f, 1.15f) : R(0.6f, 0.85f),
                    BaseAlpha = low ? R(0.55f, 0.9f) : R(0.4f, 0.7f),
                    UvScale = low ? new Vector2(R(0.9f, 1.4f), R(0.5f, 0.8f)) : new Vector2(R(0.6f, 1f), R(0.7f, 1.1f)),
                    Uv = new Vector2(R(0f, 1f), R(0f, 1f)),
                    Visible = 1f
                };
            }
        }

        private static Mesh CreateCardMesh()
        {
            // A 7x5 grid whose vertex alpha fades every edge, so the card has no visible outline.
            const int nx = 7;
            const int ny = 5;
            var vertices = new Vector3[nx * ny];
            var uvs = new Vector2[nx * ny];
            var colors = new Color[nx * ny];
            for (int y = 0; y < ny; y++)
            {
                for (int x = 0; x < nx; x++)
                {
                    float u = (float)x / (nx - 1);
                    float v = (float)y / (ny - 1);
                    int i = y * nx + x;
                    vertices[i] = new Vector3(u - 0.5f, v, 0f);
                    uvs[i] = new Vector2(u, v);
                    float horizontal = Mathf.Sin(u * Mathf.PI);
                    float vertical = v < 0.3f ? Mathf.SmoothStep(0f, 1f, v / 0.3f) : Mathf.SmoothStep(1f, 0f, (v - 0.3f) / 0.7f);
                    colors[i] = new Color(1f, 1f, 1f, horizontal * vertical);
                }
            }

            var triangles = new int[(nx - 1) * (ny - 1) * 6];
            int t = 0;
            for (int y = 0; y < ny - 1; y++)
            {
                for (int x = 0; x < nx - 1; x++)
                {
                    int i = y * nx + x;
                    triangles[t++] = i;
                    triangles[t++] = i + nx;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + nx;
                    triangles[t++] = i + nx + 1;
                }
            }

            var mesh = new Mesh { name = "Winter Outbreak Haze Card" };
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f));
            return mesh;
        }

        private bool IsUnderRoof(Vector3 middle)
        {
            return Physics.Raycast(middle, Vector3.up, out var hit, RoofReach, GeometryMask, QueryTriggerInteraction.Ignore)
                && !IsTree(hit.collider);
        }

        private bool IsTree(Collider collider)
        {
            if (_treeColliders.TryGetValue(collider, out bool tree))
            {
                return tree;
            }

            for (Transform part = collider.transform; part != null && !tree; part = part.parent)
            {
                string name = part.name;
                tree = name.IndexOf("tree", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("pine", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("spruce", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("birch", System.StringComparison.OrdinalIgnoreCase) >= 0;
            }

            // The haze outlives raids; drop the cache now and then so old map colliders do not pile up.
            if (_treeColliders.Count > 4096)
            {
                _treeColliders.Clear();
            }

            _treeColliders.Add(collider, tree);
            return tree;
        }

        /// <param name="camera">Camera position.</param> <param name="feetY">Player's feet height, used as the local
        /// ground reference.</param> <param name="outdoors">False while the player is indoors; the haze fades
        /// out.</param>
        public void Update(Vector3 camera, float feetY, StormWind wind, float intensity, Color light,
            bool outdoors, float deltaTime)
        {
            if (_material == null)
            {
                return;
            }

            float target = outdoors ? intensity * Mathf.Lerp(0f, 0.5f, wind.Severity)
                * (0.65f + wind.GustAt(camera, camera) * 0.45f + wind.FrontPassing * 0.6f) : 0f;
            _master = Mathf.MoveTowards(_master, target, deltaTime * 0.35f);
            bool active = _master > 0.002f;
            _root.SetActive(active);
            if (!active)
            {
                _placed = false;
                return;
            }

            Vector3 along = wind.Prevailing;
            Vector3 across = Vector3.Cross(Vector3.up, along);
            bool checkIndoor = Time.time >= _nextIndoorCheck;
            if (checkIndoor)
            {
                _nextIndoorCheck = Time.time + 0.4f;
            }

            for (int i = 0; i < _cards.Length; i++)
            {
                var card = _cards[i];
                if (!_placed)
                {
                    card.Offset = new Vector2(wind.Range(-HalfLength, HalfLength), wind.Range(-HalfWidth, HalfWidth));
                }

                card.Offset.x += wind.SnowSpeed * card.Speed * deltaTime;
                card.Offset.y += Vector3.Dot(wind.Direction, across) * wind.SnowSpeed * card.Speed * deltaTime;
                if (card.Offset.x > HalfLength)
                {
                    card.Offset.x -= 2f * HalfLength;
                    card.Offset.y = wind.Range(-HalfWidth, HalfWidth);
                }

                if (Mathf.Abs(card.Offset.y) > HalfWidth)
                {
                    card.Offset.y = -Mathf.Sign(card.Offset.y) * (HalfWidth - 1f);
                }

                Vector3 position = new Vector3(camera.x, feetY + card.Lift, camera.z)
                    + along * card.Offset.x + across * card.Offset.y;
                Vector3 toCard = position - camera;
                toCard.y = 0f;
                float distance = toCard.magnitude;

                if (checkIndoor)
                {
                    // Hide cards whose middle sits inside a building or indoor volume, or under a roof or deck.
                    Vector3 middle = position + Vector3.up * Mathf.Min(card.Height * 0.4f, 3f);
                    card.Visible = SnowSurface.IsInIndoorVolume(middle)
                        || Physics.CheckSphere(middle, 1.2f, GeometryMask, QueryTriggerInteraction.Ignore)
                        || IsUnderRoof(middle) ? 0f : 1f;
                }

                // Near fade keeps the flat card from being noticed; edge fade hides the wrap.
                float fade = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(3f, 11f, distance))
                    * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(HalfLength * 0.6f, HalfLength, Mathf.Abs(card.Offset.x))))
                    * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(HalfWidth * 0.6f, HalfWidth, Mathf.Abs(card.Offset.y))));
                float density = 0.4f + wind.Pocket(position) * 1.05f;
                float alphaTarget = _master * card.BaseAlpha * fade * density * card.Visible;
                card.Alpha = Mathf.MoveTowards(card.Alpha, alphaTarget, deltaTime * 0.6f);

                bool show = card.Alpha > 0.004f && distance > 1f;
                if (card.Renderer.enabled != show)
                {
                    card.Renderer.enabled = show;
                }

                if (!show)
                {
                    continue;
                }

                // Face the camera around the vertical axis only, like a standing sheet of snow.
                Quaternion facing = Quaternion.LookRotation(toCard / distance, Vector3.up);
                card.Transform.SetPositionAndRotation(position, facing);
                card.Transform.localScale = new Vector3(card.Width, card.Height, 1f);

                // Internal churn: scroll the texture with the on-screen wind, plus slow lift.
                Vector3 right = facing * Vector3.right;
                float lateral = Vector3.Dot(wind.Direction, right) * wind.SnowSpeed * 0.45f;
                card.Uv.x -= lateral / card.Width * card.UvScale.x * deltaTime;
                card.Uv.y += (0.02f + wind.Turbulence * 0.05f) * deltaTime;
                card.Uv.x -= Mathf.Floor(card.Uv.x);
                card.Uv.y -= Mathf.Floor(card.Uv.y);

                _properties.SetVector(TexStId, new Vector4(card.UvScale.x, card.UvScale.y, card.Uv.x, card.Uv.y));
                // Legacy particle shaders double the tint, so 0.5 means unchanged.
                Color hazeTint = light;
                _properties.SetColor(TintId, new Color(hazeTint.r * 0.48f, hazeTint.g * 0.48f, hazeTint.b * 0.485f,
                    Mathf.Clamp01(card.Alpha) * 0.5f));
                card.Renderer.SetPropertyBlock(_properties);
            }

            _placed = true;
        }

        public void Dispose()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }

            Object.Destroy(_material);
            Object.Destroy(_texture);
            Object.Destroy(_mesh);
        }
    }
}
