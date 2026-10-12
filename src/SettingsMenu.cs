using BoneLib.BoneMenu;
using UnityEngine;

namespace GravityGloves
{
    // The in-game settings pages in BoneMenu, grouped like the settings table in CLAUDE.md. Quick settings comes
    // first, with what most players would change (William's picks), each also on its own page. Then the pages
    // follow a pull from start to finish, then the pockets, the look and the sound. Settings that are rarely changed
    // sit on a page inside their group's page.
    internal static class SettingsMenu
    {
        public static void Create()
        {
            Page root = Page.Root.CreatePage("Gravity Gloves", new Color(1f, 0.55f, 0.15f));

            Page quick = AddGroup(root, "Quick settings", Settings.Gloves, Settings.MaxRange, Settings.MaxMass,
                Settings.WristPockets, Settings.PocketFollows, Settings.PocketMaxSize, Settings.DebugView);
            AddReset(quick);

            AddGroup(root, "Aiming", Settings.MaxRange, Settings.ConeAngle, Settings.ViewAngle, Settings.TargetStickiness,
                Settings.MinMass, Settings.MaxMass, Settings.ItemPriority, Settings.AimFrom, Settings.AimPitch,
                Settings.AimYaw, Settings.BatchAmmo, Settings.BatchRadius);
            AddGroup(root, "Flick", Settings.FlickSpeed, Settings.DirectionTolerance, Settings.Input, Settings.Cooldown);
            AddGroup(root, "Flight", Settings.FlightTime, Settings.ExtraTimePerMetre, Settings.ArcHeight,
                Settings.AvoidObstacles, Settings.Weight, Settings.HandPrediction, Settings.FollowHand);
            AddGroup(root, "Arrival and catch", Settings.HandTracking, Settings.TrackingStart, Settings.TrackingStop,
                Settings.SlowdownStart,
                Settings.SlowdownAmount, Settings.FloatTime, Settings.FloatBrakeTime, Settings.FloatFollow,
                Settings.FloatStopSpeed, Settings.PullAgainAfter, Settings.GrabAssist);

            Page pockets = AddGroup(root, "Wrist pockets", Settings.WristPockets, Settings.PocketKeep,
                Settings.PocketReach, Settings.PocketTakeReach, Settings.PocketSideReach, Settings.PocketMaxSize,
                Settings.PocketPassThrough);
            AddGroup(pockets, "Placement", Settings.PocketFollows, Settings.PocketFacesYou, Settings.PocketAlong,
                Settings.PocketHeight, Settings.PocketSide, Settings.PocketRoll, Settings.PocketTilt);
            AddGroup(pockets, "Look", Settings.RingThickness, Settings.RingOpacity, Settings.RingGlow,
                Settings.RingSize, Settings.PocketOwnColour, Settings.PocketHue, Settings.PocketSaturation,
                Settings.PocketMiniature, Settings.PocketPreview, Settings.PocketEffect);

            Page look = AddGroup(root, "Look", Settings.Glow, Settings.GlowIntensity, Settings.GlowInFlight,
                Settings.Beam, Settings.BeamLength, Settings.LineThickness, Settings.LineOpacity, Settings.LineGlow,
                Settings.LineStiffness, Settings.ColourHue, Settings.ColourSaturation);
            AddGroup(look, "Advanced", Settings.LineGripResponse, Settings.LineRetractTime, Settings.LineFlare,
                Settings.GlowStyle, Settings.Sparks);

            // One page since v0.34 (William): the sound choices and custom sounds pages went, with the sounds fixed to
            // the ones he used (see Sounds).
            AddGroup(root, "Sound and haptics", Settings.Sound, Settings.Volume, Settings.LockHum, Settings.CatchThump,
                Settings.FlightWhoosh, Settings.HapticStrength, Settings.LockVibration, Settings.ReachTick,
                Settings.ReachTickLength, Settings.PocketHum);

            Page general = AddGroup(root, "General", Settings.Gloves, Settings.ScaleWithAvatar, Settings.DebugView);
            AddReset(general);
        }

        private static void AddReset(Page page) =>
            page.CreateFunction("Reset to defaults", Color.red, Settings.ResetAll)
                .SetTooltip("Put every Gravity Gloves setting back to its default.");

        private static Page AddGroup(Page parent, string name, params Setting[] settings)
        {
            Page page = parent.CreatePage(name, Color.white);
            foreach (Setting setting in settings)
                setting.AddTo(page);
            return page;
        }
    }
}
