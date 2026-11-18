using System.Collections.Generic;
using UnityEngine;

namespace WinterOutbreak
{
    // EFT draws its static ground decals (grime, oil stains, road dirt) into the deferred buffers after opaque
    // geometry, projected through a box. A floor decal whose footprint is mostly under ground snow is switched off
    // (StaticDeferredDecal unregisters itself in OnDisable) and switched back on when the snow is cleared.
    internal sealed partial class VoxelSnowArea
    {
        private const float DecalHideDepth = 0.04f;
        private const int DecalChecksPerFrame = 32;
        private const int DecalHidesPerFrame = 4;

        private List<StaticDeferredDecal> _floorDecals;
        private readonly List<StaticDeferredDecal> _hiddenDecals = new List<StaticDeferredDecal>();
        private int _decalCursor;
        private int _staticDecalCount;
        private int _forwardVerticalDecals;

        private List<Renderer> _floorOverlays;
        private readonly List<Renderer> _hiddenOverlays = new List<Renderer>();
        private int _overlayCursor;

        internal void HideDecalsUnderSnow(float depth)
        {
            if (depth < DecalHideDepth)
            {
                return;
            }

            if (_floorDecals == null)
            {
                _floorDecals = new List<StaticDeferredDecal>();
                foreach (var decal in Object.FindObjectsOfType<StaticDeferredDecal>())
                {
                    _staticDecalCount++;
                    if (Mathf.Abs(decal.transform.forward.y) >= 0.7f)
                    {
                        _forwardVerticalDecals++;
                    }

                    // The decal box projects along its local up axis; walls and props keep their decals.
                    if (decal.enabled && Mathf.Abs(decal.transform.up.y) >= 0.7f)
                    {
                        _floorDecals.Add(decal);
                    }
                }

                _floorOverlays = new List<Renderer>();
                foreach (var renderer in Object.FindObjectsOfType<MeshRenderer>())
                {
                    if (renderer.enabled && renderer.bounds.size.y <= 0.5f && UsesDeferredDecalShader(renderer))
                    {
                        _floorOverlays.Add(renderer);
                    }
                }
            }

            HideOverlaysUnderSnow();

            int hidden = 0;
            for (int checks = 0; checks < DecalChecksPerFrame && _floorDecals.Count > 0 && hidden < DecalHidesPerFrame;
                 checks++)
            {
                if (_decalCursor >= _floorDecals.Count)
                {
                    _decalCursor = 0;
                }

                StaticDeferredDecal decal = _floorDecals[_decalCursor];
                if (decal == null || !decal.enabled)
                {
                    RemoveFloorDecal(_decalCursor);
                    continue;
                }

                if (!IsUnderSnow(decal.transform))
                {
                    _decalCursor++;
                    continue;
                }

                decal.enabled = false;
                _hiddenDecals.Add(decal);
                RemoveFloorDecal(_decalCursor);
                hidden++;
            }
        }

        private void HideOverlaysUnderSnow()
        {
            for (int checks = 0; checks < 8 && _floorOverlays.Count > 0; checks++)
            {
                if (_overlayCursor >= _floorOverlays.Count)
                {
                    _overlayCursor = 0;
                }

                Renderer overlay = _floorOverlays[_overlayCursor];
                if (overlay == null || !overlay.enabled)
                {
                    RemoveFloorOverlay(_overlayCursor);
                    continue;
                }

                if (!IsOverlayUnderSnow(overlay.bounds))
                {
                    _overlayCursor++;
                    continue;
                }

                overlay.enabled = false;
                _hiddenOverlays.Add(overlay);
                RemoveFloorOverlay(_overlayCursor);
            }
        }

        private void RemoveFloorOverlay(int index)
        {
            int last = _floorOverlays.Count - 1;
            _floorOverlays[index] = _floorOverlays[last];
            _floorOverlays.RemoveAt(last);
        }

        private static bool UsesDeferredDecalShader(Renderer renderer)
        {
            foreach (var material in renderer.sharedMaterials)
            {
                string shader = material != null && material.shader != null ? material.shader.name : "";
                if (shader.StartsWith("Decal/") && shader.Contains("Deferred"))
                {
                    return true;
                }
            }

            return false;
        }

        // Snow at any of nine points across the overlay's bounds. A puddle with snow anywhere on it is outdoors, and
        // one indoors finds no snow at all.
        private bool IsOverlayUnderSnow(Bounds bounds)
        {
            for (int i = -1; i <= 1; i++)
            {
                for (int j = -1; j <= 1; j++)
                {
                    Vector3 point = bounds.center
                        + new Vector3(bounds.extents.x * 0.5f * i, 0f, bounds.extents.z * 0.5f * j);
                    Vector3 local = point - _origin;
                    if (TryGetSnowHeights(local, out float ground, out float top)
                        && top - ground >= DecalHideDepth && Mathf.Abs(ground - local.y) <= 0.75f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void RemoveFloorDecal(int index)
        {
            int last = _floorDecals.Count - 1;
            _floorDecals[index] = _floorDecals[last];
            _floorDecals.RemoveAt(last);
        }

        // Covered when at least three of five points across the decal (center and its four half-way points) have ground
        // snow whose ground lies inside the decal box.
        private bool IsUnderSnow(Transform decal)
        {
            Vector3 center = decal.position;
            Vector3 scale = decal.lossyScale;
            float reachY = Mathf.Max(0.75f, Mathf.Abs(scale.y) * 0.5f);
            Vector3 right = decal.right * (scale.x * 0.25f);
            Vector3 forward = decal.forward * (scale.z * 0.25f);
            int covered = 0;
            for (int i = 0; i < 5; i++)
            {
                Vector3 point = i == 0 ? center
                    : center + (i == 1 ? right : i == 2 ? -right : i == 3 ? forward : -forward);
                Vector3 local = point - _origin;
                if (TryGetSnowHeights(local, out float ground, out float top)
                    && top - ground >= DecalHideDepth && Mathf.Abs(ground - local.y) <= reachY)
                {
                    covered++;
                }

                if (covered >= 3 || covered + (4 - i) < 3)
                {
                    break;
                }
            }

            return covered >= 3;
        }

        private static string OverlayNames(List<Renderer> overlays)
        {
            var names = new List<string>();
            foreach (var overlay in overlays)
            {
                if (overlay != null && names.Count < 8)
                {
                    names.Add(overlay.name);
                }
            }

            return string.Join(", ", names);
        }

        private void RestoreDecals()
        {
            foreach (var decal in _hiddenDecals)
            {
                if (decal != null)
                {
                    decal.enabled = true;
                }
            }

            _hiddenDecals.Clear();
            _floorDecals = null;

            foreach (var overlay in _hiddenOverlays)
            {
                if (overlay != null)
                {
                    overlay.enabled = true;
                }
            }

            _hiddenOverlays.Clear();
            _floorOverlays = null;
        }
    }
}
