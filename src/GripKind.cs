using Il2CppSLZ.Marrow;

namespace GravityGloves
{
    // How a grip decides where the hand goes.
    // - A fixed grip, like a gun's handle, always holds the hand the same way.
    // - A handle grip (CylinderGrip), like a sword's, lets the hand slide along it and turn around it.
    // - A surface grip (box, sphere, barrel and the rest) takes the hand wherever it touches, so its default
    //   hold can be anywhere. For the traffic cone's box grip it's inside the cone.
    internal enum GripKind
    {
        Fixed,
        Handle,
        Surface,
    }

    internal static class GripKinds
    {
        // Subclasses first, since they all count as TargetGrips too.
        public static GripKind Of(Grip grip)
        {
            if (grip.TryCast<CylinderGrip>() != null)
                return GripKind.Handle;
            if (grip.TryCast<BoxGrip>() != null || grip.TryCast<SphereGrip>() != null ||
                grip.TryCast<BarrelGrip>() != null)
            {
                return GripKind.Surface;
            }
            return grip.TryCast<TargetGrip>() != null ? GripKind.Fixed : GripKind.Surface;
        }

        // The grip's class, for the log.
        public static string TypeName(Grip grip) => grip.GetIl2CppType().Name;
    }
}
