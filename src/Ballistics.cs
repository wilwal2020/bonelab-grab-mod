using UnityEngine;

namespace GravityGloves
{
    internal static class Ballistics
    {
        // Velocity that carries a body from start to target in the given time under constant gravity.
        // From target = start + v*t + 0.5*g*t^2.
        public static Vector3 LaunchVelocity(Vector3 start, Vector3 target, Vector3 gravity, float time)
        {
            return (target - start - 0.5f * time * time * gravity) / time;
        }
    }
}
