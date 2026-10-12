using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace GravityGloves
{
    // Small glowing sparks drifting along the line toward the hand, an experiment that's off by default. They're
    // simulated here and drawn as camera-facing quads in one mesh, with the line's shader, rather than with Unity's
    // particle system. Until v0.25 there was also a puff where a pulled object lifted off.
    internal static class Sparks
    {
        private const int MaxParticles = 256;

        // The line emits this many sparks per second at 100%, while locked and fully squeezed.
        private const float LineRate = 40f;

        private struct Particle
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public float Age;
            public float Life;
            public float StartSize;
            public float EndSize;
            public float Drag;
            public Color Color;
        }

        private static readonly Particle[] Particles = new Particle[MaxParticles];
        private static readonly Random Rng = new Random();
        private static int _count;

        // How many quads were filled in last frame. The rest are already collapsed to nothing.
        private static int _drawn;

        private static Il2CppStructArray<Vector3> _vertices;
        private static Il2CppStructArray<Color> _colors;
        private static Mesh _mesh;
        private static MeshRenderer _renderer;
        private static bool _unavailable;

        // How many sparks the line should emit per second right now. Squeeze is 0 to 1.
        public static float LineSparkRate(bool locked, float squeeze)
        {
            float amount = Settings.Sparks.Value / 100f;
            return amount <= 0f ? 0f : LineRate * amount * (locked ? 0.35f + 0.65f * squeeze : 0.25f);
        }

        // A spark leaving the line at the given point and drifting along it toward the hand.
        public static void EmitAlongLine(Vector3 position, Vector3 towardHand, Color color)
        {
            Vector3 velocity = towardHand * Range(0.4f, 1.2f) + RandomDirection() * 0.12f;
            Emit(position, velocity, Range(0.25f, 0.5f), Range(0.012f, 0.022f), 0.004f, 1.5f, color);
        }

        // A spark that goes from one point to another over the given time, shrinking as it arrives: the wrist
        // pocket's pull-in effect.
        public static void Toward(Vector3 from, Vector3 to, float time, Color color)
        {
            time = Mathf.Max(time, 0.05f);
            Emit(from, (to - from) / time, time, Range(0.008f, 0.014f), 0.002f, 0f, color);
        }

        // Moves the particles on and redraws them, facing the eye. Call once per frame.
        public static void Update(Vector3 eye)
        {
            if (_count == 0 && _drawn == 0)
                return;
            float dt = Time.unscaledDeltaTime;
            int live = 0;
            for (int i = 0; i < _count; i++)
            {
                Particle particle = Particles[i];
                particle.Age += dt;
                if (particle.Age >= particle.Life)
                    continue;
                particle.Velocity *= Mathf.Exp(-particle.Drag * dt);
                particle.Position += particle.Velocity * dt;
                Particles[live++] = particle;
            }
            _count = live;
            Draw(eye);
        }

        // A random number from 0 to 1, for spreading sparks along the line.
        public static float RandomValue() => (float)Rng.NextDouble();

        public static void Clear()
        {
            _count = 0;
            if (_renderer != null)
                _renderer.enabled = false;
        }

        private static void Emit(Vector3 position, Vector3 velocity, float life, float startSize, float endSize,
            float drag, Color color)
        {
            if (_count >= MaxParticles)
                return;
            Particles[_count++] = new Particle
            {
                Position = position,
                Velocity = velocity,
                Life = life,
                StartSize = startSize,
                EndSize = endSize,
                Drag = drag,
                Color = color,
            };
        }

        private static void Draw(Vector3 eye)
        {
            if (!EnsureCreated())
                return;
            for (int i = 0; i < _count; i++)
            {
                Particle particle = Particles[i];
                float t = particle.Age / particle.Life;
                float size = Mathf.Lerp(particle.StartSize, particle.EndSize, t);
                // A quick fade in, so they don't pop, then a fade out over their life.
                float alpha = particle.Color.a * Mathf.Min(t * 10f, 1f) * (1f - t);

                Vector3 toEye = eye - particle.Position;
                Vector3 right = Vector3.Cross(Vector3.up, toEye);
                right = right.sqrMagnitude > 1e-8f ? right.normalized : Vector3.right;
                Vector3 up = Vector3.Cross(toEye, right);
                up = up.sqrMagnitude > 1e-8f ? up.normalized : Vector3.up;
                right *= size;
                up *= size;

                int v = i * 4;
                _vertices[v] = particle.Position - right - up;
                _vertices[v + 1] = particle.Position + right - up;
                _vertices[v + 2] = particle.Position + right + up;
                _vertices[v + 3] = particle.Position - right + up;
                var color = new Color(particle.Color.r, particle.Color.g, particle.Color.b, alpha);
                for (int c = 0; c < 4; c++)
                    _colors[v + c] = color;
            }
            // Particles that died since last frame collapse to nothing.
            for (int i = _count; i < _drawn; i++)
            {
                int v = i * 4;
                for (int c = 0; c < 4; c++)
                {
                    _vertices[v + c] = Vector3.zero;
                    _colors[v + c] = Color.clear;
                }
            }
            _drawn = _count;

            _mesh.vertices = _vertices;
            _mesh.colors = _colors;
            // Large enough to never be culled. The quads are in world space.
            _mesh.bounds = new Bounds(eye, Vector3.one * 2000f);
            _renderer.enabled = _count > 0;
        }

        private static bool EnsureCreated()
        {
            if (_unavailable)
                return false;
            if (_mesh != null && _renderer != null)
                return true;

            Material material = LineFactory.CreateMaterial(GlowTextures.Spark);
            if (material == null)
            {
                _unavailable = true;
                return false;
            }
            try
            {
                _vertices = new Il2CppStructArray<Vector3>(MaxParticles * 4);
                _colors = new Il2CppStructArray<Color>(MaxParticles * 4);
                var uvs = new Il2CppStructArray<Vector2>(MaxParticles * 4);
                var triangles = new Il2CppStructArray<int>(MaxParticles * 6);
                for (int i = 0; i < MaxParticles; i++)
                {
                    int v = i * 4;
                    uvs[v] = new Vector2(0f, 0f);
                    uvs[v + 1] = new Vector2(1f, 0f);
                    uvs[v + 2] = new Vector2(1f, 1f);
                    uvs[v + 3] = new Vector2(0f, 1f);
                    int t = i * 6;
                    triangles[t] = v;
                    triangles[t + 1] = v + 2;
                    triangles[t + 2] = v + 1;
                    triangles[t + 3] = v;
                    triangles[t + 4] = v + 3;
                    triangles[t + 5] = v + 2;
                }

                _mesh = new Mesh { name = "Gravity Gloves Sparks" };
                _mesh.MarkDynamic();
                _mesh.vertices = _vertices;
                _mesh.colors = _colors;
                _mesh.uv = uvs;
                _mesh.triangles = triangles;
                _mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _drawn = 0;

                var sparksObject = new GameObject("Gravity Gloves Sparks");
                Object.DontDestroyOnLoad(sparksObject);
                sparksObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
                _renderer = sparksObject.AddComponent<MeshRenderer>();
                _renderer.sharedMaterial = material;
                _renderer.shadowCastingMode = ShadowCastingMode.Off;
                _renderer.receiveShadows = false;
                _renderer.enabled = false;
                return true;
            }
            catch (Exception e)
            {
                _unavailable = true;
                Mod.Log.Error($"Couldn't set up the sparks, so they're off: {e}");
                return false;
            }
        }

        private static float Range(float min, float max) => min + (float)Rng.NextDouble() * (max - min);

        private static Vector3 RandomDirection()
        {
            Vector3 direction = new Vector3(Range(-1f, 1f), Range(-1f, 1f), Range(-1f, 1f));
            return direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.up;
        }
    }
}
