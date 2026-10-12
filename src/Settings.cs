using MelonLoader;

namespace GravityGloves
{
    internal enum InputButton
    {
        Grip,
        Trigger,
        Either,
    }

    internal enum AimSource
    {
        Arm,
        Hand,
    }

    internal enum BeamMode
    {
        Always,
        Locked,
        Off,
    }

    // Which hands have a glove, or which wrists a pocket.
    internal enum HandChoice
    {
        Both,
        Left,
        Right,
        Off,
    }

    internal static class HandChoices
    {
        public static bool Has(this HandChoice choice, bool right) =>
            choice == HandChoice.Both || choice == (right ? HandChoice.Right : HandChoice.Left);
    }

    // Where a wrist pocket sits: fixed on the hand, or on the forearm like a watch.
    internal enum PocketMount
    {
        Hand,
        Forearm,
    }

    internal enum GlowShader
    {
        Standard,
        Additive,
        Hologram,
    }

    // Every tunable value. Saved in UserData/MelonPreferences.cfg and shown in BoneMenu (see SettingsMenu).
    internal static class Settings
    {
        // Objects closer than this are left for a normal grab.
        public const float MinRange = 0.5f;

        // Targeting
        public static readonly FloatSetting MaxRange = new FloatSetting("MaxRange", "Max range (m)",
            15f, 2f, 30f, 1f, "How far away an object can be and still get selected.");
        public static readonly IntSetting ConeAngle = new IntSetting("ConeAngle", "Cone angle (deg)",
            11, 2, 45, 1, "How far off your aim an object can be and still get selected.");
        public static readonly IntSetting ViewAngle = new IntSetting("ViewAngle", "View angle (deg)",
            140, 60, 360, 10, "Only objects within this angle of where you're looking can be selected, so nothing " +
                              "behind you or out of view gets picked. 140 is a little wider than what a headset " +
                              "shows, 360 is all around you.");
        public static readonly FloatSetting TargetStickiness = new FloatSetting("TargetStickiness", "Target stickiness (s)",
            0.2f, 0f, 1f, 0.05f, "How long the selection stays on an object after you aim away, so it doesn't flicker.");
        public static readonly FloatSetting MinMass = new FloatSetting("MinMass", "Min object mass (kg)",
            0.3f, 0f, 5f, 0.1f, "Lighter things are ignored, like shell casings and debris. " +
                               "Anything you can grab, like ammo, always counts.");
        public static readonly FloatSetting MaxMass = new FloatSetting("MaxMass", "Max object mass (kg)",
            50f, 1f, 100f, 1f, "Heavier objects are ignored. BONELAB's own force pull still works on them.");
        public static readonly IntSetting ItemPriority = new IntSetting("ItemPriority", "Item priority (%)",
            50, 0, 100, 10, "How strongly guns, weapons and ammo win over other objects near your aim, " +
                            "so a gun on a crate gets picked instead of the crate.");
        public static readonly EnumSetting<AimSource> AimFrom = new EnumSetting<AimSource>("AimFrom", "Aim from",
            AimSource.Hand, "Arm aims along the line from your shoulder through your palm. Hand aims where the controller points.");
        public static readonly IntSetting AimPitch = new IntSetting("AimPitch", "Aim pitch (deg)",
            0, -90, 90, 5, "Tilt the aim cone up (positive) or down (negative).");
        public static readonly IntSetting AimYaw = new IntSetting("AimYaw", "Aim yaw (deg)",
            5, -90, 90, 5, "Turn the aim cone outward (positive) or inward (negative).");

        // Glow
        public static readonly BoolSetting Glow = new BoolSetting("Glow", "Glow",
            true, "Make the selected object glow, more strongly when it's locked or flying.");
        public static readonly IntSetting GlowIntensity = new IntSetting("GlowIntensity", "Glow intensity (%)",
            300, 10, 300, 10, "How strongly the selected object glows.");
        public static readonly IntSetting GlowInFlight = new IntSetting("GlowInFlight", "Glow in flight (%)",
            70, 0, 100, 10, "How far through its flight a pulled object keeps glowing. The glow fades out " +
                             "gradually and is gone by this point. 100% fades out just as it reaches your hand, " +
                             "0% stops it at the flick.");

