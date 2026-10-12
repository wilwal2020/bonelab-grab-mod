using System.Collections.Generic;
using UnityEngine;

namespace GravityGloves
{
    // Velocity and angular velocity from recent poses, measured over a short window to smooth out tracking jitter.
    internal sealed class MotionTracker
    {
        private const float Window = 0.05f;
        private const float MinSpan = 0.005f;

        private struct Sample
        {
            public float Time;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private readonly Queue<Sample> _samples = new Queue<Sample>();

        // Both are in the space the samples were given in. Angular velocity is the rotation axis
        // scaled by degrees per second.
        public Vector3 Velocity { get; private set; }
        public Vector3 AngularVelocity { get; private set; }

        public void AddSample(float time, Vector3 position, Quaternion rotation)
        {
            _samples.Enqueue(new Sample { Time = time, Position = position, Rotation = rotation });
            while (_samples.Count > 2 && time - _samples.Peek().Time > Window)
                _samples.Dequeue();

            Sample oldest = _samples.Peek();
            float span = time - oldest.Time;
            if (span < MinSpan)
            {
                Velocity = Vector3.zero;
                AngularVelocity = Vector3.zero;
                return;
            }
            Velocity = (position - oldest.Position) / span;
            AngularVelocity = RotationMath.ToAngleAxis(rotation * Quaternion.Inverse(oldest.Rotation)) / span;
        }

        public void Clear()
        {
            _samples.Clear();
            Velocity = Vector3.zero;
            AngularVelocity = Vector3.zero;
        }
    }
}
