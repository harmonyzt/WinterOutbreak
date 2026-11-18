using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace WinterOutbreak
{
    internal sealed class CorpseSnowCover
    {
        private const int DepthLayers = 10;
        private readonly List<CorpseSurfaceSnow> _surfaces;

        private CorpseSnowCover(List<CorpseSurfaceSnow> surfaces)
        {
            _surfaces = surfaces;
        }

        public static CorpseSnowCover Create(Corpse corpse, Material material, int roofMask, bool suppressIndoorSnow)
        {
            var surfaces = CorpseSurfaceSnow.Create(corpse.GetComponentInChildren<PlayerBody>(), material,
                roofMask, suppressIndoorSnow);
            return surfaces.Count == 0 ? null : new CorpseSnowCover(surfaces);
        }

        public void SetDepth(float progress)
        {
            int layers = Mathf.FloorToInt(Mathf.Clamp01(progress) * DepthLayers);
            for (int i = _surfaces.Count - 1; i >= 0; i--)
            {
                if (!_surfaces[i].Update(layers, DepthLayers))
                {
                    _surfaces[i].Dispose();
                    _surfaces.RemoveAt(i);
                }
            }
        }

        public void Dispose()
        {
            foreach (var surface in _surfaces)
            {
                surface.Dispose();
            }
        }
    }
}