        // Line
        public static readonly EnumSetting<BeamMode> Beam = new EnumSetting<BeamMode>("LineMode", "Line",
            BeamMode.Locked, "When to draw the line from the object toward your hand. Locked shows it while you're " +
                             "holding the button on an object, like Half-Life: Alyx. Always shows it whenever " +
                             "something is selected.");
        public static readonly IntSetting BeamLength = new IntSetting("BeamLength", "Line length (%)",
            65, 10, 100, 5, "How far the line reaches from the object toward your hand, fading out at its end. " +
                            "50% stops around the middle.");
        public static readonly IntSetting LineThickness = new IntSetting("LineThickness", "Line thickness (%)",
            130, 50, 400, 10, "How thick the line is, compared with the original thin line.");
        public static readonly IntSetting LineOpacity = new IntSetting("LineOpacity", "Line opacity (%)",
            300, 20, 300, 10, "How solid the line looks, compared with the original. Above 100% the faint parts " +
                              "get stronger.");
        public static readonly IntSetting LineGlow = new IntSetting("LineGlow", "Line glow (%)",
            190, 0, 300, 10, "Light added along the line, the same kind of glow as on the selected object. 0% leaves " +
                             "only the plain line.");
        public static readonly IntSetting LineStiffness = new IntSetting("LineStiffness", "Line stiffness (%)",
            30, 0, 100, 10, "How long the line stays straight when you point away from the object, like in " +
                            "Half-Life: Alyx. At 100% it only starts to bend once you point 45 degrees away. " +
                            "0% bends it as soon as you point away.");
        public static readonly IntSetting LineGripResponse = new IntSetting("LineGripResponse", "Line grip response (%)",
            100, 0, 100, 5, "How much the line's strength follows how hard you squeeze. At 100% a light squeeze " +
                           "gives a faint line and a full squeeze a strong one. 0% keeps it the same.");
        public static readonly FloatSetting LineRetractTime = new FloatSetting("LineRetractTime", "Line retract time (s)",
            0.12f, 0f, 0.5f, 0.02f, "When you flick, the line shoots back into the object over this time, " +
                                    "with a flash of the flare as it goes in. 0 makes it vanish at once.");
        public static readonly IntSetting LineFlare = new IntSetting("LineFlare", "Line flare (%)",
            40, 0, 200, 10, "The bright spot where the line leaves the object. At 0% the line fades in softly from " +
                           "the object. 100% gives it a solid, rounded start with a glow around it, and 200% makes " +
                           "that glow bigger.");

        // Effects. The glow style and sparks are experiments, off by default.
        public static readonly IntSetting ColourHue = new IntSetting("ColourHue", "Colour hue (deg)",
            30, 0, 350, 10, "The colour of the line, the glow and the sparks. 30 is Alyx orange, 60 yellow, 120 green, " +
                            "180 cyan, 220 blue, 280 purple, 330 pink and 0 red.");
        public static readonly IntSetting ColourSaturation = new IntSetting("ColourSaturation", "Colour saturation (%)",
            100, 0, 100, 10, "How strong the colour is. 0% is white.");
        public static readonly EnumSetting<GlowShader> GlowStyle = new EnumSetting<GlowShader>("GlowStyle", "Glow style",
            GlowShader.Additive, "Experimental. Standard tints the object with a see-through copy. Additive adds light " +
                                 "instead, like a real glow. Hologram borrows the game's hologram shader, the one the " +
                                 "plank gun is drawn with, once something that uses it has been spawned.");
        public static readonly IntSetting Sparks = new IntSetting("Sparks", "Sparks (%)",
            25, 0, 200, 25, "Experimental. Sparks drift along the line toward your hand while you hold the lock. " +
                           "0% turns them off.");

        // Flick
        public static readonly IntSetting FlickSpeed = new IntSetting("FlickAngularSpeed", "Flick speed (deg/s)",
            400, 100, 1000, 25, "How fast you have to flick your wrist up to pull the object.");
        public static readonly IntSetting DirectionTolerance = new IntSetting("DirectionTolerance", "Direction tolerance (deg)",
            60, 10, 90, 5, "How far a flick can twist away from straight up and still count.");
        public static readonly EnumSetting<InputButton> Input = new EnumSetting<InputButton>("InputButton", "Input button",
            InputButton.Grip, "Hold this while pointing at an object to lock onto it. Either means grip or trigger.");
        public static readonly FloatSetting Cooldown = new FloatSetting("Cooldown", "Cooldown (s)",
            0.5f, 0f, 1f, 0.05f, "Wait after a pull before the glove can lock onto something again.");

