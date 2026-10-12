using System;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Utilities;
using UnityEngine;

namespace GravityGloves
{
    // How a hand holds a handle (a cylinder grip): its target in the grip's own frame, and the grip's per-hand
    // state, which keeps where along the handle and how far round it the hand holds it. Noted while an object is
    // held, and put back when it's taken out of a wrist pocket.
    //
    // Which way round along the handle the hand holds it (isZFlipped) is only noted. The grab decides that itself
    // from how the hand is turned against the handle, and makes the hand's joint for it at once (see
    // GrabAssist.BeginHover), so setting it afterwards can't turn the object round, only leave the grip's state
    // disagreeing with its joint. v0.28 did set it: every take-out where it changed it, 11 of 17 in the v0.28 test,
    // came out reversed.
    internal sealed class GripHold
    {
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private bool _zFlipped;
        private float _posInFlippedBase;
        private float _rotInFlippedBase;
        private float _dynamicFloat;
        private Quaternion _targetRotationInBase;

        private GripHold()
        {
        }

        // How the hand holds the grip now, or null if it isn't a handle or can't be read.
        public static GripHold Capture(Grip grip, Hand hand)
        {
            if (grip == null || hand == null || grip.TryCast<CylinderGrip>() == null)
                return null;
            try
            {
                HandToGripState state = grip.GetHandState(hand);
                if (state == null)
                    return null;
                SimpleTransform target = grip.GetTargetInBase(hand);
                return new GripHold
                {
                    _targetPosition = target.position,
                    _targetRotation = target.rotation,
                    _zFlipped = state.isZFlipped,
                    _posInFlippedBase = state.posInFlippedBase,
                    _rotInFlippedBase = state.rotInFlippedBase,
                    _dynamicFloat = state.dynamicFloat,
                    _targetRotationInBase = state.targetRotationInBase,
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Puts the hold back on a hand that has just grabbed the grip, if the grab has it the same way round as it
        // was noted. If not, the noted state didn't match how the object was really held, and the grab, worked out
        // from the object where it is, goes. Says what it did, for the log.
        public string Apply(Grip grip, Hand hand)
        {
            HandToGripState state = grip.GetHandState(hand);
            if (state == null)
                return "the grip had no state for the hand";
            if (state.isZFlipped != _zFlipped)
            {
                return $"the handle's hold not put back: the grab holds it {Way(state.isZFlipped)}, but it was noted " +
                       $"as held {Way(_zFlipped)}";
            }
            grip.SetTargetInBase(hand, _targetPosition, _targetRotation);
            state.posInFlippedBase = _posInFlippedBase;
            state.rotInFlippedBase = _rotInFlippedBase;
            state.dynamicFloat = _dynamicFloat;
            state.targetRotationInBase = _targetRotationInBase;
            grip.UpdateJointConfiguration(hand);
            return $"the handle's hold put back, held {Way(_zFlipped)}";
        }

        private static string Way(bool zFlipped) => zFlipped ? "the other way round (z-flipped)" : "the handle's own way round";
    }
}
