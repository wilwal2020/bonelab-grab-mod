using System;
using System.Collections.Generic;
using Il2CppSLZ.Marrow.Interaction;
using UnityEngine;

namespace GravityGloves
{
    // Targets are shared by both hands and cached per rigidbody, since building one walks the whole entity.
    internal static class TargetCache
    {
        private const float CleanupInterval = 10f;

        private static readonly Dictionary<IntPtr, Target> ByBody = new Dictionary<IntPtr, Target>();
        private static readonly Dictionary<IntPtr, Target> ByKey = new Dictionary<IntPtr, Target>();
        private static readonly List<IntPtr> DeadKeys = new List<IntPtr>();
        private static float _nextCleanup;

        public static Target Get(Rigidbody rb)
        {
            IntPtr pointer = rb.Pointer;
            if (ByBody.TryGetValue(pointer, out Target cached) && cached.IsAlive)
                return cached;

            MarrowBody body = rb.GetComponent<MarrowBody>();
            MarrowEntity entity = body != null ? body.Entity : null;
            if (entity == null)
                entity = rb.GetComponentInParent<MarrowEntity>();

            IntPtr key = entity != null ? entity.Pointer : pointer;
            if (!ByKey.TryGetValue(key, out Target target) || !target.IsAlive)
            {
                target = new Target(rb, entity);
                ByKey[key] = target;
            }
            ByBody[pointer] = target;
            return target;
        }

        public static void Clear()
        {
            ByBody.Clear();
            ByKey.Clear();
        }

        public static void CleanupIfDue(float now)
        {
            if (now < _nextCleanup)
                return;
            _nextCleanup = now + CleanupInterval;
            RemoveDead(ByBody);
            RemoveDead(ByKey);
        }

        private static void RemoveDead(Dictionary<IntPtr, Target> targets)
        {
            DeadKeys.Clear();
            foreach (KeyValuePair<IntPtr, Target> pair in targets)
            {
                if (!pair.Value.IsAlive)
                    DeadKeys.Add(pair.Key);
            }
            foreach (IntPtr key in DeadKeys)
                targets.Remove(key);
        }
    }
}