        // Launch
        public static readonly FloatSetting FlightTime = new FloatSetting("FlightTime", "Flight time (s)",
            0.45f, 0.2f, 1.5f, 0.05f, "Base time for a pulled object to reach your hand. Lower is snappier.");
        public static readonly IntSetting ExtraTimePerMetre = new IntSetting("ExtraTimePerMetre", "Extra time per metre (ms)",
            40, 0, 100, 5, "Added flight time for each metre of distance, so far objects don't arrive too fast.");
        public static readonly FloatSetting ArcHeight = new FloatSetting("ArcHeight", "Arc height (x)",
            1.1f, 0f, 3f, 0.1f, "1 is a natural throw. 0 flies straight at your hand, higher values lob it.");
        public static readonly BoolSetting AvoidObstacles = new BoolSetting("AvoidObstacles", "Avoid obstacles",
            true, "Check the arc for walls, door frames and ceilings when you flick, and fly lower or higher if it " +
                  "would hit one.");
        public static readonly IntSetting Weight = new IntSetting("Weight", "Weight (%)",
            70, 0, 200, 10, "How much heavier objects fly slower and lower, and land harder in your hand. Things up " +
                             "to 3 kg, like guns, aren't affected. At 100% something of 30 kg takes 1.6 times as " +
                             "long and arcs about half as high. 0% makes everything fly the same.");
        public static readonly IntSetting HandPrediction = new IntSetting("HandPrediction", "Hand prediction (%)",
            0, 0, 100, 10, "When you're moving, aim where your hand will be when the object arrives. Only with " +
                             "hand tracking and slowdown both off: otherwise the object moves with you anyway.");

        // Arrival
        public static readonly IntSetting HandTracking = new IntSetting("HandTracking", "Hand tracking (%)",
            100, 0, 100, 10, "How much the object steers toward your hand if you move it during the flight. " +
                             "It ignores quick movements at first and gets more accurate as it gets closer.");
        public static readonly IntSetting TrackingStart = new IntSetting("TrackingStart", "Tracking starts at (%)",
            30, 0, 90, 10, "How far through its flight the object starts steering toward your hand. Before that it " +
                           "keeps to its arc, so the swing of the flick itself isn't followed. From there the " +
                           "steering eases in to full strength at your hand, so it homes in at the end, like " +
                           "Half-Life: Alyx.");
        public static readonly FloatSetting TrackingStop = new FloatSetting("TrackingStop", "Tracking stops at (m)",
            0f, 0f, 1f, 0.05f, "Stop steering toward your hand once the object is this close, " +
                                 "so you can still move your hand out of the way.");
        public static readonly IntSetting SlowdownStart = new IntSetting("SlowdownStart", "Slowdown starts at (%)",
            70, 0, 95, 5, "How far through its flight the object starts slowing down. 50% means halfway.");
        public static readonly IntSetting SlowdownAmount = new IntSetting("SlowdownAmount", "Slowdown amount (%)",
            30, 0, 90, 10, "How much slower the object is when it reaches your hand. 0% turns the slowdown off.");
        public static readonly FloatSetting FloatTime = new FloatSetting("FloatTime", "Float time (s)",
            0.05f, 0f, 2f, 0.05f, "As in Half-Life: Alyx, a pulled object slows to a stop as it reaches your hand and " +
                                 "floats there weightless for this long before it falls, so it's easy to catch. " +
                                 "Objects from far away come in too fast to stop and carry on past. 0 turns it off.");
        public static readonly FloatSetting FloatBrakeTime = new FloatSetting("FloatBrakeTime", "Float brake time (s)",
            0.5f, 0.05f, 0.5f, 0.05f, "How long a pulled object takes to slow to a stop at your hand before it " +
                                      "floats. Longer is softer, and it starts slowing further out.");
        public static readonly IntSetting FloatFollow = new IntSetting("FloatFollow", "Float follow (%)",
            50, 0, 100, 10, "How much a floating object follows your hand when you move it, a little behind. 0% " +
                            "leaves it where it stopped, 100% keeps it in front of your hand. Hand tracking only " +
                            "steers the flight before that.");
        public static readonly FloatSetting FloatStopSpeed = new FloatSetting("FloatStopSpeed", "Float stops up to (m/s)",
            4f, 0.5f, 20f, 0.5f, "The fastest an object can arrive and still stop to float at your hand. Faster ones, " +
                                 "usually pulled from further away, only slow down and carry on past. With the " +
                                 "default flight time, an object pulled from 2 m away arrives at about 4 m/s.");

