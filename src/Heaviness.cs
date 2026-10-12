using UnityEngine;

namespace GravityGloves
{
    // How heavy an object feels in flight and on landing, scaled by the Weight setting. Guns and other light
    // things aren't affected, and the effect grows with the logarithm of the mass, so it keeps building from a
    // chair (8-10 kg) to a barrel (15-18 kg) to a bench (24 kg).
    internal static class Heaviness
    {
        private const float LightMass = 3f;
        private const float HeavyMass = 30f;

        // 0 for things up to LightMass, rising to the Weight setting (1 at 100%) at HeavyMass and above.
        public static float Of(float mass)
        {
            float weight = Settings.Weight.Value / 100f;
            if (weight <= 0f || mass <= LightMass)
                return 0f;
            return weight * Mathf.Clamp01(Mathf.Log(mass / LightMass) / Mathf.Log(HeavyMass / LightMass));
        }

        // How much longer a heavy object's flight takes.
        public static float TimeScale(float heaviness) => 1f + 0.6f * heaviness;

        // How high its arc is, compared with a light object's over the same distance.
        public static float ArcScale(float heaviness) => 1f / (1f + 0.8f * heaviness);
    }
}
