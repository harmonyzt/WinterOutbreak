using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    // The far snow: snow beyond the detailed chunks, drawn on the GPU
    public sealed partial class WinterOutbreakPlugin
    {
        private const int HeightCaptureSize = 1024;
        private const float HeightCaptureWidth = 200f;
        private const float HeightCaptureAbove = 300f;
        private const float HeightCaptureInterval = 10f;
        private const float HeightCaptureMove = 25f;
        private const float HeightCaptureEmpty = -10000f;
        private const float FarGridSpacing = 0.5f;
        private const int FarMaskSize = 64;
        private const float FarMaskInterval = 0.25f;

        private ConfigEntry<bool> _farSnowEnabled;

        private Camera _heightCamera;
        private RenderTexture _heightTarget;
        private float _nextHeightCapture;
        private Vector3 _lastHeightCapturePosition;
        private Vector3 _farCenter;
        private readonly List<Renderer> _hiddenForCapture = new List<Renderer>();

        private RenderTexture _farHeight;
        private RenderTexture _farTerrain;
        private CommandBuffer _farCommands;
        private RenderTexture _farIce;
        private readonly List<Renderer> _iceForCapture = new List<Renderer>();
        private readonly List<int> _iceLayers = new List<int>();
        private int _iceCaptureLayer = -2;
        internal const string FarHeightSample = "Winter Outbreak far height";
        private Material _farHeightMaterial;
        private Material _farSnowMaterial;
        private GameObject _farSnowObject;
        private MeshRenderer _farRenderer;
        private Mesh _farMesh;
        private Texture2D _farMask;
        private byte[] _farMaskPixels;
        private float _nextFarMask;
        private bool _farLive;
        private bool _farFailureLogged;

        private static readonly int FarHeightId = Shader.PropertyToID("_WSFarHeight");
        private static readonly int FarRectId = Shader.PropertyToID("_WSFarRect");
        private static readonly int FarParamsId = Shader.PropertyToID("_WSFarParams");
        private static readonly int FarMaskId = Shader.PropertyToID("_WSFarMask");
        private static readonly int FarMaskRectId = Shader.PropertyToID("_WSFarMaskRect");
        private static readonly int TerrainHeightId = Shader.PropertyToID("_WSTerrainHeight");
        private static readonly int TerrainRectId = Shader.PropertyToID("_WSTerrainRect");
        private static readonly int TerrainParamsId = Shader.PropertyToID("_WSTerrainParams");
        private static readonly int FarTerrainId = Shader.PropertyToID("_WSFarTerrain");
        private static readonly int FarIceId = Shader.PropertyToID("_WSFarIce");

        private void BindFarSnowSettings()
        {
            _farSnowEnabled = BindSetting(PerformanceSection, "Far Snow On GPU", true,
                "Draws the snow beyond the detailed area on the graphics card, from a top-down picture of the map taken "
                + "every few seconds. Off uses simpler snow tiles built on the CPU instead. Needs GPU Snow Prints.", 6);
        }

        // Takes a new top-down picture when one is due, then updates the depth and the detail mask
        private void UpdateFarSnow(Player player)
        {
            // A headless Fika host has no graphics device to draw with
            if (!_farSnowEnabled.Value || _voxelSnow == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                SetFarSnowLive(false);
                return;
            }

            if (player != null)
            {
                CaptureHeightWhenDue(player.Position);
            }

            if (!_farLive)
            {
                return;
            }

            // A snow area made again mid-raid starts with the distant tiles on
            _voxelSnow.FarSnowActive = true;

            float depth = _voxelSnow.LastDepth;
            Vector4 parameters = Shader.GetGlobalVector(FarParamsId);
            parameters.y = depth < 0.005f ? 0f : depth;
            Shader.SetGlobalVector(FarParamsId, parameters);

            if (Time.time >= _nextFarMask)
            {
                _nextFarMask = Time.time + FarMaskInterval;
                UpdateFarMask();
            }
        }

        private void CaptureHeightWhenDue(Vector3 position)
        {
            bool moved = (position - _lastHeightCapturePosition).sqrMagnitude > HeightCaptureMove * HeightCaptureMove;
            if (Time.time < _nextHeightCapture && !moved)
            {
                return;
            }

            Shader captureShader = SnowPrintTexture.LoadHeightShader();
            Shader heightShader = SnowPrintTexture.LoadFarHeightShader();
            Material snow = _voxelSnow.SnowMaterial;
            if (captureShader == null || !captureShader.isSupported || heightShader == null || !heightShader.isSupported
                || snow == null || snow.shader.name != "WinterStorms/SnowPrints")
            {
                if (!_farFailureLogged)
                {
                    _farFailureLogged = true;
                    Logger.LogWarning("Far snow: needs GPU snow prints and its shaders in snowprints.bundle; using the "
                        + "simpler distant snow instead.");
                }

                return;
            }

            _nextHeightCapture = Time.time + HeightCaptureInterval;
            _lastHeightCapturePosition = position;
            EnsureHeightCamera();
            EnsureFarObjects(heightShader, snow);

            // Render straight down
            _heightCamera.transform.position = position + Vector3.up * HeightCaptureAbove;
            _heightCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            // Our own snow lies on the default layer with the scenery
            _voxelSnow.HideSnowForCapture(_hiddenForCapture);
            if (_farRenderer != null && !_farRenderer.forceRenderingOff)
            {
                _farRenderer.forceRenderingOff = true;
                _hiddenForCapture.Add(_farRenderer);
            }

            _heightCamera.RenderWithShader(captureShader, "RenderType");

            foreach (var renderer in _hiddenForCapture)
            {
                if (renderer != null)
                {
                    renderer.forceRenderingOff = false;
                }
            }

            _hiddenForCapture.Clear();
            CaptureIceTops(captureShader);
            BuildFarHeight(position);
        }

        private void EnsureHeightCamera()
        {
            if (_heightCamera != null)
            {
                return;
            }

            _heightTarget = new RenderTexture(HeightCaptureSize, HeightCaptureSize, 24, RenderTextureFormat.ARGBFloat)
            {
                name = "Winter Outbreak height capture",
                filterMode = FilterMode.Point
            };
            _heightTarget.Create();

            var holder = new GameObject("Winter Outbreak height capture camera");
            DontDestroyOnLoad(holder);
            _heightCamera = holder.AddComponent<Camera>();
            _heightCamera.enabled = false;
            _heightCamera.orthographic = true;
            _heightCamera.orthographicSize = HeightCaptureWidth * 0.5f;
            _heightCamera.aspect = 1f;
            _heightCamera.nearClipPlane = 1f;
            _heightCamera.farClipPlane = HeightCaptureAbove * 2f;
            _heightCamera.clearFlags = CameraClearFlags.SolidColor;
            _heightCamera.backgroundColor = new Color(HeightCaptureEmpty, 0f, 0f, 0f);
            _heightCamera.renderingPath = RenderingPath.Forward;
            _heightCamera.useOcclusionCulling = false;
            _heightCamera.allowHDR = false;
            _heightCamera.allowMSAA = false;
            _heightCamera.depthTextureMode = DepthTextureMode.None;
            _heightCamera.cullingMask = HeightCaptureMask();
            _heightCamera.targetTexture = _heightTarget;
        }

        // Everything but the terrain (read from its heightmap) and layers that hold people, loot and effects
        private static int HeightCaptureMask()
        {
            int mask = ~LayersMaskController.TerrainMask;
            for (int layer = 0; layer < 32; layer++)
            {
                string name = LayerMask.LayerToName(layer).ToLowerInvariant();
                if (IsDynamicLayer(name))
                {
                    mask &= ~(1 << layer);
                }
            }

            return mask;
        }

        private static bool IsDynamicLayer(string name)
        {
            string[] dynamicParts = { "player", "deadbody", "corpse", "loot", "trigger", "water", "ui", "sky", "weapon",
                "hands", "ignore raycast", "transparentfx", "sound", "postprocess", "volume" };
            foreach (string part in dynamicParts)
            {
                if (name.Contains(part))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds the far height texture right after a top-down picture and moves the far grid under it.
        /// </summary>
        private void BuildFarHeight(Vector3 center)
        {
            _farCenter = center;

            // The camera looks straight down from the centre, so the texture's minimum corner is half the picture's
            // width away along -x and -z
            float half = HeightCaptureWidth * 0.5f;
            var rect = new Vector4(center.x - half, center.z - half, HeightCaptureWidth, 1f / HeightCaptureWidth);
            Shader.SetGlobalVector(FarRectId, rect);

            // One command buffer
            _farCommands ??= new CommandBuffer { name = FarHeightSample };
            _farCommands.Clear();
            _farCommands.BeginSample(FarHeightSample);
            _farCommands.SetGlobalVector(FarRectId, rect);

            // Terrain first, into its own texture: every tile max-blended over an empty start
            _farCommands.SetRenderTarget(_farTerrain);
            _farCommands.ClearRenderTarget(false, true, new Color(HeightCaptureEmpty, 0f, 0f, 0f));
            foreach (var terrain in Terrain.activeTerrains)
            {
                TerrainData data = terrain != null ? terrain.terrainData : null;
                if (data == null || data.heightmapTexture == null)
                {
                    continue;
                }

                Vector3 corner = terrain.GetPosition();
                Vector3 size = data.size;
                if (corner.x > rect.x + rect.z || corner.z > rect.y + rect.z
                    || corner.x + size.x < rect.x || corner.z + size.z < rect.y)
                {
                    continue;
                }

                _farCommands.SetGlobalTexture(TerrainHeightId, data.heightmapTexture);
                _farCommands.SetGlobalVector(TerrainRectId, new Vector4(corner.x, corner.z, size.x, size.z));
                _farCommands.SetGlobalVector(TerrainParamsId, new Vector4(corner.y, size.y, data.heightmapResolution, 0f));
                _farCommands.Blit(_heightTarget, _farTerrain, _farHeightMaterial, 0);
            }

            // Then the picture over it, keeping only smooth raised surfaces and leaving the ice bare (FarHeight.shader)
            _farCommands.SetGlobalTexture(FarTerrainId, _farTerrain);
            _farCommands.SetGlobalTexture(FarIceId, _farIce);
            _farCommands.Blit(_heightTarget, _farHeight, _farHeightMaterial, 1);
            _farCommands.EndSample(FarHeightSample);
            Graphics.ExecuteCommandBuffer(_farCommands);

            Shader.SetGlobalTexture(FarHeightId, _farHeight);
            Vector4 parameters = Shader.GetGlobalVector(FarParamsId);

            // The shader's slope taps sit one grid step apart, so a jump between two vertices is always seen
            parameters.x = FarGridSpacing / HeightCaptureWidth;
            parameters.z = center.y;
            Shader.SetGlobalVector(FarParamsId, parameters);

            float x = Mathf.Round(center.x / FarGridSpacing) * FarGridSpacing;
            float z = Mathf.Round(center.z / FarGridSpacing) * FarGridSpacing;
            _farSnowObject.transform.position = new Vector3(x, 0f, z);

            UpdateFarMask();
            SetFarSnowLive(true);
        }

        private void EnsureFarObjects(Shader heightShader, Material snow)
        {
            if (_farHeight == null)
            {
                _farHeight = new RenderTexture(HeightCaptureSize, HeightCaptureSize, 0, RenderTextureFormat.RFloat)
                {
                    name = "Winter Outbreak far snow height",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                _farHeight.Create();
            }

            if (_farTerrain == null)
            {
                _farTerrain = new RenderTexture(HeightCaptureSize, HeightCaptureSize, 0, RenderTextureFormat.RFloat)
                {
                    name = "Winter Outbreak far snow terrain",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                _farTerrain.Create();
            }

            if (_farIce == null)
            {
                _farIce = new RenderTexture(HeightCaptureSize, HeightCaptureSize, 16, RenderTextureFormat.RFloat)
                {
                    name = "Winter Outbreak far snow ice",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                _farIce.Create();
            }

            if (_farHeightMaterial == null)
            {
                _farHeightMaterial = new Material(heightShader);
            }

            if (_farMask == null)
            {
                _farMask = new Texture2D(FarMaskSize, FarMaskSize, TextureFormat.R8, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                _farMaskPixels = new byte[FarMaskSize * FarMaskSize];
            }

            if (_farSnowObject != null)
            {
                return;
            }

            _farMesh = CreateFarGrid();
            _farSnowMaterial = new Material(snow) { name = "Winter Outbreak far snow" };
            _farSnowMaterial.EnableKeyword("WS_FAR_SNOW");

            _farSnowObject = new GameObject("Winter Outbreak far snow");
            _farSnowObject.AddComponent<MeshFilter>().sharedMesh = _farMesh;
            _farRenderer = _farSnowObject.AddComponent<MeshRenderer>();
            _farRenderer.sharedMaterial = _farSnowMaterial;
            _farRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _farRenderer.lightProbeUsage = LightProbeUsage.Off;
            _farRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _farRenderer.enabled = false;
        }

        /// <summary>
        /// Renders our frozen lake and sea ice alone from the top-down camera, so the far snow can leave it bare.
        /// </summary>
        private void CaptureIceTops(Shader shader)
        {
            _iceForCapture.Clear();
            foreach (var renderer in _iceRenderers)
            {
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    _iceForCapture.Add(renderer);
                }
            }

            foreach (var chunk in _seaChunks)
            {
                if (chunk.Near != null && chunk.Near.enabled)
                {
                    _iceForCapture.Add(chunk.Near);
                }

                if (chunk.Far != null && chunk.Far.enabled)
                {
                    _iceForCapture.Add(chunk.Far);
                }
            }

            if (_iceCaptureLayer == -2)
            {
                _iceCaptureLayer = FindUnusedLayer();
                if (_iceCaptureLayer < 0)
                {
                    Logger.LogWarning("Far snow: no unused layer for the ice capture; far snow may lie on the ice.");
                }
            }

            if (_iceForCapture.Count == 0 || _iceCaptureLayer < 0)
            {
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = _farIce;
                GL.Clear(true, true, new Color(HeightCaptureEmpty, 0f, 0f, 0f));
                RenderTexture.active = previous;
                return;
            }

            _iceLayers.Clear();
            foreach (var renderer in _iceForCapture)
            {
                _iceLayers.Add(renderer.gameObject.layer);
                renderer.gameObject.layer = _iceCaptureLayer;
            }

            int mask = _heightCamera.cullingMask;
            RenderTexture target = _heightCamera.targetTexture;
            _heightCamera.cullingMask = 1 << _iceCaptureLayer;
            _heightCamera.targetTexture = _farIce;
            _heightCamera.RenderWithShader(shader, "RenderType");
            _heightCamera.cullingMask = mask;
            _heightCamera.targetTexture = target;

            for (int i = 0; i < _iceForCapture.Count; i++)
            {
                if (_iceForCapture[i] != null)
                {
                    _iceForCapture[i].gameObject.layer = _iceLayers[i];
                }
            }
        }

        // A layer EFT leaves unnamed
        private static int FindUnusedLayer()
        {
            for (int layer = 31; layer > 0; layer--)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(layer)))
                {
                    return layer;
                }
            }

            return -1;
        }

        // A flat square grid centred on its object, one vertex every FarGridSpacing metres across the picture
        private static Mesh CreateFarGrid()
        {
            int count = Mathf.RoundToInt(HeightCaptureWidth / FarGridSpacing) + 1;
            float half = HeightCaptureWidth * 0.5f;
            var vertices = new Vector3[count * count];
            var normals = new Vector3[vertices.Length];
            var colors = new Color32[vertices.Length];
            for (int z = 0; z < count; z++)
            {
                for (int x = 0; x < count; x++)
                {
                    int i = z * count + x;
                    vertices[i] = new Vector3(x * FarGridSpacing - half, 0f, z * FarGridSpacing - half);
                    normals[i] = Vector3.up;
                    colors[i] = new Color32(255, 255, 255, 255);
                }
            }

            var triangles = new int[(count - 1) * (count - 1) * 6];
            int t = 0;
            for (int z = 0; z < count - 1; z++)
            {
                for (int x = 0; x < count - 1; x++)
                {
                    int a = z * count + x;
                    int b = a + count;
                    triangles[t++] = a;
                    triangles[t++] = b;
                    triangles[t++] = a + 1;
                    triangles[t++] = a + 1;
                    triangles[t++] = b;
                    triangles[t++] = b + 1;
                }
            }

            var mesh = new Mesh { name = "Winter Outbreak far snow grid", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.colors32 = colors;
            mesh.triangles = triangles;

            // The shader lifts the vertices onto the ground, which can be hundreds of metres above or below zero
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(HeightCaptureWidth, 4000f, HeightCaptureWidth));
            mesh.UploadMeshData(true);
            return mesh;
        }

        private void UpdateFarMask()
        {
            if (_farMask == null || _voxelSnow == null)
            {
                return;
            }

            Vector4 rect = _voxelSnow.FillDetailMask(_farMaskPixels, FarMaskSize, _raid?.MainPlayer?.Position ?? _farCenter);
            _farMask.LoadRawTextureData(_farMaskPixels);
            _farMask.Apply(false);
            Shader.SetGlobalTexture(FarMaskId, _farMask);
            Shader.SetGlobalVector(FarMaskRectId, rect);
        }

        private void SetFarSnowLive(bool live)
        {
            if (_farLive == live)
            {
                return;
            }

            _farLive = live;
            if (_farRenderer != null)
            {
                _farRenderer.enabled = live;
            }

            if (_voxelSnow != null)
            {
                _voxelSnow.FarSnowActive = live;
            }
        }

        private void ClearFarSnow()
        {
            SetFarSnowLive(false);
            if (_farSnowObject != null)
            {
                Destroy(_farSnowObject);
                _farSnowObject = null;
                _farRenderer = null;
            }

            if (_farMesh != null)
            {
                Destroy(_farMesh);
                _farMesh = null;
            }

            if (_farSnowMaterial != null)
            {
                Destroy(_farSnowMaterial);
                _farSnowMaterial = null;
            }

            _farFailureLogged = false;
            _nextHeightCapture = 0f;
            _lastHeightCapturePosition = Vector3.zero;
        }
    }

    internal sealed partial class VoxelSnowArea
    {
        internal Material SnowMaterial => _material;

        /// <summary>
        /// Turns off rendering of the ground snow, distant tiles and stair snow for the top-down picture.
        /// </summary>
        internal void HideSnowForCapture(List<Renderer> hidden)
        {
            foreach (var chunk in _active)
            {
                HideForCapture(chunk.Object, hidden);
            }

            foreach (var tile in _groundPreviews.Values)
            {
                HideForCapture(tile.Object, hidden);
            }

            HideForCapture(_stairSnowObject, hidden);
        }

        private static void HideForCapture(GameObject holder, List<Renderer> hidden)
        {
            if (holder == null)
            {
                return;
            }

            var renderer = holder.GetComponent<MeshRenderer>();
            if (renderer != null && !renderer.forceRenderingOff)
            {
                renderer.forceRenderingOff = true;
                hidden.Add(renderer);
            }
        }

        /// <summary>
        /// Marks every published chunk in a square of chunks around a point, one byte per chunk, and returns the
        /// square's world rectangle (xy: minimum corner XZ, z: size, w: 1 / size) for the far snow shader.
        /// </summary>
        internal Vector4 FillDetailMask(byte[] pixels, int size, Vector3 center)
        {
            int centerX = Mathf.FloorToInt((center.x - _origin.x) / ChunkSize);
            int centerZ = Mathf.FloorToInt((center.z - _origin.z) / ChunkSize);
            int baseX = centerX - size / 2;
            int baseZ = centerZ - size / 2;

            Array.Clear(pixels, 0, pixels.Length);
            foreach (var chunk in _active)
            {
                // A new chunk rises out of the ground over a moment
                if (chunk.GrowthOffset > 0f)
                {
                    continue;
                }

                int x = chunk.Key.x - baseX;
                int z = chunk.Key.y - baseZ;
                if (x >= 0 && z >= 0 && x < size && z < size)
                {
                    pixels[z * size + x] = 255;
                }
            }

            float width = size * ChunkSize;
            return new Vector4(_origin.x + baseX * ChunkSize, _origin.z + baseZ * ChunkSize, width, 1f / width);
        }
    }
}
