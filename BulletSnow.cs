using BepInEx.Configuration;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using Systems.Effects;
using UnityEngine;

namespace WinterOutbreak
{
    /// <summary>
    /// Bullets in snow. EFT's impact effect (EffectsCommutator.PlayHitEffect) runs once per world hit with the hit
    /// point, surface normal, direction and speed. A hit on the ground under the snow presses a small hole through the
    /// print texture (with a rim, refilling over a minute) and kicks up a puff of powder and grains along the surface,
    /// tilted by the bullet's angle and bigger for faster rounds. Hits on walls and props above the snow, and hits on
    /// players, are left alone. EFT's own impact effect and sound still play.
    /// </summary>
    public sealed partial class WinterOutbreakPlugin
    {
        private const float BulletHoleRadius = 0.06f;
        private const float BulletHoleMaxDepth = 0.12f;
        private const float BulletHoleRefillSeconds = 60f;
        private const float BulletSnowRange = 70f;
        private const int BulletImpactsPerFrame = 6;

        private ConfigEntry<bool> _bulletImpacts;
        private BulletSnowPuff _bulletSnowPuff;
        private int _bulletImpactFrame;
        private int _bulletImpactsThisFrame;

        private void BindBulletSnowSettings()
        {
            _bulletImpacts = Config.Bind("Snow Surface", "Bullet Impacts", true,
                "Bullets that hit snowy ground leave a small hole and kick up a puff of snow.");
        }

        private void OnBulletHit(Shot shot)
        {
            if (_voxelSnow == null || _bulletImpacts == null || !_bulletImpacts.Value)
            {
                return;
            }

            Vector3 point = shot.HitPoint;
            Vector3 normal = shot.HitNormal;
            if (normal.y < 0.35f)
            {
                return;
            }

            var camera = Camera.main;
            if (camera == null || (camera.transform.position - point).sqrMagnitude > BulletSnowRange * BulletSnowRange)
            {
                return;
            }

            if (_bulletImpactFrame != Time.frameCount)
            {
                _bulletImpactFrame = Time.frameCount;
                _bulletImpactsThisFrame = 0;
            }

            // A long burst into the snow: the first few hits of a frame are plenty to read.
            if (_bulletImpactsThisFrame >= BulletImpactsPerFrame)
            {
                return;
            }

            // Only the ground under the snow (or the snow surface itself); TryGetDepth rejects points well above it.
            if (!_voxelSnow.TryGetDepth(point, out float depth) || depth < 0.02f)
            {
                return;
            }

            _bulletImpactsThisFrame++;
            float speed = Mathf.InverseLerp(150f * 150f, 900f * 900f, shot.VelocitySqrMagnitude);
            float energy = Mathf.Lerp(0.45f, 1f, speed) * (shot.FragmentIndex > 0 ? 0.5f : 1f);
            // Every hole a little different, and the outline is lobed per hole (random phases).
            float radius = BulletHoleRadius * Mathf.Lerp(0.8f, 1.15f, energy) * Random.Range(0.75f, 1.3f);
            float holeDepth = Mathf.Min(depth * 0.85f, BulletHoleMaxDepth) * energy * Random.Range(0.7f, 1.1f);
            _voxelSnow.StampBulletHole(point, radius, holeDepth, BulletHoleRefillSeconds);

            // A shallow shot skips forward through the snow: a short, fading gouge along its path.
            Vector3 flat = new Vector3(shot.Direction.x, 0f, shot.Direction.z);
            float grazing = 1f - Mathf.Abs(Vector3.Dot(shot.Direction.normalized, normal));
            if (grazing > 0.5f && flat.sqrMagnitude > 0.01f)
            {
                flat.Normalize();
                int steps = grazing > 0.8f ? 3 : 2;
                for (int i = 1; i <= steps; i++)
                {
                    float fade = 1f - i / (steps + 1f);
                    _voxelSnow.StampBulletHole(point + flat * (radius * 1.1f * i), radius * Mathf.Lerp(0.6f, 0.9f, fade),
                        holeDepth * fade, BulletHoleRefillSeconds);
                }
            }

            _bulletSnowPuff ??= new BulletSnowPuff();
            _bulletSnowPuff.Emit(point, normal, shot.Direction, energy, Mathf.InverseLerp(0.08f, 0.02f, depth));
        }

