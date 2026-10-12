using System;
using System.Collections.Generic;
using BoneLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace GravityGloves
{
    // The player's own colliders, for checking whether something would come out inside the player's body. They're
    // gathered from the player rig and kept for a while, since the rig keeps its colliders between frames.
    internal static class PlayerBody
    {
        private const float RefreshInterval = 5f;

        private static readonly HashSet<IntPtr> Colliders = new HashSet<IntPtr>();
        private static readonly Il2CppReferenceArray<Collider> Found = new Il2CppReferenceArray<Collider>(128);
        private static IntPtr _rig;
        private static int _mask;
        private static float _nextRefresh;

        // Whether a box (centre, half sizes and turn in the world) overlaps any part of the player's body.
        public static bool Overlaps(Vector3 center, Vector3 halfExtents, Quaternion rotation)
        {
            Refresh();
            if (_mask == 0)
                return false;
            int count = Physics.OverlapBoxNonAlloc(center, halfExtents, Found, rotation, _mask,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider collider = Found[i];
                if (collider != null && Colliders.Contains(collider.Pointer))
                    return true;
            }
            return false;
        }

        // The parts of the player's body that a collider is inside of now.
        public static void Inside(Collider collider, List<Collider> into)
        {
            Refresh();
            if (_mask == 0)
                return;
            Bounds bounds = collider.bounds;
            int count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, Found, Quaternion.identity, _mask,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider part = Found[i];
                if (part != null && Colliders.Contains(part.Pointer) && Penetrates(collider, part))
                {
                    into.Add(part);
                }
            }
        }

        // Whether two colliders are inside each other where they are now.
        public static bool Penetrates(Collider a, Collider b)
        {
            Transform ta = a.transform;
            Transform tb = b.transform;
            return Physics.ComputePenetration(a, ta.position, ta.rotation, b, tb.position, tb.rotation, out _, out _);
        }

        public static void Clear()
        {
            Colliders.Clear();
            _rig = IntPtr.Zero;
            _mask = 0;
            _nextRefresh = 0f;
        }

        private static void Refresh()
        {
            RigManager rig = Player.RigManager;
            IntPtr pointer = rig != null ? rig.Pointer : IntPtr.Zero;
            if (pointer == _rig && Time.unscaledTime < _nextRefresh)
                return;
            _rig = pointer;
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            Colliders.Clear();
            _mask = 0;
            if (rig == null)
                return;
            foreach (Collider collider in rig.GetComponentsInChildren<Collider>())
            {
                if (collider == null || collider.isTrigger || !collider.enabled)
                    continue;
                Colliders.Add(collider.Pointer);
                _mask |= 1 << collider.gameObject.layer;
            }
        }
    }
}
