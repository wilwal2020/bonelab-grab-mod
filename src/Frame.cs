using Il2CppSLZ.Marrow.Utilities;
using UnityEngine;

namespace GravityGloves
{
    // A position and rotation, for chaining poses without going through Transforms.
    internal readonly struct Frame
    {
        public Frame(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }

        public Vector3 Position { get; }
        public Quaternion Rotation { get; }

        public bool IsValid
        {
            get
            {
                if (!float.IsFinite(Position.x) || !float.IsFinite(Position.y) || !float.IsFinite(Position.z))
                    return false;
                Quaternion q = Rotation;
                float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                return float.IsFinite(norm) && norm > 0.8f && norm < 1.2f;
            }
        }

        public static Frame Of(Transform transform) => new Frame(transform.position, transform.rotation);

        public static Frame Of(SimpleTransform transform) => new Frame(transform.position, transform.rotation);

        // This frame followed by a pose given relative to it.
        public Frame Then(Frame local) => new Frame(Position + Rotation * local.Position, Rotation * local.Rotation);

        public Frame Inverse()
        {
            Quaternion inverse = Quaternion.Inverse(Rotation);
            return new Frame(inverse * -Position, inverse);
        }

        public Vector3 TransformPoint(Vector3 point) => Position + Rotation * point;

        public override string ToString()
        {
            Vector3 p = Position * 100f;
            Vector3 r = Rotation.eulerAngles;
            return $"({p.x:0}, {p.y:0}, {p.z:0}) cm / ({r.x:0}, {r.y:0}, {r.z:0}) deg";
        }
    }
}