        private void ClearBulletSnow()
        {
            _bulletSnowPuff?.Dispose();
            _bulletSnowPuff = null;
        }

        [HarmonyPatch(typeof(EffectsCommutator), nameof(EffectsCommutator.PlayHitEffect))]
        private static class BulletSnowPatch
        {
            private static void Postfix(Shot info, PlayerHitInfo playerHitInfo)
            {
                if (playerHitInfo != null || info == null || _plugin == null)
                {
                    return;
                }

                try
                {
                    _plugin.OnBulletHit(info);
                }
                catch (System.Exception exception)
                {
                    _plugin.Logger.LogWarning($"Bullet snow impact failed: {exception.Message}");
                    _plugin._bulletImpacts.Value = false;
                }
            }
        }
    }

    /// <summary>
    /// A bullet's puff in snow: a little powder and a spray of grains, lit like the scene.
    /// </summary>
    internal sealed class BulletSnowPuff
    {
        private readonly System.Random _random = new System.Random();
        private readonly SnowPowder _powder = new SnowPowder("Winter Outbreak Bullet Snow Powder", 1200, 0.08f, 2.2f, 2.2f, 0.3f);
        private readonly SnowPowder _grains = new SnowPowder("Winter Outbreak Bullet Snow Grains", 2000, 1.0f, 0.4f, 1.0f, 0f);
        private Color _light = Color.white;
        private float _nextLight;

        public void Emit(Vector3 point, Vector3 normal, Vector3 direction, float energy, float shallow)
        {
            if (!_powder.IsAvailable)
            {
                return;
            }

            if (Time.time >= _nextLight)
            {
                _nextLight = Time.time + 0.5f;
                _light = SnowPowder.Tint(1f, 1f);
            }

            // Snow kicks out along the surface and forward of the bullet's path, not straight back at the shooter.
            Vector3 reflected = Vector3.Reflect(direction.normalized, normal);
            Vector3 kick = (normal * 0.8f + reflected * 0.6f).normalized;
            Color dirt = new Color(_light.r * 0.6f, _light.g * 0.5f, _light.b * 0.42f, 1f);

            int puffs = Mathf.RoundToInt(Mathf.Lerp(2f, 6f, energy));
            for (int i = 0; i < puffs; i++)
            {
                Color color = Color.Lerp(_light, dirt, Mathf.Max(shallow * Range(0.2f, 0.6f), Range(0f, 0.12f)))
                    * Range(0.78f, 1.0f);
                color.a = Range(0.16f, 0.32f);
                _powder.Emit(point + normal * 0.03f, (kick * Range(1.0f, 3.2f) + Random3() * 0.6f) * energy,
                    Range(0.10f, 0.32f) * Mathf.Lerp(0.8f, 1.2f, energy), Range(0.7f, 1.6f), color, Range(0f, 360f));
            }

            int grains = Mathf.RoundToInt(Mathf.Lerp(6f, 18f, energy));
            for (int i = 0; i < grains; i++)
            {
                Color color = _random.NextDouble() < shallow * 0.5 ? dirt * Range(0.8f, 1.1f)
                    : _light * Range(0.82f, 1.0f);
                color.a = Range(0.7f, 0.95f);
                _grains.Emit(point + normal * 0.02f, (kick * Range(2.5f, 6.5f) + Random3() * 1.4f) * energy,
                    Range(0.012f, 0.035f), Range(0.35f, 0.85f), color, Range(0f, 360f));
            }
        }

        private Vector3 Random3()
        {
            return new Vector3(Range(-1f, 1f), Range(-1f, 1f), Range(-1f, 1f));
        }

        private float Range(float min, float max)
        {
            return Mathf.Lerp(min, max, (float)_random.NextDouble());
        }

        public void Dispose()
        {
            _powder.Dispose();
            _grains.Dispose();
        }
    }
}
