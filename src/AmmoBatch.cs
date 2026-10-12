using System;
using System.Collections.Generic;
using BoneLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSLZ.Bonelab;
using UnityEngine;

namespace GravityGloves
{
    // Ammo pickups lying close together are selected and pulled as a batch, and collected together:
    // as soon as one of them is picked up, the rest are too.
    internal static class AmmoBatch
    {
        private const int MaxBatchSize = 8;

        // How long after the last of a batch should have arrived to keep waiting for one to be picked up.
        private const float CollectWindow = 3f;

        private const int GatherMask = (int)(GameLayers.DYNAMIC | GameLayers.INTERACTABLE);

        private sealed class Batch
        {
            public readonly List<Target> Members = new List<Target>();
            public float Expires;
        }

        private static readonly Il2CppReferenceArray<Collider> Overlaps = new Il2CppReferenceArray<Collider>(64);
        private static readonly HashSet<IntPtr> Seen = new HashSet<IntPtr>();
        private static readonly List<Batch> Pending = new List<Batch>();

        // Fills the list with the other ammo pickups near the given one, not including it.
        // It stays empty if batching is off or the given object isn't an ammo pickup.
        public static void Find(Target primary, List<Target> batch)
        {
            batch.Clear();
            if (primary == null || !Settings.BatchAmmo.Value || primary.AmmoPickup == null)
                return;

            HeldObjects held = HeldObjects.Read();
            int count = Physics.OverlapSphereNonAlloc(primary.CenterOfMass, Settings.BatchRadius.Value, Overlaps,
                GatherMask, QueryTriggerInteraction.Ignore);
            Seen.Clear();
            Seen.Add(primary.Key);
            for (int i = 0; i < count && batch.Count < MaxBatchSize - 1; i++)
            {
                Collider collider = Overlaps[i];
                Rigidbody rb = collider != null ? collider.attachedRigidbody : null;
                if (rb == null || rb.isKinematic)
                    continue;
                Target target = TargetCache.Get(rb);
                if (!Seen.Add(target.Key))
                    continue;
                AmmoPickup pickup = target.AmmoPickup;
                if (pickup == null || pickup._isCollected || held.Contains(target) || !Flights.CanPull(target) ||
                    target.Mass > Settings.MaxMass.Value)
                {
                    continue;
                }
                batch.Add(target);
            }
        }

        // Starts watching a launched batch, so picking up any one of it collects the rest.
        public static void Track(List<Target> members, float longestFlight)
        {
            var batch = new Batch { Expires = Time.time + longestFlight + CollectWindow };
            batch.Members.AddRange(members);
            Pending.Add(batch);
        }

        public static void FixedUpdate()
        {
            if (Pending.Count == 0)
                return;
            HeldObjects held = HeldObjects.Read();
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                Batch batch = Pending[i];
                if (IsAnyPickedUp(batch, held))
                {
                    Pending.RemoveAt(i);
                    int collected = CollectAll(batch);
                    if (collected > 0)
                        Mod.Trace($"Collected {collected} more ammo pickups with the batch");
                }
                else if (Time.time > batch.Expires)
                {
                    Pending.RemoveAt(i);
                }
            }
        }

        public static void Clear() => Pending.Clear();

        // Picked up means collected by the game, or grabbed by the player.
        private static bool IsAnyPickedUp(Batch batch, HeldObjects held)
        {
            foreach (Target member in batch.Members)
            {
                if (!member.IsAlive)
                    continue;
                AmmoPickup pickup = member.AmmoPickup;
                if (pickup != null && (pickup._isCollected || held.Contains(member)))
                    return true;
            }
            return false;
        }

        private static int CollectAll(Batch batch)
        {
            int collected = 0;
            foreach (Target member in batch.Members)
            {
                if (!member.IsAlive)
                    continue;
                AmmoPickup pickup = member.AmmoPickup;
                if (pickup == null || pickup._isCollected)
                    continue;
                try
                {
                    pickup.ForcePickup();
                    collected++;
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"Couldn't collect {member.Name} with its batch: {e.Message}");
                }
            }
            return collected;
        }
    }
}
