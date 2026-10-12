using UnityEngine;

namespace GravityGloves
{
    internal static class RotationMath
    {
        // A rotation as its axis scaled by the angle in degrees, taking the short way round.
        public static Vector3 ToAngleAxis(Quaternion rotation)
        {
            // q and -q are the same rotation.
            float sign = rotation.w < 0f ? -1f : 1f;
            Vector3 axis = new Vector3(rotation.x, rotation.y, rotation.z) * sign;
            float sinHalfAngle = axis.magnitude;
            if (sinHalfAngle < 1e-6f)
                return Vector3.zero;
            float angle = 2f * Mathf.Atan2(sinHalfAngle, rotation.w * sign) * Mathf.Rad2Deg;
            return axis / sinHalfAngle * angle;
        }
    }
}
