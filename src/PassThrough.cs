using System;
using System.Collections.Generic;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Interaction;
using UnityEngine;

namespace GravityGloves
{
    // Lets something taken out of a wrist pocket pass through the player's body where it came out inside it, until
    // it's out of it again (William). A big object comes out held the way it went in, which can put it through the arm
    // the pocket is on, and the arm would then shove it about in the hand.
    //
    // Each of its colliders that's inside a part of the body when it comes out stops colliding with that part, the
    // hands included. Once the two have been apart for ClearTime, they collide again. It starts at the physics step
    // the hand grabs it in, after the grab, so what the game ignores itself on grabbing is left to the game.
    //
    // The game also keeps a hand's colliders from colliding with what that hand holds, and lets them collide again when
    // it lets go. So a part of a hand that's holding the object is left to the game: it isn't made to collide again
    // while the hand holds it, and if the game made it collide while it's still inside, it's ignored again.
    // v0.29.0 left the hands out altogether, and the other hand still bumped big objects coming out (William).
    internal static class PassThrough
    {
        // How long a collider has to be out of a part of the body before they collide again, so it doesn't flicker.
        private const float ClearTime = 0.1f;

        // How many of the body parts it came out in to name in the log.
        private const int NamedParts = 4;

        private sealed class Pair
        {
            public Collider Part;
            public Collider Body;
            public Hand Hand;
            public float ClearSince = -1f;
        }

        private sealed class Passing
        {
            public Target Target;
            public string Name;
            public List<Collider> Colliders;
            public readonly List<Pair> Pairs = new List<Pair>();
            public bool Started;
            public float Start;
            public bool Lost;
        }

        private static readonly List<Passing> Active = new List<Passing>();
        private static readonly List<Collider> Inside = new List<Collider>();

        // The object and everything plugged into it, just taken out of a pocket.
        public static void Begin(Target target, List<MarrowEntity> entities)
        {
            var colliders = new List<Collider>();
            var seen = new HashSet<IntPtr>();
            foreach (MarrowEntity entity in entities)
            {
                if (entity == null)
                    continue;
                foreach (Collider collider in entity.GetComponentsInChildren<Collider>())
                {
                    if (collider != null && !collider.isTrigger && seen.Add(collider.Pointer))
                        colliders.Add(collider);
                }
            }
            if (colliders.Count > 0)
                Active.Add(new Passing { Target = target, Name = target.Name, Colliders = colliders });
        }

        public static void FixedUpdate()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Passing passing = Active[i];
                if (!passing.Started)
                {
                    StartPassing(passing);
                    if (passing.Pairs.Count == 0)
                        Active.RemoveAt(i);
                    continue;
                }
                if (Step(passing))
                    continue;
                Active.RemoveAt(i);
                float time = Time.time - passing.Start;
                Mod.Trace(passing.Lost
                    ? $"{passing.Name} was put away or removed {time:0.0} s after it came out inside your body"
                    : $"{passing.Name} is out of your body {time:0.0} s after it came out inside it, so it collides with it again");
            }
        }

        // Undoes what's still being passed through, for a change of level.
        public static void Clear()
        {
            foreach (Passing passing in Active)
            {
                foreach (Pair pair in passing.Pairs)
                {
                    bool heldByIt = pair.Hand != null && HeldObjects.KeyOf(pair.Hand) == passing.Target.Key;
                    if (!heldByIt && Usable(pair.Part) && Usable(pair.Body))
                        Physics.IgnoreCollision(pair.Part, pair.Body, false);
                }
            }
            Active.Clear();
        }

        private static void StartPassing(Passing passing)
        {
            passing.Started = true;
            passing.Start = Time.time;
            var names = new List<string>();
            foreach (Collider collider in passing.Colliders)
            {
                if (!Usable(collider))
                    continue;
                Inside.Clear();
                PlayerBody.Inside(collider, Inside);
                foreach (Collider body in Inside)
                {
                    if (Physics.GetIgnoreCollision(collider, body))
                        continue;
                    Physics.IgnoreCollision(collider, body, true);
                    passing.Pairs.Add(new Pair { Part = collider, Body = body, Hand = body.GetComponentInParent<Hand>() });
                    if (!names.Contains(body.name))
                        names.Add(body.name);
                }
            }
            if (passing.Pairs.Count == 0)
                return;
            string shown = string.Join(", ", names.GetRange(0, Math.Min(names.Count, NamedParts))) +
                           (names.Count > NamedParts ? $" and {names.Count - NamedParts} more" : "");
            Mod.Trace($"{passing.Name} came out inside your body ({passing.Pairs.Count} overlaps, with {shown}), " +
                        "so it passes through until it's out");
        }

        // Returns false once nothing is passed through any more.
        private static bool Step(Passing passing)
        {
            float now = Time.time;
            for (int j = passing.Pairs.Count - 1; j >= 0; j--)
            {
                Pair pair = passing.Pairs[j];
                if (!Usable(pair.Part) || !Usable(pair.Body))
                {
                    // Switched off or gone, like put back in a pocket. The physics engine forgets the ignore then.
                    passing.Pairs.RemoveAt(j);
                    passing.Lost = true;
                    continue;
                }
                bool heldByIt = pair.Hand != null && HeldObjects.KeyOf(pair.Hand) == passing.Target.Key;
                if (PlayerBody.Penetrates(pair.Part, pair.Body))
                {
                    pair.ClearSince = -1f;
                    if (!heldByIt && !Physics.GetIgnoreCollision(pair.Part, pair.Body))
                        Physics.IgnoreCollision(pair.Part, pair.Body, true);
                    continue;
                }
                if (pair.ClearSince < 0f)
                {
                    pair.ClearSince = now;
                    continue;
                }
                if (now - pair.ClearSince < ClearTime)
                    continue;
                if (!heldByIt)
                    Physics.IgnoreCollision(pair.Part, pair.Body, false);
                passing.Pairs.RemoveAt(j);
            }
            return passing.Pairs.Count > 0;
        }

        private static bool Usable(Collider collider) =>
            collider != null && collider.enabled && collider.gameObject.activeInHierarchy;
    }
}
