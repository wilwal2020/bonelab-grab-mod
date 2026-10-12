using System;
using Il2CppSLZ.Bonelab;
using Il2CppSLZ.Marrow.Data;
using Il2CppSLZ.Marrow.Pool;
using UnityEngine;

namespace GravityGloves
{
    // A spawn gun shows what it will spawn on a screen (SpawnGunUI: a camera far off at (-9999, 9999, -9999) that
    // renders the preview), which the first spawn gun in a level spawns in its Start if there's none. A spawn gun
    // carried into a new level in a wrist pocket has had its Start, so unless the new level has spawn guns of its
    // own there's no screen, and the gun throws every time it's grabbed or let go (SpawnGun.OnTriggerGripAttached
    // and OnTriggerGripDetached use SpawnGunUI.Instance). In the v0.33.2 test that broke every grab of a carried
    // SPAWN_GUN: 28 errors, and the hand held it on no grip ("held by nothing"). So a carried spawn gun spawns the
    // screen the same way, a moment after the level loads, and again when it comes out of the pocket if there's
    // still none.
    internal static class SpawnGunScreen
    {
        // After the level loads, so the level's own spawn guns spawn their screen first.
        private const float LevelDelay = 1f;

        private static readonly Vector3 ScreenPosition = new Vector3(-9999f, 9999f, -9999f);

        private static SpawnGun _carried;
        private static float _checkAt = -1f;

        // A pocket's object came along to a new level: if it's a spawn gun, the screen is checked for shortly.
        public static void CameAlong(GameObject root)
        {
            SpawnGun gun = Find(root);
            if (gun == null)
                return;
            _carried = gun;
            _checkAt = Time.unscaledTime + LevelDelay;
        }

        public static void Update()
        {
            if (_checkAt < 0f || Time.unscaledTime < _checkAt)
                return;
            _checkAt = -1f;
            SpawnGun gun = _carried;
            _carried = null;
            Ensure(gun);
        }

        // Spawns the screen for a spawn gun if the level has none.
        public static void Ensure(GameObject root) => Ensure(Find(root));

        private static void Ensure(SpawnGun gun)
        {
            try
            {
                if (gun == null || SpawnGunUI.Instance != null)
                    return;
                Spawnable screen = gun._spawnGunUISpawnable;
                if (screen == null)
                    return;
                // As SpawnGun.Start does.
                AssetSpawner.Register(screen);
                AssetSpawner.Spawn(screen, ScreenPosition, Quaternion.identity, new Il2CppSystem.Nullable<Vector3>(),
                    null, false, new Il2CppSystem.Nullable<int>(), null, null);
                Mod.Trace($"This level had no spawn gun screen, so one was spawned for {gun.name}, which came from another level");
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"Couldn't spawn a screen for the spawn gun: {e.Message}");
            }
        }

        private static SpawnGun Find(GameObject root) =>
            root != null ? root.GetComponentInChildren<SpawnGun>(true) : null;
    }
}