        // Catch
        public static readonly FloatSetting PullAgainAfter = new FloatSetting("PullAgainAfter", "Pull again after (s)",
            0.25f, 0f, 2f, 0.05f, "How long after a pulled object reaches your hand (after its float, if it floats) " +
                                   "before you can lock onto it and pull it again, if you didn't catch it.");
        public static readonly FloatSetting GrabAssist = new FloatSetting("GrabAssist", "Grab assist (m)",
            0.4f, 0f, 1f, 0.05f, "Start a grab while a pulled object is this close to your hand and you'll catch it. " +
                               "0 turns it off.");

        // Ammo
        public static readonly BoolSetting BatchAmmo = new BoolSetting("BatchAmmo", "Batch ammo",
            true, "Ammo pickups lying close together are selected and pulled together, and picking up one " +
                  "collects them all.");
        public static readonly FloatSetting BatchRadius = new FloatSetting("BatchRadius", "Batch radius (m)",
            0.5f, 0.1f, 2f, 0.1f, "How close other ammo pickups have to be to the selected one to join its batch.");

        // Rotation. Anything with a grip is turned during the flight to arrive the way you'd hold it.
        public static readonly IntSetting FollowHand = new IntSetting("FollowHand", "Follow hand rotation (%)",
            20, 0, 100, 10, "Objects that can't be turned to fit your hand, like ones without a grip, turn as you " +
                           "turn your hand during the flight. 100% copies your hand's rotation exactly.");

