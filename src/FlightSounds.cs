using System.Collections.Generic;
using UnityEngine;

namespace GravityGloves
{
    // The whoosh of a pulled object, played from the object itself so you hear it coming. It gets louder and
    // higher the faster the object moves, and fades out once the flight is over.
    internal static class FlightSounds
    {
        // How quickly it fades: at once when caught, gently when the flight just ends.
        private const float CaughtRelease = 0.04f;
        private const float EndRelease = 0.25f;
        private const float Smoothing = 0.08f;

        private sealed class Voice
        {
            public Target Target;
            public LoopSound Sound;
            public Vector3 Position;
            public bool Ending;
            public float Release;
        }

        private static readonly List<Voice> Active = new List<Voice>();
        private static readonly Stack<LoopSound> Spare = new Stack<LoopSound>();

        public static void Start(Target target)
        {
            if (Settings.FlightWhoosh.Value <= 0 || Sounds.Volume <= 0f)
                return;
            foreach (Voice voice in Active)
            {
                if (voice.Target == target)
                {
                    voice.Ending = false;
                    return;
                }
            }
            LoopSound sound = Spare.Count > 0 ? Spare.Pop() : new LoopSound("Gravity Gloves Whoosh", () => Sounds.Whoosh, 0.6f);
            Active.Add(new Voice { Target = target, Sound = sound, Position = target.CenterOfMass });
        }

        // Fades out the object's whoosh, quickly if it was caught.
        public static void Stop(Target target, bool caught)
        {
            foreach (Voice voice in Active)
            {
                if (voice.Target == target && !voice.Ending)
                {
                    voice.Ending = true;
                    voice.Release = caught ? CaughtRelease : EndRelease;
                }
            }
        }

        public static void Update()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Voice voice = Active[i];
                Target target = voice.Target;
                bool present = target.IsAlive && target.IsActive;
                // However the flight ended, the whoosh ends with it.
                if (!voice.Ending && (!present || !Flights.IsFlying(target)))
                {
                    voice.Ending = true;
                    voice.Release = EndRelease;
                }

                float volume = 0f;
                float pitch = 1f;
                if (present)
                {
                    voice.Position = target.CenterOfMass;
                    float rush = Mathf.Clamp01((target.Velocity.magnitude - 0.5f) / 8f);
                    pitch = 0.8f + 0.5f * rush;
                    if (!voice.Ending)
                        volume = 0.7f * Mathf.Sqrt(rush) * Settings.FlightWhoosh.Value / 100f * Sounds.Volume;
                }
                voice.Sound.Update(voice.Position, volume, pitch, voice.Ending ? voice.Release : Smoothing);

                if (voice.Ending && voice.Sound.IsSilent)
                {
                    Active.RemoveAt(i);
                    Spare.Push(voice.Sound);
                }
            }
        }

        public static void Clear()
        {
            foreach (Voice voice in Active)
            {
                voice.Sound.Silence();
                Spare.Push(voice.Sound);
            }
            Active.Clear();
        }
    }
}
