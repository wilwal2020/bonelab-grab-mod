using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSLZ.Marrow.Interaction;
using Il2CppSLZ.Marrow.Utilities;
using Object = UnityEngine.Object;

namespace GravityGloves
{
    // The game's culling (InactiveObjectManager) calls MarrowEntity.OnCullResolve when an object is culled or comes
    // back, and that goes through the object's parts (_behaviours) and what registered to be culled with it
    // (_cullables). An entry that's been destroyed makes it throw a NullReferenceException, which it catches and logs.
    // In the v0.32 and v0.33.1 tests it did that about once a second in every level something was carried into from
    // a wrist pocket (1308 times in the v0.33.1 test), and cleaning the carried objects at the level load didn't stop
    // it. So just before each call this looks for such entries, takes them out, and says which object had them and
    // whether it's been in a pocket, so the log shows whose they are.
    [HarmonyPatch(typeof(MarrowEntity), nameof(MarrowEntity.OnCullResolve))]
    internal static class CullResolvePatch
    {
        private static void Prefix(MarrowEntity __instance) => CullCheck.Clean(__instance);
    }

    internal static class CullCheck
    {
        // How many objects are named in the log per level; the rest are only counted.
        private const int MaxNamed = 20;

        private static readonly HashSet<IntPtr> Named = new HashSet<IntPtr>();
        private static int _objects;
        private static int _entries;
        private static int _pocketObjects;
        private static bool _loggedError;

        public static void Clean(MarrowEntity entity)
        {
            // Called from inside the game's culling, so nothing may escape from here.
            try
            {
                if (entity == null)
                    return;
                var dead = new List<string>();
                Il2CppReferenceArray<MarrowBehaviour> behaviours = entity._behaviours;
                if (behaviours != null)
                {
                    List<MarrowBehaviour> alive = null;
                    for (int i = 0; i < behaviours.Length; i++)
                    {
                        MarrowBehaviour part = behaviours[i];
                        if (part != null)
                        {
                            alive?.Add(part);
                            continue;
                        }
                        if (alive == null)
                        {
                            alive = new List<MarrowBehaviour>();
                            for (int j = 0; j < i; j++)
                                alive.Add(behaviours[j]);
                        }
                        dead.Add(TypeOf(part));
                    }
                    if (alive != null)
                        entity._behaviours = new Il2CppReferenceArray<MarrowBehaviour>(alive.ToArray());
                }
                var cullables = entity._cullables;
                if (cullables != null)
                {
                    for (int i = cullables.Count - 1; i >= 0; i--)
                    {
                        Il2CppObjectBase entry = cullables[i];
                        if (!IsDead(entry))
                            continue;
                        dead.Add(TypeOf(entry));
                        cullables.RemoveAt(i);
                    }
                }
                if (dead.Count > 0)
                    Report(entity, dead);
            }
            catch (Exception e)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    Mod.Log.Warning($"Couldn't check an object before the game culled it: {e.Message}");
                }
            }
        }

        // At a change of level: how many objects in the last one had dead entries, if any did.
        public static void Clear()
        {
            if (_objects > 0)
            {
                Mod.Trace($"In that level {_objects} objects had parts that were gone ({_entries} in all, " +
                            $"{_pocketObjects} of the objects from a wrist pocket), taken out before the game culled them");
            }
            Named.Clear();
            _objects = 0;
            _entries = 0;
            _pocketObjects = 0;
        }

        private static void Report(MarrowEntity entity, List<string> dead)
        {
            string pocket = WristPockets.HistoryOf(entity);
            if (Named.Add(entity.Pointer))
            {
                _objects++;
                if (pocket != null)
                    _pocketObjects++;
            }
            _entries += dead.Count;
            if (Named.Count > MaxNamed)
                return;
            var kinds = new Dictionary<string, int>();
            foreach (string kind in dead)
                kinds[kind] = kinds.TryGetValue(kind, out int n) ? n + 1 : 1;
            var shown = new List<string>();
            foreach (var pair in kinds)
                shown.Add(pair.Value > 1 ? $"{pair.Value} {pair.Key}" : pair.Key);
            Mod.Trace($"{entity.name} ({pocket ?? "never in a pocket"}) had parts that were gone, which would have made " +
                        $"the game's culling throw: {string.Join(", ", shown)}. Took them out");
        }

        // A destroyed Unity object still has its wrapper, so it's asked whether it's still there.
        private static bool IsDead(Il2CppObjectBase entry)
        {
            if (ReferenceEquals(entry, null))
                return true;
            Object unity = entry.TryCast<Object>();
            return !ReferenceEquals(unity, null) && unity == null;
        }

        private static string TypeOf(Il2CppObjectBase entry)
        {
            if (ReferenceEquals(entry, null))
                return "empty slot";
            try
            {
                Il2CppSystem.Object managed = entry.TryCast<Il2CppSystem.Object>();
                return ReferenceEquals(managed, null) ? "unknown" : managed.GetIl2CppType().Name;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