        // Sound and haptics
        public static readonly BoolSetting Sound = new BoolSetting("Sound", "Sound",
            true, "Play the glove's sounds.");
        public static readonly IntSetting Volume = new IntSetting("SoundVolume", "Volume (%)",
            60, 0, 200, 10, "Volume of all the glove's sounds.");
        public static readonly IntSetting LockHum = new IntSetting("LockHum", "Lock hum (%)",
            10, 0, 200, 10, "A hum while you hold the lock on an object, getting louder as you squeeze. " +
                             "0% turns it off.");
        public static readonly IntSetting LockVibration = new IntSetting("LockVibration", "Lock vibration (%)",
            10, 0, 200, 10, "A light vibration while you hold the lock on an object, getting stronger as you " +
                             "squeeze. 0% turns it off.");
        public static readonly IntSetting CatchThump = new IntSetting("CatchThump", "Catch thump (%)",
            40, 0, 200, 10, "A thump and a vibration when a pulled object lands in your hand. Heavy objects land " +
                             "harder (see Weight on the Flight page). 0% turns it off.");
        public static readonly IntSetting FlightWhoosh = new IntSetting("FlightWhoosh", "Flight whoosh (%)",
            40, 0, 200, 10, "A whoosh that comes from the flying object, so you hear it coming toward you. " +
                             "0% turns it off.");
        public static readonly IntSetting HapticStrength = new IntSetting("HapticStrength", "Haptic strength (%)",
            90, 0, 100, 10, "Strength of all the glove's controller vibrations.");
        public static readonly IntSetting ReachTick = new IntSetting("ReachTick", "In reach tick (%)",
            200, 0, 400, 10, "A tap when an object you pulled comes close enough to grab with your hand, like in " +
                             "Half-Life: Alyx. 0% turns it off.");
        public static readonly IntSetting ReachTickLength = new IntSetting("ReachTickLength", "In reach tick length (ms)",
            150, 10, 300, 10, "How long the in reach tick lasts.");
        public static readonly IntSetting PocketHum = new IntSetting("PocketHum", "Pocket hum (%)",
            100, 0, 200, 10, "A slight vibration while something you hold is at a wrist pocket, ready to go in, or " +
                             "while you reach into a full one. 0% turns it off.");
        // Wrist pockets (WristPockets)
        public static readonly EnumSetting<HandChoice> WristPockets = new EnumSetting<HandChoice>("PocketWrists",
            "Wrist pockets", HandChoice.Both, "Which wrists have a pocket: both, only the left or right, or none. Turning one off drops " +
                  "what's in it beside it. " +
                  "As in Half-Life: Alyx, each wrist has a pocket for one object. Let go of an object at the pocket " +
                  "on your other wrist to put it away, and a see-through copy shows there in miniature. Reach for a " +
                  "full pocket with an open hand and the copy moves into your hand. Grab to take the object out. " +
                  "Bring another object to a full pocket to swap: the one inside moves out beside the pocket and " +
                  "drops when you let go of the new one.");
        public static readonly EnumSetting<PocketMount> PocketFollows = new EnumSetting<PocketMount>("PocketFollows",
            "Pocket follows", PocketMount.Forearm, "Hand keeps the pocket in one place on your hand, so it moves when " +
                                                "you bend or turn your wrist. Forearm keeps it on your forearm like a " +
                                                "watch, so moving your hand doesn't move it.");
        public static readonly BoolSetting PocketKeep = new BoolSetting("PocketKeep", "Keep between levels",
            true, "What's in your wrist pockets comes along to the next level: the object itself, with its magazine " +
                  "and ammo, whether you found it in the level or spawned it. If the game removes it anyway, " +
                  "something spawned from a crate is spawned again.");
        public static readonly BoolSetting PocketFacesYou = new BoolSetting("PocketFacesYou", "Pocket faces you",
            true, "Like in Half-Life: Alyx, the pocket turns to face your eyes, and toward your hand or the object " +
                  "when you reach in or bring something to it. It's drawn over your arm, so it never disappears " +
                  "into it. Off keeps it flat on your wrist, turned by the rotation and tilt settings.");
        public static readonly BoolSetting PocketPassThrough = new BoolSetting("PocketPassThrough", "Pass through your body",
            true, "Something you take out of a pocket that comes out inside your arm or body passes through that part " +
                  "of you, your hands included, instead of being shoved by it, until it's out of it. Then it bumps " +
                  "into you as usual.");
        public static readonly IntSetting PocketEffect = new IntSetting("PocketEffect", "Pull-in effect (%)",
            100, 0, 200, 25, "Sparks drawn into a pocket while something is at it, and a burst as it goes in. 0% " +
                             "turns them off.");
        public static readonly FloatSetting PocketReach = new FloatSetting("PocketReach", "Put-in reach (m)",
            0.09f, 0.03f, 0.3f, 0.01f, "How close an object you're holding has to be to a pocket to go in, straight " +
                                       "out from its face. From the side it's less (see Side reach).");
        public static readonly FloatSetting PocketTakeReach = new FloatSetting("PocketTakeReach", "Take-out reach (m)",
            0.17f, 0.03f, 0.3f, 0.01f, "How close your open hand has to be to a full pocket to take what's in it, " +
                                       "straight out from its face. From the side it's less (see Side reach).");
        public static readonly IntSetting PocketSideReach = new IntSetting("PocketSideReach", "Side reach (%)",
            30, 10, 100, 10, "How much of the reach is left coming at a pocket from the side, so it reaches out " +
                             "like a cone from the wrist instead of a ball. Less makes the pocket easier to miss when " +
                             "you reach for something else on your arm, like the body log. 100% is the same all round.");
        public static readonly IntSetting PocketMaxSize = new IntSetting("PocketMaxSize", "Pocket max size (cm)",
            60, 10, 300, 10, "The biggest object a pocket takes, by its longest side, apart from weapons: guns and " +
                              "melee weapons always fit, whatever their size.");
        public static readonly IntSetting PocketAlong = new IntSetting("PocketAlong", "Pocket position (cm)",
            8, -20, 30, 1, "How far up your forearm the pocket sits, from the wrist toward the elbow. Negative " +
                           "moves it onto the back of your hand.");
        public static readonly IntSetting PocketHeight = new IntSetting("PocketHeight", "Pocket height (cm)",
            4, -10, 15, 1, "How far out from the middle of your wrist the pocket sits, on the back of the wrist. " +
                           "Negative moves it to the palm side.");
        public static readonly IntSetting PocketSide = new IntSetting("PocketSide", "Pocket sideways (cm)",
            1, -15, 15, 1, "Moves the pocket sideways across your wrist. Positive is toward your thumb.");
        public static readonly IntSetting PocketRoll = new IntSetting("PocketRoll", "Pocket rotation (deg)",
            55, -180, 180, 5, "Turns the pocket around your wrist, or your hand, keeping it facing out. Positive turns " +
                             "it toward your thumb.");
        public static readonly IntSetting PocketTilt = new IntSetting("PocketTilt", "Pocket tilt (deg)",
            0, -90, 90, 5, "Tilts the pocket's face toward your fingers (positive) or your elbow, without moving it.");
        public static readonly IntSetting PocketMiniature = new IntSetting("PocketMiniature", "Miniature size (cm)",
            6, 2, 15, 1, "How big the copy of a stored object is in its pocket, by its longest side.");
        public static readonly IntSetting PocketPreview = new IntSetting("PocketPreview", "See-through strength (%)",
            120, 10, 200, 10, "How strongly the see-through copy of a stored object shows: in its pocket, in your " +
                              "hand when you reach for it, and beside the pocket when you're about to swap it.");

