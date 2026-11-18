using UnityEngine;

namespace WinterOutbreak
{
    /// <summary>
    /// Procedural textures for blowing snow, generated once at runtime (no asset bundle).
    /// </summary>
    internal static class SnowTextures
    {
        /// <summary>
        /// Soft billowing blob with a broken, cauliflower-like edge for plumes.
        /// </summary>
        internal static Texture2D CreatePuffTexture()
        {
            const int size = 128;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f;
                    float v = (y + 0.5f) / size * 2f - 1f;
                    float radius = Mathf.Sqrt(u * u + v * v);
                    float billow = Fbm(x / 22f, y / 22f, 4, 0f, 0f);
                    float edge = 1f - Mathf.SmoothStep(0.35f, 1f, radius + (billow - 0.5f) * 0.55f);
                    float density = Mathf.Clamp01(edge * Mathf.Lerp(0.45f, 1.15f, billow));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(255f * density * density));
                }
            }

            return Build("Winter Outbreak Puff", size, size, pixels, TextureWrapMode.Clamp);
        }

        /// <summary>
        /// Seamless wispy noise for the haze cards. It tiles in both axes so the cards can scroll it with the wind
        /// indefinitely.
        /// </summary>
        internal static Texture2D CreateHazeTexture()
        {
            const int width = 256;
            const int height = 128;
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float n = TileableFbm(x, y, width, height);
                    float wisps = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.32f, 0.82f, n));
                    float alpha = Mathf.Clamp01(0.18f + wisps * 0.82f);
                    pixels[y * width + x] = new Color32(255, 255, 255, (byte)(255f * alpha));
                }
            }

            return Build("Winter Outbreak Haze", width, height, pixels, TextureWrapMode.Repeat);
        }

        private static Texture2D Build(string name, int width, int height, Color32[] pixels, TextureWrapMode wrap)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
            {
                name = name,
                wrapMode = wrap,
                filterMode = FilterMode.Trilinear
            };
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        private static float Fbm(float x, float y, int octaves, float ox, float oy)
        {
            float sum = 0f;
            float amplitude = 0.5f;
            float total = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += Mathf.PerlinNoise(x + ox + i * 17.3f, y + oy + i * 31.7f) * amplitude;
                total += amplitude;
                x *= 2.03f;
                y *= 2.03f;
                amplitude *= 0.5f;
            }

            return sum / total;
        }

        private static float TileableFbm(int x, int y, int width, int height)
        {
            float sum = 0f;
            float amplitude = 0.5f;
            float total = 0f;
            int cellsX = 4;
            int cellsY = 2;
            for (int octave = 0; octave < 5; octave++)
            {
                float fx = (float)x / width * cellsX;
                float fy = (float)y / height * cellsY;
                sum += PeriodicValueNoise(fx, fy, cellsX, cellsY, octave) * amplitude;
                total += amplitude;
                cellsX *= 2;
                cellsY *= 2;
                amplitude *= 0.52f;
            }

            return sum / total;
        }

        private static float PeriodicValueNoise(float x, float y, int periodX, int periodY, int seed)
        {
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            float tx = x - x0;
            float ty = y - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);

            float a = Hash(x0, y0, periodX, periodY, seed);
            float b = Hash(x0 + 1, y0, periodX, periodY, seed);
            float c = Hash(x0, y0 + 1, periodX, periodY, seed);
            float d = Hash(x0 + 1, y0 + 1, periodX, periodY, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        private static float Hash(int x, int y, int periodX, int periodY, int seed)
        {
            x = ((x % periodX) + periodX) % periodX;
            y = ((y % periodY) + periodY) % periodY;
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }
    }
}
