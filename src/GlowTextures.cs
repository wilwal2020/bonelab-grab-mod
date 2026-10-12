using UnityEngine;

namespace GravityGloves
{
    // Textures for the aim beam and sparks, generated in code so the mod doesn't need image files.
    internal static class GlowTextures
    {
        private const int BeamLength = 128;
        private const int BeamWidth = 32;

        private static Texture2D _beam;
        private static Texture2D _dot;
        private static Texture2D _spark;

        // Soft across its width and the same all along it. It used to carry pulses of light that scrolled along
        // the beam, but William's settings (2 m pulses, 0.5 per metre) joined them into this solid line, and he
        // kept that look when the pulse settings were removed in v0.25.
        public static Texture2D Beam => _beam != null ? _beam : _beam = CreateBeam();

        // A round glow that fades out to the edges.
        public static Texture2D Dot => _dot != null ? _dot : _dot = CreateDot();

        // A small soft spot, fully bright in the middle, for sparks.
        public static Texture2D Spark => _spark != null ? _spark : _spark = CreateSpark();

        private static Texture2D CreateBeam()
        {
            var pixels = new Color[BeamLength * BeamWidth];
            for (int x = 0; x < BeamLength; x++)
            {
                for (int y = 0; y < BeamWidth; y++)
                {
                    float across = (y + 0.5f) / BeamWidth * 2f - 1f;
                    float edge = Mathf.Exp(-across * across * 5f);
                    pixels[y * BeamLength + x] = new Color(1f, 1f, 1f, edge);
                }
            }
            return Create(BeamLength, BeamWidth, pixels, TextureWrapMode.Repeat);
        }

        private static Texture2D CreateDot()
        {
            const int size = 64;
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float r2 = dx * dx + dy * dy;
                    // A bright centre in a wider haze, fading out toward the edge of the square. It peaks at 30%,
                    // which is the look William picked in the line preview.
                    float glow = 0.6f * Mathf.Exp(-r2 * 12f) + 0.4f * Mathf.Exp(-r2 * 3.5f);
                    float fade = 0.3f * (1f - Ease(0f, 1f, Mathf.Sqrt(r2)));
                    pixels[y * size + x] = new Color(1f, 1f, 1f, glow * fade);
                }
            }
            return Create(size, size, pixels, TextureWrapMode.Clamp);
        }

        private static Texture2D CreateSpark()
        {
            const int size = 32;
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float glow = Mathf.Exp(-r * r * 5f) * (1f - Ease(0.6f, 1f, r));
                    pixels[y * size + x] = new Color(1f, 1f, 1f, glow);
                }
            }
            return Create(size, size, pixels, TextureWrapMode.Clamp);
        }

        // Eases from 0 at a to 1 at b. Mathf.SmoothStep is different: it blends between two values, and using it
        // like this by mistake is what made the flare peak at 30%.
        private static float Ease(float a, float b, float x)
        {
            if (b <= a)
                return x < a ? 0f : 1f;
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        private static Texture2D Create(int width, int height, Color[] pixels, TextureWrapMode wrapAlong)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.SetPixels(pixels);
            texture.wrapModeU = wrapAlong;
            texture.wrapModeV = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.Apply();
            // Keep it through level loads, which unload unused assets.
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return texture;
        }
    }
}
