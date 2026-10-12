using HarmonyLib;
using Il2CppSLZ.Marrow;

namespace GravityGloves
{
    // BONELAB's built-in force pull also reacts to the grip on far objects. While the glove is using a hand,
    // the built-in pull is skipped for that hand so the two don't fight over the same object. It still works
    // as normal whenever the glove has nothing selected, for example on objects that are too heavy.
    [HarmonyPatch(typeof(ForcePullGrip), nameof(ForcePullGrip.OnFarHandHoverUpdate))]
    internal static class ForcePullHoverPatch
    {
        private static bool Prefix(Hand hand) => !Mod.BlocksForcePull(hand);
    }

    [HarmonyPatch(typeof(ForcePullGrip), nameof(ForcePullGrip.Pull))]
    internal static class ForcePullStartPatch
    {
        private static bool Prefix(Hand hand) => !Mod.BlocksForcePull(hand);
    }
}