        // Wrist pockets / Look: the ring's look, like the line's settings
        public static readonly IntSetting RingThickness = new IntSetting("RingThickness", "Ring thickness (%)",
            50, 25, 400, 25, "How thick the pocket's ring is, its bright line and the glow around it.");
        public static readonly IntSetting RingOpacity = new IntSetting("RingOpacity", "Ring opacity (%)",
            180, 0, 200, 10, "How strongly the ring's bright line shows. 0% leaves only its glow.");
        public static readonly IntSetting RingGlow = new IntSetting("RingGlow", "Ring glow (%)",
            100, 0, 300, 25, "The soft glow around the ring. Above 100% it also spreads wider. 0% turns it off.");
        public static readonly IntSetting RingSize = new IntSetting("RingSize", "Ring size (%)",
            100, 50, 200, 10, "How big the ring is. It still grows when something can go in. Only the look: how close " +
                              "things have to be is the reach.");
        public static readonly BoolSetting PocketOwnColour = new BoolSetting("PocketOwnColour", "Own colour",
            false, "Give the pockets their own colour below: the ring, the see-through copy and the sparks. Off, they " +
                   "use the glove's colour from the Look page.");
        public static readonly IntSetting PocketHue = new IntSetting("PocketHue", "Pocket hue (deg)",
            170, 0, 350, 10, "The pockets' colour, with Own colour on. 30 is Alyx orange, 60 yellow, 120 green, 180 cyan, " +
                            "220 blue, 280 purple, 330 pink and 0 red.");
        public static readonly IntSetting PocketSaturation = new IntSetting("PocketSaturation", "Pocket saturation (%)",
            100, 0, 100, 10, "How strong the pockets' colour is, with Own colour on. 0% is white.");

        // General
        public static readonly EnumSetting<HandChoice> Gloves = new EnumSetting<HandChoice>("Gloves", "Gloves",
            HandChoice.Both, "Which hands have a gravity glove: both, only the left or right, or none.");
        public static readonly BoolSetting ScaleWithAvatar = new BoolSetting("ScaleWithAvatar", "Scale with avatar",
            false, "Scale the distances (max range, grab assist reach and where tracking stops) with " +
                   "your avatar's height, so they feel the same on small and big avatars.");
        public static readonly BoolSetting DebugView = new BoolSetting("DebugView", "Debug view",
            false, "Show the aim cone and range, and the planned path of pulled objects.");

        private static readonly Setting[] All =
        {
            MaxRange, ConeAngle, ViewAngle, TargetStickiness, MinMass, MaxMass, ItemPriority, AimFrom, AimPitch, AimYaw,
            Glow, GlowIntensity, GlowInFlight,
            Beam, BeamLength, LineThickness, LineOpacity, LineGlow, LineStiffness, LineGripResponse, LineRetractTime, LineFlare,
            ColourHue, ColourSaturation, GlowStyle, Sparks,
            FlickSpeed, DirectionTolerance, Input, Cooldown,
            FlightTime, ExtraTimePerMetre, ArcHeight, AvoidObstacles, Weight, HandPrediction,
            HandTracking, TrackingStart, TrackingStop, SlowdownStart, SlowdownAmount, FloatTime, FloatBrakeTime, FloatFollow, FloatStopSpeed,
            PullAgainAfter, GrabAssist,
            BatchAmmo, BatchRadius,
            FollowHand,
            Sound, Volume, LockHum, LockVibration, CatchThump, FlightWhoosh, HapticStrength, ReachTick, ReachTickLength,
            PocketHum,
            WristPockets, PocketKeep, PocketPassThrough, PocketFacesYou, PocketEffect, PocketFollows, PocketReach, PocketTakeReach, PocketSideReach, PocketMaxSize, PocketAlong, PocketHeight, PocketSide,
            PocketRoll, PocketTilt, PocketMiniature, PocketPreview,
            RingThickness, RingOpacity, RingGlow, RingSize, PocketOwnColour, PocketHue, PocketSaturation,
            Gloves, ScaleWithAvatar, DebugView,
        };

        private static MelonPreferences_Category _category;

        public static void Init()
        {
            _category = MelonPreferences.CreateCategory("GravityGloves", "Gravity Gloves");
            foreach (Setting setting in All)
                setting.CreateEntry(_category);
        }

        public static void Save() => _category.SaveToFile(false);

        public static void ResetAll()
        {
            foreach (Setting setting in All)
                setting.Reset();
            Save();
        }
    }
}
