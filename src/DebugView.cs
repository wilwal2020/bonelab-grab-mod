using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace GravityGloves
{
    // Debug lines for tuning: each hand's aim cone, and each flight's planned path (drawn by Flights).
    internal static class DebugView
    {
        private const int RimSegments = 24;

        // The apex, the rim loop back to its start, then apex and end point for three spokes and the centre line.
        private const int ConePoints = 1 + (RimSegments + 1) + 4 * 2;
        private const float Width = 0.015f;

        private static readonly LineRenderer[] Cones = new LineRenderer[2];
        private static Il2CppStructArray<Vector3> _conePoints;

        public static LineRenderer CreateLine(string name, int points, Color color)
        {
            LineRenderer line = LineFactory.Create(name, points);
            if (line != null)
            {
                line.startWidth = Width;
                line.endWidth = Width;
                line.startColor = color;
                line.endColor = color;
            }
            return line;
        }

        public static void ShowCone(int index, Vector3 apex, Vector3 axis, float angle, float range)
        {
            if (Cones[index] == null)
                Cones[index] = CreateLine("Gravity Gloves Cone", ConePoints, new Color(0.2f, 0.8f, 1f));
            LineRenderer cone = Cones[index];
            if (cone == null)
                return;
            _conePoints ??= new Il2CppStructArray<Vector3>(ConePoints);

            Vector3 side = Vector3.Cross(axis, Vector3.up);
            if (side.sqrMagnitude < 1e-4f)
                side = Vector3.Cross(axis, Vector3.right);
            side.Normalize();
            Vector3 up = Vector3.Cross(side, axis);
            Vector3 center = apex + axis * range;
            float radius = range * Mathf.Tan(angle * Mathf.Deg2Rad);

            Vector3 Rim(int i)
            {
                float a = 2f * Mathf.PI * i / RimSegments;
                return center + (side * Mathf.Cos(a) + up * Mathf.Sin(a)) * radius;
            }

            int n = 0;
            _conePoints[n++] = apex;
            for (int i = 0; i <= RimSegments; i++)
                _conePoints[n++] = Rim(i);
            // Three spokes to the rim, then the centre line.
            for (int spoke = 0; spoke < 4; spoke++)
            {
                _conePoints[n++] = apex;
                _conePoints[n++] = spoke < 3 ? Rim((spoke + 1) * RimSegments / 4) : center;
            }

            cone.SetPositions(_conePoints);
            cone.enabled = true;
        }

        public static void HideCone(int index)
        {
            LineRenderer cone = Cones[index];
            if (cone != null)
                cone.enabled = false;
        }
    }
}
