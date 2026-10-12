using System;
using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Interaction;
using UnityEngine;

namespace GravityGloves
{
    // What the player's hands are holding, as Target keys.
    internal readonly struct HeldObjects
    {
        private readonly IntPtr _left;
        private readonly IntPtr _right;

        private HeldObjects(IntPtr left, IntPtr right)
        {
            _left = left;
            _right = right;
        }

        public static HeldObjects Read() => new HeldObjects(KeyOf(Player.LeftHand), KeyOf(Player.RightHand));

        public bool Contains(Target target) => target.Key == _left || target.Key == _right;

        // The hand holding the target, or null.
        public Hand HandHolding(Target target)
        {
            if (target.Key == _right)
                return Player.RightHand;
            return target.Key == _left ? Player.LeftHand : null;
        }

        // The same key a Target gets for whatever this hand holds: its entity, or its rigidbody if it has no entity.
        public static IntPtr KeyOf(Hand hand)
        {
            if (hand == null)
                return IntPtr.Zero;
            GameObject held = Player.GetObjectInHand(hand);
            if (held == null)
                return IntPtr.Zero;
            MarrowEntity entity = held.GetComponentInParent<MarrowEntity>();
            if (entity != null)
                return entity.Pointer;
            Rigidbody rb = held.GetComponentInParent<Rigidbody>();
            return rb != null ? rb.Pointer : IntPtr.Zero;
        }
    }
}
