using System;
using HarmonyLib;
using Il2CppSLZ.Marrow.Warehouse;

namespace GravityGloves
{
    // On a change of level the game unloads every crate the new level doesn't need (StreamSession's Load calls
    // AssetWarehouse.UnloadAllCrateAssets, which unloads each crate's assets with UnloadCrateAsset). Something in a
    // wrist pocket is kept out of the level so it comes along, but it, and the pocket's see-through copy, are drawn
    // with its crate's meshes, so once the crate was unloaded neither had anything to show: in the v0.32 test William
    // found the pockets looked empty after a level change, though the objects had come along. So the crates of what's
    // in the pockets are skipped.
    [HarmonyPatch(typeof(AssetWarehouse), nameof(AssetWarehouse.UnloadCrateAsset), new[] { typeof(Crate), typeof(bool) })]
    internal static class KeepPocketCratePatch
    {
        private static bool Prefix(Crate crate, ref bool __result)
        {
            string id;
            try
            {
                Barcode barcode = crate != null ? crate.Barcode : null;
                id = barcode != null ? barcode.ID : null;
            }
            catch (Exception)
            {
                return true;
            }
            if (!WristPockets.KeepsCrate(id))
                return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(AssetWarehouse), nameof(AssetWarehouse.UnloadCrateAsset), new[] { typeof(Barcode), typeof(bool) })]
    internal static class KeepPocketCrateByBarcodePatch
    {
        private static bool Prefix(Barcode barcode, ref bool __result)
        {
            string id;
            try
            {
                id = barcode != null ? barcode.ID : null;
            }
            catch (Exception)
            {
                return true;
            }
            if (!WristPockets.KeepsCrate(id))
                return true;
            __result = false;
            return false;
        }
    }
}
