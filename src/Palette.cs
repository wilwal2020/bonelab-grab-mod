using UnityEngine;

namespace GravityGloves
{
    // The colour setting. The colours in the code are Alyx orange, and this turns them to the chosen hue and
    // saturation, keeping how they differ from each other (the line's core is paler than its glow, for example).
    internal static class Palette
    {
        // The hue of the colours in the code, in degrees.
        private const float BaseHue = 30f;

        public static Color Apply(Color color) =>
            Apply(color, Settings.ColourHue.Value, Settings.ColourSaturation.Value);

        // The wrist pockets' colours: their own colour if they have one, otherwise the glove's.
        public static Color Pocket(Color color) => Settings.PocketOwnColour.Value
            ? Apply(color, Settings.PocketHue.Value, Settings.PocketSaturation.Value)
            : Apply(color);

        private static Color Apply(Color color, int hue, int saturationPercent)
        {
            float shift = (hue - BaseHue) / 360f;
            float saturation = saturationPercent / 100f;
            if (shift == 0f && saturation >= 1f)
                return color;

            ToHsv(color, out float h, out float s, out float v);
            Color result = FromHsv(Mathf.Repeat(h + shift, 1f), s * saturation, v);
            result.a = color.a;
            return result;
        }

        private static void ToHsv(Color color, out float h, out float s, out float v)
        {
            float max = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            float min = Mathf.Min(color.r, Mathf.Min(color.g, color.b));
            float range = max - min;
            v = max;
            s = max > 0f ? range / max : 0f;
            if (range <= 0f)
                h = 0f;
            else if (max == color.r)
                h = Mathf.Repeat((color.g - color.b) / range / 6f, 1f);
            else if (max == color.g)
                h = ((color.b - color.r) / range + 2f) / 6f;
            else
                h = ((color.r - color.g) / range + 4f) / 6f;
        }

        private static Color FromHsv(float h, float s, float v)
        {
            float sector = h * 6f;
            int i = Mathf.FloorToInt(sector) % 6;
            float f = sector - Mathf.Floor(sector);
            float p = v * (1f - s);
            float q = v * (1f - s * f);
            float t = v * (1f - s * (1f - f));
            switch (i)
            {
                case 0: return new Color(v, t, p);
                case 1: return new Color(q, v, p);
                case 2: return new Color(p, v, t);
                case 3: return new Color(p, q, v);
                case 4: return new Color(t, p, v);
                default: return new Color(v, p, q);
            }
        }
    }
}
