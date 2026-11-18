using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace WinterOutbreak
{
    /// <summary>
    /// GPU print texture for ground snow impressions. Stamps (boots, prone, bodies, craters) are drawn into a
    /// world-space texture that follows the player, and refill there over their own duration. The snow material
    /// (WinterStorms/SnowPrints in snowprints.bundle) presses vertices down by this texture and takes its normals from
    /// it, so prints and their refill never rebuild a mesh. The shader source and its build script live in the
    /// WinterOutbreakShaders Unity project next to this one.
    /// </summary>
    internal sealed class SnowPrintTexture
    {
        public const int KindBoot = 0, KindProne = 1, KindBody = 2, KindBlast = 3, KindTrack = 4,
            KindGrenadeMelt = 5, KindFireMelt = 6, KindItem = 7;
        private const string CoatShaderPath = "assets/shaders/snowcoat.shader";
        private const int ScorchResolution = Resolution / 2;
        // Soot from a grenade blast fades over this long (new snow covers it).
        private const float ScorchSeconds = 420f;

        private const string BundleFile = "snowprints.bundle";
        private const string SnowShaderPath = "assets/shaders/snowprints.shader";
        private const string UpdateShaderPath = "assets/shaders/printupdate.shader";
        private const int Resolution = 2048;
        private const float Texel = 0.03f;
        private const float WorldSize = Resolution * Texel;
        private const float RecenterDistance = 8f;
        private const float EdgeFadeMeters = 4f;
        private const float CompressionScale = 0.5f;
        private const float RateScale = 0.064f;
        private const float UpdateInterval = 0.1f;
        // The player's own prints (crawling, steps) update sooner, so a furrow advances smoothly, not in 10 Hz steps.
        private const float UrgentUpdateInterval = 0.033f;
        private const int MaxStampsPerPass = 16;
        private const float RimScale = 0.1f;
        private const float RimShare = 0.75f;
        private const float MaxRim = 0.06f;

        private static bool _loadAttempted;
        private static Shader _snowShader;
        private static Shader _updateShader;
        private static Shader _coatShader;
        private static readonly int ScorchTexId = Shader.PropertyToID("_WSScorchTex");
        private static readonly int ScorchId = Shader.PropertyToID("_WSScorch");
        private static string _loadStatus;

        private static readonly int PrintTexId = Shader.PropertyToID("_WSPrintTex");
        private static readonly int PrintRectId = Shader.PropertyToID("_WSPrintRect");
        private static readonly int PrintParamsId = Shader.PropertyToID("_WSPrintParams");
        private static readonly int UpdateShiftId = Shader.PropertyToID("_WSUpdateShift");
        private static readonly int UpdateRectId = Shader.PropertyToID("_WSUpdateRect");
        private static readonly int UpdateScaleId = Shader.PropertyToID("_WSUpdateScale");
        private static readonly int StampAId = Shader.PropertyToID("_WSStampA");
        private static readonly int StampBId = Shader.PropertyToID("_WSStampB");
        private static readonly int RimTexId = Shader.PropertyToID("_WSRimTex");
        private static readonly int RimSettingsId = Shader.PropertyToID("_WSRimSettings");

        private readonly Material _updateMaterial;
        private readonly List<Vector4> _pendingA = new List<Vector4>();
        private readonly List<Vector4> _pendingB = new List<Vector4>();
        private readonly Vector4[] _stampA = new Vector4[MaxStampsPerPass];
        private readonly Vector4[] _stampB = new Vector4[MaxStampsPerPass];
        private RenderTexture _read;
        private RenderTexture _write;
        private RenderTexture _rim;
        private RenderTexture _scorchRead;
        private RenderTexture _scorchWrite;
        private Vector2Int _centerTexel;
        private bool _placed;
        private float _lastUpdate;
        private float _liveUntil = float.NegativeInfinity;
        private bool _recentred;
        private bool _urgent;
        private Vector2? _recentredFrom;

        public long Passes { get; private set; }
        public long Stamps { get; private set; }
        public long Recenters { get; private set; }

        private SnowPrintTexture(RenderTextureFormat format)
        {
            _updateMaterial = new Material(_updateShader) { hideFlags = HideFlags.HideAndDontSave };
            _read = CreateTarget(format, "Winter Outbreak Snow Prints A");
            _write = CreateTarget(format, "Winter Outbreak Snow Prints B");
            RenderTextureFormat rimFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R16)
                ? RenderTextureFormat.R16 : RenderTextureFormat.RHalf;
            _rim = CreateTarget(rimFormat, "Winter Outbreak Snow Print Rims");
            RenderTextureFormat scorchFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8)
                ? RenderTextureFormat.R8 : RenderTextureFormat.RHalf;
            _scorchRead = CreateTarget(scorchFormat, "Winter Outbreak Snow Scorch A", ScorchResolution);
            _scorchWrite = CreateTarget(scorchFormat, "Winter Outbreak Snow Scorch B", ScorchResolution);
            Shader.SetGlobalVector(PrintParamsId, Vector4.zero);
        }

        /// <summary>
        /// Rim height scale in metres, for SnowSurfaceShading to pass to the snow shader.
        /// </summary>
        public const float RimHeightScale = RimScale;

        public static SnowPrintTexture TryCreate(out string status)
        {
            if (!LoadShaders())
            {
                status = _loadStatus;
                return null;
            }

            RenderTextureFormat format = RenderTextureFormat.RG32;
            if (!SystemInfo.SupportsRenderTextureFormat(format))
            {
                format = RenderTextureFormat.RGFloat;
                if (!SystemInfo.SupportsRenderTextureFormat(format))
                {
                    status = "GPU snow prints off: no supported two-channel render texture format";
                    return null;
                }
            }

            status = $"GPU snow prints on: {Resolution} x {Resolution} {format} over {WorldSize:F0} m";
            return new SnowPrintTexture(format);
        }

        private static bool LoadShaders()
        {
            if (_loadAttempted)
            {
                return _snowShader != null && _updateShader != null;
            }

            _loadAttempted = true;
            string path = Path.Combine(Path.GetDirectoryName(typeof(SnowPrintTexture).Assembly.Location),
                "Resources", BundleFile);
            if (!File.Exists(path))
            {
                _loadStatus = $"GPU snow prints off: {BundleFile} not found, using CPU prints";
                return false;
            }

            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                _loadStatus = $"GPU snow prints off: {BundleFile} could not be loaded, using CPU prints";
                return false;
            }

            // The bundle stays loaded for the session; the shaders are reused by every raid.
            _snowShader = bundle.LoadAsset<Shader>(SnowShaderPath);
            _updateShader = bundle.LoadAsset<Shader>(UpdateShaderPath);
            _coatShader = bundle.LoadAsset<Shader>(CoatShaderPath);
            if (_snowShader == null || _updateShader == null || !_snowShader.isSupported || !_updateShader.isSupported)
            {
                _loadStatus = "GPU snow prints off: shaders missing or unsupported on this GPU, using CPU prints";
                _snowShader = null;
                _updateShader = null;
                return false;
            }

            return true;
        }

        public Material CreateMaterial()
        {
            var material = new Material(_snowShader) { color = new Color(0.89f, 0.90f, 0.91f) };
            material.SetFloat("_Glossiness", 0.12f);
            return material;
        }

        /// <summary>
        /// Queues a stamp in world XZ. The shape matches SnowImpression.Compression at its start.
        /// </summary>
        public void Queue(Vector2 center, Vector2 forward, float depth, int kind, float blastRadius, float duration,
            bool urgent = false)
        {
            if (depth <= 0f || duration <= 0f)
            {
                return;
            }

            _urgent |= urgent;
            _pendingA.Add(new Vector4(center.x, center.y, forward.x, forward.y));
            _pendingB.Add(new Vector4(depth, kind, blastRadius, 1f / duration));
        }

        public void Update(Vector3 player, float now)
        {
            if (_read == null)
            {
                return;
            }

            bool moved = false;
            Vector2 shift = Vector2.zero;
            var target = new Vector2Int(Mathf.RoundToInt(player.x / Texel), Mathf.RoundToInt(player.z / Texel));
            if (!_placed || (target - _centerTexel).sqrMagnitude > RecenterDistance * RecenterDistance / (Texel * Texel))
            {
                if (_placed)
                {
                    shift = new Vector2(target.x - _centerTexel.x, target.y - _centerTexel.y) / Resolution;
                    Recenters++;
                    _recentredFrom = WindowMin();
                }
                else
                {
                    // Nothing to carry over on the first placement.
                    shift = new Vector2(2f, 2f);
                    _lastUpdate = now;
                    _recentredFrom = null;
                }

                _recentred = true;

                _centerTexel = target;
                _placed = true;
                moved = true;
            }

            // Stamps wait for the next tick, so many bots walking cannot force a pass every frame.
            float elapsed = now - _lastUpdate;
            float interval = _urgent ? UrgentUpdateInterval : UpdateInterval;
            if (!moved && (elapsed < interval || (_pendingA.Count == 0 && now > _liveUntil)))
            {
                return;
            }

            for (int i = 0; i < _pendingB.Count; i++)
            {
                _liveUntil = Mathf.Max(_liveUntil, now + 1f / _pendingB[i].w);
                if (Mathf.Approximately(_pendingB[i].y, KindGrenadeMelt))
                {
                    // Keep updating while soot fades, even after the melt itself has refilled.
                    _liveUntil = Mathf.Max(_liveUntil, now + ScorchSeconds);
                }
            }

            Vector2 min = WindowMin();
            RenderTexture active = RenderTexture.active;
            float decay = Mathf.Clamp(elapsed, 0f, 5f);
            int offset = 0;
            do
            {
                int count = Mathf.Min(MaxStampsPerPass, _pendingA.Count - offset);
                for (int i = 0; i < MaxStampsPerPass; i++)
                {
                    _stampA[i] = i < count ? _pendingA[offset + i] : Vector4.zero;
                    _stampB[i] = i < count ? _pendingB[offset + i] : Vector4.zero;
                }

                _updateMaterial.SetVector(UpdateShiftId, new Vector4(shift.x, shift.y, decay, count));
                _updateMaterial.SetVector(UpdateRectId, new Vector4(min.x, min.y, WorldSize, 0f));
                _updateMaterial.SetVector(UpdateScaleId, new Vector4(CompressionScale, RateScale, 0f, 0f));
                _updateMaterial.SetVectorArray(StampAId, _stampA);
                _updateMaterial.SetVectorArray(StampBId, _stampB);
                Graphics.Blit(_read, _write, _updateMaterial, 0);
                (_read, _write) = (_write, _read);
                // Soot on the same window and stamps, at half resolution.
                _updateMaterial.SetVector(ScorchId, new Vector4(ScorchSeconds, 0f, 0f, 0f));
                Graphics.Blit(_scorchRead, _scorchWrite, _updateMaterial, 2);
                (_scorchRead, _scorchWrite) = (_scorchWrite, _scorchRead);
                Passes++;
                Stamps += count;

                // Later batches of the same update neither move nor refill again.
                shift = Vector2.zero;
                decay = 0f;
                offset += count;
            }
            while (offset < _pendingA.Count);

            // Snow pushed aside around the prints, from the updated compression.
            _updateMaterial.SetVector(RimSettingsId, new Vector4(RimScale, RimShare, MaxRim, 0f));
            Graphics.Blit(_read, _rim, _updateMaterial, 1);
            RenderTexture.active = active;
            _pendingA.Clear();
            _pendingB.Clear();
            _urgent = false;
            _lastUpdate = now;

            Shader.SetGlobalTexture(PrintTexId, _read);
            Shader.SetGlobalTexture(RimTexId, _rim);
            Shader.SetGlobalTexture(ScorchTexId, _scorchRead);
            Shader.SetGlobalVector(PrintRectId, new Vector4(min.x, min.y, WorldSize, 1f / WorldSize));
            Shader.SetGlobalVector(PrintParamsId, new Vector4(CompressionScale, 1f / Resolution, EdgeFadeMeters, 1f));
        }

        /// <summary>
        /// True once after the window moved, with the previous window's minimum corner (null on the first placement),
        /// so the caller can draw stamps that just came into view.
        /// </summary>
        public bool TakeRecentre(out Vector2? previousMin)
        {
            previousMin = _recentredFrom;
            bool recentred = _recentred;
            _recentred = false;
            return recentred;
        }

        /// <summary>
        /// True when a stamp of this reach at world XZ touches the current window.
        /// </summary>
        public bool Overlaps(Vector2 world, float reach)
        {
            if (!_placed)
            {
                return false;
            }

            Vector2 min = WindowMin();
            return world.x + reach >= min.x && world.y + reach >= min.y
                && world.x - reach <= min.x + WorldSize && world.y - reach <= min.y + WorldSize;
        }

        /// <summary>
        /// True when a stamp of this reach lay wholly inside a window with this minimum corner.
        /// </summary>
        public static bool Inside(Vector2 windowMin, Vector2 world, float reach)
        {
            return world.x - reach >= windowMin.x && world.y - reach >= windowMin.y
                && world.x + reach <= windowMin.x + WorldSize && world.y + reach <= windowMin.y + WorldSize;
        }

        private Vector2 WindowMin()
        {
            return new Vector2(_centerTexel.x * Texel, _centerTexel.y * Texel) - Vector2.one * (WorldSize * 0.5f);
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(PrintParamsId, Vector4.zero);
            Shader.SetGlobalTexture(PrintTexId, Texture2D.blackTexture);
            Shader.SetGlobalTexture(RimTexId, Texture2D.blackTexture);
            Shader.SetGlobalTexture(ScorchTexId, Texture2D.blackTexture);
            Release(ref _scorchRead);
            Release(ref _scorchWrite);
            Release(ref _read);
            Release(ref _rim);
            Release(ref _write);
            Object.Destroy(_updateMaterial);
            _pendingA.Clear();
            _pendingB.Clear();
        }

        /// <summary>
        /// The snow coat shader (WinterStorms/SnowCoat), or null when the bundle is missing.
        /// </summary>
        public static Shader LoadCoatShader()
        {
            return LoadShaders() ? _coatShader : null;
        }

        /// <summary>
        /// True when a point lies at least margin inside the current window.
        /// </summary>
        public bool ContainsWell(Vector2 world, float margin)
        {
            return _placed && Inside(WindowMin(), world, margin);
        }

        private static RenderTexture CreateTarget(RenderTextureFormat format, string name, int size = Resolution)
        {
            var texture = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = previous;
            return texture;
        }

        private static void Release(ref RenderTexture texture)
        {
            if (texture != null)
            {
                texture.Release();
                Object.Destroy(texture);
                texture = null;
            }
        }
    }
}
