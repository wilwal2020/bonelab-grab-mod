# Bonelab Gravity Gloves mod

Goal: a Half-Life: Alyx-style gravity glove "flick" grab for BONELAB. Point at an object, flick your wrist back, and it arcs into your hand.

## Stack
- BONELAB (Unity, IL2CPP), PC VR only (Quest only supports content mods, not code mods)
- MelonLoader + BoneLib
- C#, with Harmony patches where needed
- In-game settings page in BoneMenu (BoneLib), with values saved through MelonPreferences
- Game assembly references usually come from `BONELAB/MelonLoader/Il2CppAssemblies`, which MelonLoader generates on first launch. Check the actual game, MelonLoader and BoneLib versions before writing code, since their APIs have changed between patches.

## Mechanic
1. **Selection**: cast a cone from the palm and pick the best rigidbody within range and mass limits. Angles are measured to each collider's point nearest the aim line, not to the centre of mass, so pointing at either end of a long object (bat, broom) counts. A fifth of the angle to the object's centre is added, so between neighbours the one aimed at more centrally wins. The selection is slightly sticky so it doesn't flicker: it only switches once the current object hasn't been the best pick for the stickiness time. The cone aims along the arm (shoulder through palm) or along the controller ("Aim from"), and can be tilted with aim pitch and yaw.
   - **View angle** (v0.26, William, 140° by default): only objects whose aimed-at point is within half the view angle of where the head faces can be selected, so nothing behind the player or out of view gets picked. 360 turns it off. A locked object stays locked when the player looks away.
   - **Min mass:** only applies to things without a grip. That keeps out shell casings and debris, while ammo and other grabbable items always count.
   - **Item priority:** weapons, magazines and ammo get a scoring bonus of the item priority share of the cone angle, so a gun on a crate beats the crate. Other grabbables get a quarter of that. A more important kind of object (item, then grabbable, then anything else) also takes over the selection at once, ignoring stickiness.
   - **Ammo batches:** selecting an ammo pickup (`AmmoPickup`) also selects other ammo pickups within the batch radius, up to 8 in total (`src/AmmoBatch.cs`). They glow together and are pulled together, with arrival points spread around the palm and arrivals staggered. As soon as one is collected or grabbed, the rest are collected with `AmmoPickup.ForcePickup()`. Magazines aren't batched, since a hand can only hold one.
   - **Highlight, as in Alyx:** pointing makes the selected object glow, using enlarged see-through copies of its visible meshes (`src/ObjectGlow.cs`). Grabbing (locking) makes a line grow out of the object toward the hand, stopping around the middle. William was explicit that the line goes from the object toward the hand, stops partway, and only appears once you grab.
   - **Line glow** (v0.27, William: the line looked dim next to the glowing object): two more lines are drawn over it with the additive object-glow material (`GlowMaterials.CreateAdditive`, `Hidden/Internal-Colored`): a wider soft one and a narrow bright one, so the light falls off toward the edges without a texture. Their strength is "Line glow" (100%), and follows the line's visibility, flash and squeeze.
   - **Line stiffness** (v0.27, 50%): the line heads out of the hand straight at the object until the hand points more than stiffness × 45° away, and only bends by however much further it points, as in Half-Life: Alyx. 0% bends it at once, as before.
   - **The line reacts:** its strength follows how hard the input is squeezed, from just holding the lock (release threshold 0.35) to a full squeeze, scaled by "line grip response". It's applied after opacity is capped at 1, so it still shows at high opacity. On the flick the line shoots back into the flying object over "line retract time", and the flare pops as it goes in.
   - **The line's start ("line flare"):** the line points almost straight at the player, so a solid round start reads as a bright blob on the object, even with no flare sprite. William saw it as "the flare" and found the setting did nothing, because it only drove the sprite. Now at 0% the line fades in from 20% strength over its first 15% and has no rounded ends (a 3-key alpha gradient). From 50% it has rounded ends again, from 100% the start is solid, and the sprite grows with the setting.
   - **Glow in flight:** a pulled object's glow fades out over its flight (smoothstep on flight progress) and is gone by the "glow in flight" share. A guided flight released near the hand counts as arrived.
   - **Skinned meshes** (common in modded weapons) can't be inflated by scaling the copy, because their bones place the vertices. Each bone gets an empty child scaled up around the mesh's centre, and the copy is skinned to those.
   - **Effect shaders:** if an object has nothing solid to glow, its see-through or hidden-shader meshes glow instead. The plank gun (`BoardGun`) is drawn entirely with `SLZ/Hologram Depth fade`.
2. **Flick**: the input is held (grip only by default, as in Alyx) and the wrist flicks the fingers up. This is measured from the controller's rotation speed, not from the hand moving backward. William tested the backward pull and it felt wrong. The rotation has to be around the axis that lifts the fingers, relative to the direction from the palm to the target, faster than the threshold and within the direction tolerance. When the target is more than 35° below the hand, a flick in either direction counts. From 60° to 80° below, the axis blends over to the controller's own side-to-side axis, since pointing almost straight down the world axis says nothing about how the wrist is turned.
3. **Launch**: a ballistic arc. Solve `v = (target - start - 0.5 * g * t^2) / t`, where `t` is the flight time plus extra time per metre, and `target` is the predicted hand position. With hand tracking or slowdown on, the object is moved along that arc step by step instead of left to physics.
   - **Guided steps** (v0.26.2): each step's velocity is the path's own step plus a pull back onto the path, capped at 4 m/s. It used to be the whole gap over one step, so a blocked object was shoved into what blocked it at up to 45 m/s until it was 0.5 m off its path, which can tunnel it through thin floors. In the v0.26.1 test the DevManipulator, blocked 20 m out after a 22.7 m pull, was 4 km below the level a minute and a half later.
   - **Continuous collision:** a flying object's bodies use `ContinuousSpeculative` (only those that were `Discrete`) from the launch until it starts braking for the float or the flight ends (`Target.SetFastCollision`).
   - **Range check:** an object whose centre is more than 1.5 × the max range away isn't pulled, and the lock is dropped with a log line. The lock check goes by the main body, while the flight goes by the centre of all bodies: the fallen DevManipulator still counted as in range, and was pulled from 4099 m at 793 m/s, a 164 s flight.
4. **Arrival and catch**: William asked for the arrival to be softened, which is what these options are for.
   - **Hand tracking**: the end of the arc drifts toward a smoothed hand position. The smoothing is heavy early, to ignore quick hand movements, and accurate near the end. Steering stops inside "tracking stops at" so the player can still move out of the way.
     - **Moving with the player** (v0.33): a guided flight is moved in the player's frame. Its arc, and where it ends, move with the player's own movement (the origin of the tracking space, `OpenControllerRig.vrRoot`, which stick locomotion moves), in full and at once, and its velocity has the player's velocity added (`Flights.PlayerShift`, `GloveHand.PlayerVelocity`). Hand tracking, its smoothing and "tracking starts at" only follow the hand's movement relative to the player. Guided flights don't use hand prediction any more, since they follow the real movement.
       - William: flicking something close by while moving sideways or backwards, "the tracking would stop quite quickly and not go to my hand". In the v0.32 test, pulls where the hand moved 1–2.5 m during the flight floated 50–99 cm short and fell. The smoothing lagged a steadily moving hand by up to 40 cm, and the float point followed only half of it (Float follow).
       - The log's arrival lines add how far the player moved ("the player N cm").
     - **Tracking starts at** (v0.27, 40%): the path follows none of the hand's movement until that share of the flight, then eases in (smoothstep) to the full hand tracking share at the end, so it homes in late, like Alyx's objects "magnetizing" to the hand. Until v0.27 it eased in from the flick, and William, at 100% tracking and 0% prediction, saw it start tracking "right after I flicked it": it followed the swing of the flick itself.
   - **Slowdown**: from a chosen point in the flight, the object moves along the rest of its arc more slowly.
   - **Float** (v0.20.0, `Flights.StepFloat`): William described Alyx's arrival as the object slowing down near the hand and floating weightless for a moment before it falls. It shows mostly on close pulls, since far ones come in fast enough to carry through. The slowdown didn't give that.
     - **Braking:** in the second half of the flight, once the object is close enough to its stop to brake within the "float brake time" (speed × time / 2), it brakes toward the stop without gravity, losing any sideways drift. The stop is the end of its path, with hand tracking applied. Objects that aren't fitted to the hand stop where their near side reaches it.
     - **Speed limit:** braking is capped at the stop speed divided by the brake time, and starts at most the stop speed × brake time / 2 out (0.4 m by default). So arrivals up to "float stops up to" stop fully. Faster ones only slow down, go past, and leave the float once they're out of that zone (at least 0.25 m).
     - **Floating:** it then floats for the float time while its leftover motion dies down, and anything not turned to the hand stops spinning. Gravity fades back in over 0.2 s, and the whoosh stops.
     - **Braking ends on time** (v0.26.2): braking evenly to the point takes twice the distance over the speed, and braking ends by 1.2 × that + 0.05 s (at most 1 s), even if the point moved on with the hand. In the v0.26.1 test, braking toward a point that kept following the hand lasted up to the 1 s limit, weightless all along. Floats that fell spent from under 0.2 s to over 1 s in the air past their planned arrival (a gacha capsule 1.04 s, the spawn gun 0.67 s), mostly 0.2–0.4 s. William: "some objects seemed to hover for longer than other objects, even at similar pull distances".
     - The float takes over from the guided path where it starts braking. Something slow near its stop that has stopped getting closer, like after bumping the hand, floats where it is. Something moving away from its stop at speed keeps flying (v0.22). In v0.21 those floated at once and "went past" at 1–1.7 m/s.
     - **Follow** (v0.22, "Float follow", 50%): the stop moves by that share of how far the hand's arrival point has moved since the float started, smoothed over 0.1 s. Since v0.33 that's the hand's movement relative to the player: the stop moves with the player in full, and a float that ends keeps the player's velocity as momentum (`FloatCarry`).
       - Braking and floating work relative to the moving stop. The stop's velocity is added to the object, and taken off again the next step (`FloatCarry`), so its moves are copied at once rather than caught up through the damping.
       - In v0.21 the stop stayed put. William's hand kept rising 40–70 cm after flicks at things by his feet, so even at 100% hand tracking objects stopped 17–37 cm short. He asked for it to "still follow the hand a little".
     - **Settling** (v0.23): an object that came in slow enough to stop (up to 1.25 × the float stop speed) is pulled back to the float point if it slips past, critically damped at 10 per second and capped at the hardest braking. It only leaves the float beyond twice the zone.
       - In v0.22, 65 of 137 floats "went past" at nearly full speed and left the float. The rising hand dragged the float point into objects coming down at it from their arc. William's brake time of 0.5 s also made the braking weak (8 m/s²).
       - Objects that came in faster still only get slowed and carry on past.
     - **Log:** one line per float, for example "X came in at 3.1 m/s, stopped 4 cm from the hand and floated 0.50 s before it fell (the hand moved N cm since the flick, hand tracking P%)". Other endings are "went past … at N m/s", "left the float after …" and "was caught …". The arrival line is then only logged for flights that didn't float.
   - **In reach tick** (v0.27, William, from Alyx): a haptic tap once per flight when the object comes within the hand's normal grab range: the hand's own hover is on one of its grips, or its nearest part is inside the game's hover sphere (`Hand.HoverSphereRadius` around a point 0.8 × that out along the palm's forward, from `Hand.UpdateHovering`, which also adds the controller's velocity × 0.015 s; the log gives the sphere's size once). Until v0.34 it came within grab assist's reach (William's is 40 cm), or 25 cm without it, and William wanted it only once the object is really close. Since v0.34 it's the game's tap amplitude (0.25) for "In reach tick length" (80 ms), scaled by "In reach tick" (up to 400%) and Haptic strength. Until then it was 0.1 for 30 ms, and William found it too weak and short. His Haptic strength is 10%, which scales every vibration of the mod to a tenth.
   - **Pull again after** (v0.34, 0.25 s): an object that wasn't caught can be selected and pulled again that long after its flight let go of it: the end of its path, or of its float (`Flights.CanPull`, `Flight.LetGoAt`). Until then it couldn't be selected while its flight was kept around for grab assist, which is 0.75 s past its planned arrival or its float (`GracePeriod`), and William asked to choose the wait. Longer settings are kept after the flight ends (`Flights.Resting`).
   - **Catching**: the player catches the object themselves by default. Grab assist catches the object when a grab starts within its reach. Auto-grab (with its catch radius) was removed in v0.25: William didn't need it.
     - **Confirmed at once** (v0.26.1): once grab assist has moved the object into place, it hovers the grip and confirms the grab in the same physics step, whatever the grip, as it always did for fixed grips (guns). Every grip is scored from the hand's root, as the game's own hover does (see Which way round), and a grip that isn't fixed whose hold is still more than 8 cm from the hand is told to hold where the hand is (`SettleHold`).
       - **Why:** until v0.26.0, grips that aren't fixed hovered for 3 physics steps first. The player is holding grip, so the game always closed the hand by itself during those steps ("after 1 of 3 hover steps"). Grab assist then ended the hover and unlocked it while the game's grab, which takes a few steps, was still finishing.
       - In the v0.26.0 test every such grab left the hand holding the object without being on any of its grips (`AttachedReceiver` null) for the whole hold. Crates, the milk crate's handle, swords, a morningstar, a hatchet and a spiked club were all refused by the pockets as "held by nothing" seconds later. Pocket take-outs of a crate and a sledgehammer ended 36–73 cm and 140–169° from the hold they went in with.
       - William: the hand went inside the crate's handle, or was stretched and not touching the crate, and those objects wouldn't go in a pocket. Before that: "a weird location to grab, that wouldn't be available when grabbing normally", the same problem as the cone and the milk crate.
       - The first v0.26.0 build instead handed the grab to the game's own hover (unlocking it for up to 150 ms). The game closed the hand by itself 22 ms later every time, and those grabs broke in the same way. Every grab grab assist confirmed itself, all guns, was whole ("held by Eder22_gripPoint" when pocketed).
       - If the game still closes the hand before the confirm, it's left alone to finish, with no hover end and no unlock. Every grab is then watched: a hand holding the object but on no grip of it after 1 s logs "held in a broken state".
       - A catch where it touches stops 1 cm in front of the palm, along the palm's normal (away from the back of the hand), instead of at the palm point inside the hand.
       - Pocket take-outs go the same way, with no move time and the hover kept locked, so the hand can't take the player's own arm there.
       - **Holding where the hand is** (v0.26.2): a grip's own hold is only kept if it's within 8 cm of the hand and turned less than 30° from it. Otherwise, and always for a pocket take-out, the target is set to where the hand is.
         - The grab's joint takes its rotation from the grip's `GetNeutralTargetInWorld` (see Which way round). For box and barrel grips that's this target, so they're held where the hand is: in the v0.26.2 test a plank's box grip came out of the pocket 0–1 cm and 0–1° from how it went in, though its own hold was 25 cm and 120° away. Fixed grips set their own target when they're scored. For a cylinder grip it's the grip's own frame, flipped or not, so this target only sets where the hand sits on the handle. Sphere grips have their own.
         - v0.26.2 to v0.28 also turned on the grip's `ignoreGripTargetOnAttach` around the confirm. The game never reads that field (checked in all its assemblies), so it was removed in v0.28.1.
       - **Which way round** (v0.28.1): a handle (`CylinderGrip`) can be held either way round along its axis (`HandToGripState.isZFlipped`). `CylinderGrip.ValidateGripScore(hand, handTransform)` decides it: flipped (unless the grip's `ignoreFlipOnZ`) if the grip target's primary axis (`targetTransform.rotation * GetPrimaryAxis()`) points against the same axis in the hand's handle (`handTransform` composed with `HandPoseAnimator.GetHandleInHandNeutral` for the grip's hand pose). It writes nothing else.
         - The confirm (`OnGrabConfirm` → `Snatch` → `CoSnatch`, at once when instant) turns the hand to `GetNeutralTargetInWorld` composed with `GetHandInHandle` while it makes the hand's joint (`PrepareJoint`), then turns it back. For a cylinder grip that's the grip's `targetTransform`, turned 180° about its secondary axis if `isZFlipped` (`GetPrimaryAxisInversedRotation`). So the joint holds the way round decided at the confirm, and setting `isZFlipped` afterwards doesn't turn the object.
         - The game's own hover (`Hand.UpdateHovering`) gives the hand's root (`SimpleTransform` of `hand.transform`, turned back by `SkeletonHand._offsetTransform`). From v0.26 to v0.28 grab assist gave grips that aren't fixed the hand's handle (`Hand.GetHandleInWorld()`), so the handle's turn in the hand counted twice and the test came out either way. Since v0.28.1 it gives every grip the hand's root, without the skeleton offset, since the joint is made from the physics hand. Only cylinder grips read the pose given: box, barrel, sphere and generic grips ignore it.
         - In the v0.26.1 test a dagger came out of the pocket 19–26 cm and 126–180° from how it went in, and the cone 80–101 cm and 147–178° (William: the knife came out in a reverse grip). In the v0.28 test 11 of 17 handles taken out were grabbed the other way round from how they went in, and all 11 came out reversed (96–179°: daggers, a wrench, a bat, a sledgehammer, the milk crate). William: "a knife or a bar will get grabbed in a reverse grip".
   - **Feedback:** since v0.34 the sounds are fixed to the ones William used (he asked for one sound page with only the sounds in use), and there's no choosing or custom sound folder any more (`src/Sounds.cs`).
     - **His recordings** (`src/GloveRecordings.cs`): the lock, the hum while locked, the release (a lock let go without a flick: the player let go, or the object got out of reach) and the flick (three, a random one each time, never the same twice in a row). Until v0.34 they were his custom sounds in `UserData/GravityGloves/Sounds` (`when grabbing.wav`, `when holding.wav`, `when letting go.wav` and the three flick files); they're now in `src/Recordings` as `glove-*.wav`, mixed down to mono, and built into the DLL. Where he got them isn't recorded: check before publishing the mod with them.
       - **Played as before:** each is levelled to its moment's built-in loudness (lock 0.27, hum 0.28, release 0.27 like the lock, flick 0.3), measured in 50 ms windows: one-shots by their loudest window, as `SoundSynth` does, and the hum by its median window, since it plays as long as it's held. The samples are scaled only as far as their peak allows (0.95), and any gain still missing plays as volume instead, up to 8x. The lock plays at 40%, his volume for it. The hum starts at a random point in its 13 s file on each lock (`LoopSound.Recording`), since its start is 16–21 dB below its peak.
       - **Reading:** `src/WavFile.cs` reads WAV itself. Unity's decoder can't be used, because BONELAB's build stripped the `DownloadHandlerAudioClip` constructor that `UnityWebRequestMultimedia.GetAudioClip` calls ("Method not found", v0.19.1 test).
       - **History:** custom sounds came in v0.19 (folders per moment, a volume per file, "Even out levels", "Loops start anywhere"). Before v0.19.3 loops were levelled by their loudest moment, and his hum was inaudible at his 10% lock hum.
     - **Built-in** (`src/SoundSynth.cs`, no Unity in it): the select click (RankerClick, Kenney's CC0 `click-ui` from William's image ranker, Pick of the Roll, as a 16-bit mono WAV in `src/Recordings`), the catch thump (Thump) and the whoosh of a flying object (Hum, a one-second loop). Until v0.34 there were 8 select, 11 lock, 6 hum, 12 flick, 6 catch and 6 whoosh sounds to choose from on a "Sound choices" page.
       - The line preview still has every old sound in its JavaScript copy (`synth.js` in it). `node compare.js` in `tools/SynthCheck` checks the two the mod still makes against it, sample by sample (worst difference 4e-5). The mod project leaves `tools/` out of its build.
     - There is a hum and buzz while locked that follow the squeeze, the flick at the hand, a whoosh from the flying object (louder and higher with speed), and a thump and vibration on the catch, harder for heavy objects.
5. **Rotation**: anything with a grip is turned during the flight so it arrives in the pose the game would hold it in. Since v0.25 that's always on, for everything (William had "Fit to hand" on All, and asked for the setting to go). Objects that can't be fitted can copy the hand's rotation since the flick, scaled by "Follow hand rotation" (100% is 1:1). "Reverse rotation", which turned them the opposite way, was removed in v0.34 (William: not needed).
   - A launch keeps the object's own spin. "Spin damping" was removed in v0.25 (William had it off): fitted objects are turned to the hand anyway.
   - The held pose is the grip's `GetNeutralTargetInWorld` (world pose of the grip target) composed with `GetForcePullHandTransform`, which looks like the hand's pose relative to that target and is constant per grip.
   - `GetHandInTarget` is unusable before the hand touches the grip: it returned world positions.
6. **Wrist pockets** (v0.21.0, reworked in v0.22.0, `src/WristPockets.cs`): William asked for Alyx's wrist pockets. Each wrist has a pocket for one object, filled and emptied by the other hand.
   - **The pocket:** a glowing ring, by default on the back of the wrist a little up the forearm. It's set off from the wrist along the hand, out of the back of the wrist, and across it toward the thumb (Pocket position, height, sideways).
     - **Wrist point:** the anchor of the joint between the physics hand and its forearm, found on either body (v0.22). The forearm is the rig's `m_elbowLf`/`m_elbowRt`. v0.21 looked in `hand._forearmJoint`, which was empty, so it always estimated the wrist as 8 cm back from the palm, and that's still the fallback.
     - **Orientation:** the physics hand's own axes: `±right` (right on the right hand, left on the left) is the back of the hand, and `forward` runs along it. v0.22 measured these to match the controller's exactly.
       - v0.21 used the controller's axes, and the pocket wandered off the wrist while holding things (William): a grip turns the physics hand away from the controller.
       - v0.22 learned the hand's axes from the controller all the time (averaged over 3 s), and the pocket slowly turned around the wrist (William).
     - **Watch:** the back of the wrist is the forearm's own outward axis (`elbow.right` on the right arm, `-elbow.right` on the left), the same way round on both arms.
       - v0.22 to v0.25 learned it from the hand over the first 3 s of each level, which picked up however the wrist was twisted then. In the v0.25 test one level learned `(-1.00, 0.03, 0.09)` on the left and `(0.96, -0.28, 0.07)` on the right, 17° apart, and William found the pockets didn't match. Other levels had learned `(±1.00, -0.02, …)`, the forearm's own x axis.
     - **"Pocket follows":** Hand (default) keeps it fixed on the hand. Forearm keeps it on the forearm like a watch (William's request), running from the elbow to the wrist, so bending or turning the hand doesn't move it.
     - William put it on the back of his hand (position −5 cm) and liked it there.
     - **Rotation** (v0.24, William): "Pocket rotation" turns the pocket around the wrist toward the thumb, moving it with the turn. "Pocket tilt" tilts its face toward the fingers without moving it.
     - The ring grows and brightens when something that can go in is at it, and is fainter when empty.
     - **The ring adds light** (v0.27) with the additive object-glow material, like the see-through copies. The plain line looked dim when it opened (William).
     - **Pocket faces you** (v0.27, on): as in Alyx, the ring and the miniature turn to face the eyes, and toward the other hand's palm while it reaches in or brings something to the pocket, turning smoothly (10 per second). The centre stays on the wrist, and swapping still goes by the wrist's own axes. Both are drawn over everything (`_ZTest` Always, overlay queue), so a turned ring never disappears into the arm. Off, the ring lies flat on the wrist as before.
   - **Storing:** a held object whose nearest surface comes within reach of the other wrist's pocket opens it, with a light tick and no sound. Letting go there stores it, unless a hand still holds it.
     - **Pull-in effect and hum** (v0.27, from Alyx): while something is at the pocket, sparks are drawn from all over it into the ring (30 per second at 100%, each taking about 0.3 s), and the holding hand feels a slight hum ("Pocket hum"). Storing sends a burst of 24 sparks in. A hand reaching into a full pocket hums too.
     - **Not from underneath** (v0.26, William): the nearest point can be at most 2 cm below the pocket's face, so an object held under the arm doesn't go in. Reaching in to take something out still works from any side.
     - An object held by the pocket's own hand too, like a pistol in both hands, doesn't open it.
     - **What fits:** a `MarrowEntity`, not kinematic, up to the max size, and not itself plugged into something. Weapons (`Target.IsWeapon`: a `Gun`, a melee weapon's `StabSlash`, or a `WeaponSlot` for the game's holsters) always fit, whatever their size (v0.31, William): the max size decides what else goes in. Size is the longest side of its visible meshes in its own frame (`ObjectModel.MeasureSize`).
       - In v0.21 it used world-aligned collider bounds, so it changed with how the object was held: a 76 cm crowbar went in under a 40 cm limit, while a 57 cm MP5 didn't.
     - **Plugged in:** only the object's own plugs count (`AlignPlug._isLocked` whose entity is the object's). A magazine inside a gun sits among the gun's children, so in v0.21 the gun counted as plugged in once it had a magazine, and wouldn't go back in after being taken out (William's "couldn't put it in again").
     - **Size measure:** see-through meshes don't count if anything is solid, and neither does any single mesh longer than 1.5 × the colliders' diagonal + 10 cm. The model's bounds, which set the miniature's scale and the swap spot, skip them too.
       - In v0.23 the SPAWN_GUN and handgun_M9 measured 3.7–50 m, a different length each time, from some part that stretches. That's most likely a laser or beam. They were refused about 30 times (William's "can't put anything in, for no reason").
       - The refusal now names the largest part counted.
     - **Which object:** the object is the one the held grip's body belongs to (`TargetCache.Get(host)`). Before v0.24 it came from the hand's attached object, and a mismatch was refused silently.
     - **Not on a grip yet:** right after a grab, the hand can hold an object for a moment before it's on one of its grips. Nothing is decided until it is, and it's only logged if it lasts 0.5 s near the pocket.
       - In v0.24 and v0.25 that first moment's "held by nothing" was kept as the verdict for the whole hold. So something just caught by grab assist or taken out of a pocket wouldn't go in until it was let go and grabbed again: William's "grabbed wrong, so I can't put it in", from v0.23 on. The v0.25 log shows it right after take-outs and catches (an ammo crate among them).
       - A hand that came off its grip for a moment later in a hold logged "won't go in a wrist pocket: " with no reason.
     - It's worked out again for each object and body it's held by, once the object is near the pocket. The log says why something won't go in, once per hold, and the same refusal of the same object at most every 30 s (a windmill was logged 26 times in 40 s in the v0.25 test).
   - **Swapping** (v0.22, William): an object at a full pocket moves the stored one's see-through copy out beside the pocket (full size, lying along the forearm, 5 cm clear). Letting go brings the old object out there, where it drops, and stores the new one.
     - Since v0.23 the copy always goes on the little finger's side, lifted by half its thickness. v0.22 picked the side away from the head, which flipped as the arm crossed the body (William found it awkward).
     - **Clear of the body** (v0.26, William): from beside the pocket it's moved further out, away from the wrist and a little up, in 2 cm steps (up to 60 cm), only until its bounding box is 1 cm clear of every collider of the player rig (`src/PlayerBody.cs`). The log says how far that was. The copy shows the same spot.
       - **Smooth** (v0.27): the copy is shown easing (0.1 s) toward the clear spot, so it glides instead of jumping in 2 cm steps as the arm moves (William: "a little choppy"). The object comes out where the copy was shown if that's clear of the body, otherwise at the clear spot.
       - v0.24 and v0.25 moved bigger objects out by a share of their size (longest side × 0.2 sideways, × 0.1 outward, × 0.25 toward the fingers), so long ones like swords went far away even when nothing was in the way.
   - **Never the player:** nothing on a player's rig goes in (`Target.IsOnPlayer`, a `RigManager` among its parents, which Targeting already used).
     - In the v0.22 test the right hand grabbed William's own left arm by the PhysicsRig's `spheregrip` while reaching for the left pocket, and let go there.
     - The pocket hid the whole PhysicsRig: his body went back to the start, his head stretched toward it, and he couldn't do anything until he quit.
   - **Hiding:** the object is hidden with the game's own `MarrowEntity.Hide(true, true)`, part of the hide/cull/despawn state machine, so the pool doesn't treat it as despawned. If its GameObject is still active afterwards, it's switched off directly. In the v0.21 test `Hide` always worked alone.
     - Entities plugged into it go with it: magazines and their cartridges are found through each `Socket.LockedPlug`, plus any `_parasites`. The v0.21 test stored an MP5 with its magazine and 5 cartridge entities, and the Eder22 likewise.
     - `PreventDisableOnCull(true)` keeps zone culling from switching it off while it's away. The old value comes back 2 s after it's taken out.
     - **Coming back out** (v0.26): each body's `isKinematic` and `constraints` are noted when it goes in. When it comes out, a hibernating entity gets `ClearHibernation()`, every body gets those back, and is woken up.
       - In the v0.25 test a swapped-out object, and once a taken-out one, got stuck in the air (William). Nothing was logged. The likely cause is the game's culling: an entity kept from being switched off is probably hibernated instead, which saves and freezes its bodies (`HibernationBodyInfo` has `constraints`).
       - **Watch** (v0.28): every part that came out (the object, its magazine, its cartridges) is checked 0.5, 2.5, 6 and 12 s later. A part that's hidden, culled, hibernating or switched off is logged and woken up: shown, kept from being switched off, its zone trackers switched off and on (`ForceEnableTrackers`) so the zones notice it again, hibernation cleared, bodies unfrozen. In the v0.27 test weapons sometimes stopped working or froze in the air after coming out (William). That's what a culled entity kept from being switched off looks like: its scripts stop and its bodies freeze, but it stays visible. Hidden, its trackers are off, so the game can count it as having left every zone. Nothing logged it then.
       - 2 s after coming out, an entity the game still counts as culled isn't allowed to be switched off again; its trackers are refreshed instead.
       - **Checks, 0.4 s later:** something taken out and held by that hand logs "Take-out check for X: held N cm and M deg from how it was put away", and whether it's by the same grip. Something swapped out, or taken out but not held, that has moved less than 1 cm is logged with its state (switched off, hidden, culled, hibernating, ghosting, colliders, and how many bodies are kinematic, constrained or asleep), freed again, and checked once more.
   - **The see-through copy:** `src/ObjectModel.cs` copies the visible meshes relative to the body the object was held by. Skinned meshes are baked, with their scale picked by measuring, and only LOD 0 is kept.
     - One copy in the glow style's material and colour, with no colliders, moves between three places in 0.15 s (smoothstep from where it was shown), and shrinks into the pocket from where it was let go over 0.25 s. In between it sits exactly in place.
       - v0.22 eased it toward its place every frame (0.06 s), so it trailed behind the moving wrist (William).
       - **In the pocket:** a 6 cm miniature lying flat along the forearm and turning slowly. It's see-through since v0.22 (William); in v0.21 it used the object's own materials.
       - **In a reaching hand:** at the pose it was held in when it went in.
       - **Beside the pocket:** when it's about to be swapped out.
     - "See-through strength" scales all three.
   - **Taking out:** an open hand within reach of a full pocket brings the copy into the hand. A grip press there brings the object back with `Hide(false, true)` and `Teleport`, at the pose relative to the hand it was stored in. It's placed directly if the teleport misses by more than 2 cm (it never did in the v0.21 test).
     - Then `GrabAssist.StartInPlace` grabs the same grip with no move time, so the grip works out the same hold.
     - **Handles** (v0.28, `src/GripHold.cs`): while something held by a cylinder grip is at the pocket, the grip's target in its own frame (`GetTargetInBase`) and its per-hand state (`HandToGripState`: `isZFlipped`, `posInFlippedBase`, `rotInFlippedBase`, `dynamicFloat`, `targetRotationInBase`) are noted. After the take-out grab they're put back (`SetTargetInBase`, then `UpdateJointConfiguration`), but only if the grab holds it the same way round as noted, and `isZFlipped` itself is never set (v0.28.1). The log says "the handle's hold put back, held …" or "… not put back: the grab holds it …".
       - The way round comes from the grab, worked out from how the hand is turned against the handle where the object came out (see Which way round). v0.28 set `isZFlipped` back after the grab, once the joint was already made: the 11 take-outs where that changed it all came out reversed, and the grip's state then disagreed with its joint.
       - In the v0.27 test a cone and a morningstar came out 145–149° turned: grabbed the other way round, from the hand's handle given to the grip (see Which way round). Box grips came out exact.
     - Since the same hand always serves the same pocket, the stored pose is the one that hand held it in.
     - **Passing through the body** (v0.29, `src/PassThrough.cs`, "Pass through your body", on): William asked that a big object taken out inside his arm ignore the arm until it's out of it. At the physics step the hand grabs it (after grab assist, so pairs the game ignores on grabbing are left to it), each of its colliders (and its magazine's and cartridges') that's inside a part of the player rig (`PlayerBody.Inside`: bounds overlap, then `Physics.ComputePenetration`) stops colliding with that part (`Physics.IgnoreCollision`). Pairs already ignored are skipped.
       - **Hands** (v0.29.1): included. v0.29.0 left them out, and the other hand still bumped big objects coming out (William). The game keeps a hand's colliders from colliding with what it holds (and undoes that when it lets go), so a hand part of a hand holding the object is never made to collide again while it holds it, and is ignored again if the game lets it collide while it's still inside. In the v0.29.0 test a magazine came out inside `SoftHandRt`, a collider of the holding hand that isn't under its `Hand` and isn't ignored by the game.
       - Each pair collides again once they've been apart for 0.1 s. A pair whose collider is switched off (put back in a pocket) is dropped, since the physics engine forgets the ignore then. A level change undoes the rest.
       - The log says "X came out inside your body (N overlaps, with …), so it passes through until it's out", then when it's out, or that it was put away first.
   - **Reach** (v0.28): "Put-in reach" (the old "Pocket reach", `PocketReach`) for something held to the pocket, "Take-out reach" for an open hand, both 12 cm. Both are full straight out of the wrist's face and shrink to the "Side reach" share (50%) from the side and to nothing from straight below (`side + (1 − side)·cos θ` above, `side·(1 + cos θ)` below), so the pocket reaches out like a cone from the wrist (William's idea).
     - A hand that already hovers something it could grab other than the player's own arm (a `SphereGrip` on the `PhysicsRig`), like the body log, isn't counted as reaching into the pocket, so it gets that instead.
     - The pocket's offsets, reach and miniature always scale with the avatar's height (`BodyScale.Avatar`, height / 1.76 m), whatever "Scale with avatar" says. On small avatars the pocket sat over the body log, and the hand took the pocket's object instead (William, v0.27).
   - **The glove:** a hand that's reaching, or holding the grip it just took something out with, is left alone by its glove (`IsBusy`), and the built-in force pull is blocked for it.
     - **Hover lock** (v0.23): while reaching, the hand's hover is locked with `hand.HoverLock()`, the same lock grab assist uses, after ending any hover it had (`OnHandHoverEnd`, `_mHoveringReceiver = null`). A grab there then can't take anything but the pocket's object, like the player's own arm.
       - It's unlocked when the hand stops reaching, holds something, the pockets turn off, or the level changes. Grab assist takes over the lock when something is taken out.
       - Reaching has 25% hysteresis on the reach.
     - **Known clash:** while reaching into a full pocket, the hand can't grab anything else there, like a pistol held in the pocket's hand.
     - After storing, the hand has to leave the pocket before reaching into it counts, so the copy doesn't jump straight back into the hand.
   - **Placeholders until round 2:** the lock sound on storing, and grab assist's catch thump on taking out.
   - **Between levels** ("Keep between levels", on):
     - **Kept out of the level** (v0.28): while something is in a pocket, its entity and every entity plugged into it that isn't already a child of another are unparented and moved to the `DontDestroyOnLoad` scene, noting each one's parent and scene. Coming out, each goes back under its old parent if that still exists (like the pool it was spawned from), else into its old scene if that's still loaded, else into the rig's scene (or the active scene). So a level change doesn't take it, whatever it is: something found in a level, a gun with its magazine and ammo. On the level load, a pocket whose object is still intact keeps it ("came along to the new level").
     - Untested: the old level's pool may still destroy or despawn its objects, and an object carried into a new level may still refer to the old level's zones. The watch after coming out (see Coming back out) will show it.
     - **Crates kept loaded** (v0.33.1, `src/CratePatches.cs`): on a level change the game unloads every crate the new level doesn't need (`StreamSession`'s `Load` calls `AssetWarehouse.UnloadAllCrateAssets`, which calls `UnloadCrateAsset` per crate, which calls `MarrowAsset.UnloadAsset` on each of its assets; found by scanning `GameAssembly.dll` for calls, since Cpp2IL's dump of that coroutine is cut off). A carried object, and the pocket's see-through copy, are drawn with their crate's meshes, so they had nothing left to draw: William, after the v0.32 test, "the pocket looked empty after the level change", though the mod logged each object as having come along. Harmony prefixes on both `UnloadCrateAsset` overloads skip the crates of whatever is in a pocket, its magazine and cartridges included (`WristPockets.KeepsCrate`), logging "Kept the crate of X loaded for Y". The "came along" line says if the copy or the object is missing meshes.
       - Objects that are part of a level's scene (not from a crate) can't be kept this way: their meshes go with the level's own bundle.
     - **Spawn gun screen** (v0.34, `src/SpawnGunScreen.cs`): a spawn gun shows its preview on a screen (`SpawnGunUI`, a camera at (−9999, 9999, −9999)), which the first spawn gun in a level spawns in its `Start` if there's none (`AssetSpawner.Spawn(_spawnGunUISpawnable, …)`). One carried into a new level has had its `Start`, so the level had no screen, and `SpawnGun.OnTriggerGripAttached`/`OnTriggerGripDetached` threw on every grab and let-go (`SpawnGunUI.Instance` null): in the v0.33.2 test, 28 errors, and the take-out was "held by nothing". Now a carried spawn gun spawns the screen the same way 1 s after the level loads (after the level's own spawn guns), and again as it comes out of the pocket if there's still none.
     - **Cull check** (v0.33.2, `src/CullCheck.cs`): a Harmony prefix on `MarrowEntity.OnCullResolve`, for every object in the game, takes destroyed entries out of `_behaviours` and `_cullables` (what that function goes through, and throws on) before the game culls it. The first 20 objects per level are named in the log with what they had ("X (in the left pocket / came along from another level in a pocket / has been in a pocket / never in a pocket) had parts that were gone …"), and a level change logs how many there were. To find out whether the culling errors come from the pockets or from the game itself (`WristPockets.HistoryOf`).
     - **Dead references** (v0.33): in the v0.32 test, after a traffic cone came along from the menu to Street Puncher, the game logged a NullReferenceException in `MarrowEntity.OnCullResolve` (called by `InactiveObjectManager`) about every 2 s for the whole level: 386, against 4 in the session before. `OnCullResolve` goes through the entity's `_behaviours` and `_cullables`, and throws on an entry that's gone. William found the things he'd carried "weren't kept", though the mod logged each as having come along and never lost them; he didn't reach into a pocket in the levels after. So at a level load, and again when it comes out, each carried entity's `_behaviours`, `_cullables`, `_hideables`, `_despawnables`, `_poseables` and `_parasites` lose any entries destroyed with the last level, and the "came along" line says how many and whether the copy is still shown. Unconfirmed as the cause.
     - **Spawned again** as a fallback (v0.27): if it didn't survive, something spawned from a crate is spawned again 1.5 s after the new level loads (`HelperMethods.SpawnCrate` with its barcode, from `MarrowEntity._poolee.SpawnableCrate.Barcode.ID`), 3 m above the player's head. 0.1 s later it's put back in the same pocket, held the same way: its grip and body are found again by their paths under the entity, and the copy is made again from the new object. Given up on after 10 s, or if something else went in first.
       - In the v0.27 test all three tries (a traffic cone, an MK18, a Nimbus gun) logged "didn't spawn": BoneLib's spawn callback never came, with no error. Since v0.28 the crate's pool (`AssetSpawner._instance._barcodeToPool`) is also watched for a new object, and a failure logs what the spawner and pool held.
     - William: "are there no way to make objects consistently stay in-between levels?", and guns found while playing through the game would disappear.
   - Turning a pocket off ("Wrist pockets": Both / Left / Right / Off, since v0.30) drops what's in it beside the pocket, and that wrist then has no pocket.

## Settings (BoneMenu pages, with defaults)
Since v0.25 the pages follow a pull from start to finish, then the pockets, the look and the sound (`src/SettingsMenu.cs`). A page name after a slash is a page inside that page. Values are saved by setting name, so moving one between pages keeps it.

**Defaults** (v1.0.0): every default is William's own setting from his last v0.34 session (he chose all of them over only the gameplay ones), 47 changed. Earlier defaults are in the history below.

**Quick settings** (v0.30, first page, William's picks for what most players would change): Gloves, Max range, Max object mass, Wrist pockets, Pocket follows, Pocket max size, Debug view, and Reset to defaults. Each is also on its own page below. A setting can be on several pages (`Setting.Show` keeps every copy of its element showing the same value).

| Page | Setting | Default |
|---|---|---|
| Aiming | Max range | 15 m |
| Aiming | Cone angle | 11° |
| Aiming | View angle (only objects within it of where the head faces; 360 = all around) | 140° |
| Aiming | Target stickiness | 0.2 s |
| Aiming | Min object mass (ignored for anything with a grip) | 0.3 kg |
| Aiming | Max object mass | 50 kg |
| Aiming | Item priority (share of the cone angle) | 50% |
| Aiming | Aim from (Arm / Hand) | Hand |
| Aiming | Aim pitch, aim yaw (yaw mirrored so + is outward on both hands) | 0°, 5° |
| Aiming | Batch ammo, batch radius | on, 0.5 m |
| Flick | Flick speed threshold (wrist rotation) | 400°/s |
| Flick | Direction tolerance | 60° |
| Flick | Input button (Grip / Trigger / Either) | Grip |
| Flick | Cooldown | 0.5 s |
| Flight | Flight time | 0.45 s |
| Flight | Extra time per metre | 40 ms/m |
| Flight | Arc height | 1.1x |
| Flight | Avoid obstacles (flatten or raise the arc if the level is in the way) | on |
| Flight | Weight (heavier objects fly slower and lower and land harder; none up to 3 kg) | 70% |
| Flight | Hand prediction (extrapolates the player's movement; only with hand tracking and slowdown both off) | 0% |
| Flight | Follow hand rotation (objects that can't be fitted to the hand) | 20% |
| Arrival and catch | Hand tracking | 100% |
| Arrival and catch | Tracking starts at (share of the flight, then easing in to full at the end) | 30% |
| Arrival and catch | Tracking stops at | 0 m |
| Arrival and catch | Slowdown starts at (share of the flight) | 70% |
| Arrival and catch | Slowdown amount (0% = off, max 90%) | 30% |
| Arrival and catch | Float time (weightless at the hand before it falls, 0 = off) | 0.05 s |
| Arrival and catch | Float brake time (how long it takes to slow to a stop) | 0.5 s |
| Arrival and catch | Float follow (how much a floating object follows the hand's movement) | 50% |
| Arrival and catch | Float stops up to (the fastest arrival that still stops) | 4 m/s |
| Arrival and catch | Pull again after (how long after an uncaught object reaches the hand, or ends its float, before it can be pulled again) | 0.25 s |
| Arrival and catch | Grab assist reach (0 = off) | 0.4 m |
| Wrist pockets | Wrist pockets (Both / Left / Right / Off, which wrists have one) | Both |
| Wrist pockets | Keep between levels (kept out of the level while in a pocket, or spawned again from its crate) | on |
| Wrist pockets | Pass through your body (something taken out inside your arm, hand or body ignores that part until it's out of it) | on |
| Wrist pockets | Put-in reach, take-out reach (straight out of the wrist; scaled with the avatar) | 0.09 m, 0.17 m |
| Wrist pockets | Side reach (share of the reach from the side, so it reaches out like a cone) | 30% |
| Wrist pockets | Pocket max size (longest side, up to 300 cm; weapons always fit) | 60 cm |
| Wrist pockets / Placement | Pocket follows (Hand / Forearm, like a watch) | Forearm |
| Wrist pockets / Placement | Pocket faces you (turns to the eyes or a hand, drawn over the arm) | on |
| Wrist pockets / Placement | Pocket position (−20 to 30 cm up the forearm from the wrist, negative onto the hand), height (−10 to 15 cm out of the back of the wrist), sideways (±15 cm, + toward the thumb) | 8 cm, 4 cm, 1 cm |
| Wrist pockets / Placement | Pocket rotation (±180°, around the wrist toward the thumb), tilt (±90°, face toward the fingers) | 55°, 0° |
| Wrist pockets / Look | Ring thickness (both rings), ring opacity (the bright line, 0 = only the glow), ring glow (the soft ring, wider above 100%, 0 = off), ring size (the look only, not the reach) | 50%, 180%, 100%, 100% |
| Wrist pockets / Look | Own colour (off = the glove's colour), pocket hue and saturation (the ring, the see-through copies and the sparks) | off, 170°, 100% |
| Wrist pockets / Look | Miniature size, see-through strength (all copies) | 6 cm, 120% |
| Wrist pockets / Look | Pull-in effect (sparks into the pocket, 0 = off) | 100% |
| Look | Glow on the selected object | on |
| Look | Glow intensity | 300% |
| Look | Glow in flight (fades out gradually, gone by this share of the flight) | 70% |
| Look | Line, from the object toward the hand (Always / Locked / Off) | Locked |
| Look | Line length (share of the way to the hand) | 65% |
| Look | Line thickness (vs. the original thin line) | 130% |
| Look | Line opacity (vs. the original) | 300% |
| Look | Line glow (added light, 0 = off), line stiffness (stays straight until pointed stiffness × 45° away) | 190%, 30% |
| Look | Colour hue (30 = Alyx orange) and saturation | 30°, 100% |
| Look / Advanced | Line grip response (how much its strength follows the squeeze) | 100% |
| Look / Advanced | Line retract time (shoots back into the object on the flick, 0 = vanish) | 0.12 s |
| Look / Advanced | Line flare (the bright start at the object: 0% fades in softly, 100% solid and round, sprite grows with it) | 40% |
| Look / Advanced | Glow style (Standard / Additive / Hologram) | Additive |
| Look / Advanced | Sparks along the line (experimental, 0 = off) | 25% |
| Sound and haptics (one page since v0.34) | Sound, volume | on, 60% |
| Sound and haptics | Lock hum, lock vibration (while locked, follow the squeeze) | 10%, 10% |
| Sound and haptics | Catch thump, flight whoosh | 40%, 40% |
| Sound and haptics | Haptic strength | 90% |
| Sound and haptics | In reach tick (up to 400%), in reach tick length, pocket hum (0 = off) | 200%, 150 ms, 100% |
| General | Gloves (Both / Left / Right / Off, which hands have one) | Both |
| General | Scale with avatar (distances × avatar height / 1.76 m) | off |
| General | Debug view (aim cone, planned path) | off |
| General | Reset to defaults | button |

**Removed in v0.25** (William): pulse speed, length and amount (his 2 m at 0.5 per metre made a solid line, and he kept that look), the lift-off puff, spin damping (now always off), auto-grab and its catch radius, and fit to hand (now always on for everything). Their entries, and four left over from early versions (`FlickSpeedThreshold`, `Highlight`, `ArrivalBraking`, `BeamMode`), were taken out of his `MelonPreferences.cfg`. A backup is next to it as `MelonPreferences.cfg.before-0.25.bak`.

Flight time, flick threshold and cone angle have the biggest effect on the feel.

## Notes and risks
- BONELAB's hands are physics-driven, so catching and grab assist may be janky.
- BONELAB already has a short-range force pull built in. Consider patching it rather than replacing it, and check that the two don't conflict.
- Related existing mods to look at for reference: Telekinesis (ibemakingshit) and Force Throw (thegbus), both on Thunderstore.

## Workflow
- William tests in the headset. Keep each change small, and expose tunable values as settings so tuning doesn't need a rebuild.

## Status
v1.0.0 was uploaded to Thunderstore by William on 2026-10-09. v1.0.1 only changes the description (William's: "Adds the gravity glove grab mechanic from Half-Life: Alyx. Also including functional wrist pockets"); a description can only change with a new version.

v1.0.0 is the first release, packed for Thunderstore as `release/GravityGloves-1.0.0.zip` but not uploaded. It's v0.34.0 with:
- **Release build:** `dotnet build -c Release` builds without the testing log lines and packs the zip (the `Package` target in the .csproj): `manifest.json`, `README.md` (William's own text, only his typos fixed) and `CHANGELOG.md` from `package/`, `art/icon.png`, and `Mods/GravityGloves.dll`. Its dependencies are `LavaGang-MelonLoader-0.7.3` (what William tests on) and `bonelib-BoneLib-3.2.2`, the latest on Thunderstore for BONELAB on 2026-10-09. Its name is `Working_HLA_gravity_gloves_with_wrist_pockets` (William's title, "Working HLA gravity gloves + wrist pockets"; Thunderstore names only allow letters, digits and underscores, and show underscores as spaces). The package's team (namespace) is set on Thunderstore when uploading. In the game it's still "Gravity Gloves" (MelonInfo, BoneMenu).
- **Testing lines only in Debug:** every informational log line, and warnings about how things went rather than something failing (held in a broken state, didn't spawn, still stuck, woken up after a pocket), go through `Mod.Trace`/`Mod.TraceWarning`, which are `[Conditional("TESTLOG")]`. Only the Debug build defines `TESTLOG`, so the Release build leaves the calls out, arguments and all, and logs only errors and failures. The Debug build is still the one `dotnet build` installs into the game for testing; the Release build doesn't touch the game.
- **Defaults are William's settings** (see Settings).
- **Release test** (11 minutes, one level, with his content mods): nothing from the mod in its log. William: "some of the modded guns seemed to break after some time". Player.log had 17722 `GunTrigger.Update` NullReferenceExceptions, 3593 from `ShellSFX.ImpactSound`, 1874 from `DamageVolume.Update`, 6 from `Gun.DespawnCartArt` (a magazine coming out), and 89109 lines printing a position like "(-0.12, 0.01, -0.13)", which started right after something was spawned; none of these were in the earlier sessions that day. No sign yet whether the mod plays a part. The Debug build is back in the game, and now copies the game's errors into MelonLoader's log with times (`src/GameErrors.cs`, test build only): the first of each kind per level as "Game error: …", and how often each repeated at the level change. Next: match them with the pulls and pockets of the guns that broke, and ask whether those guns break without the mod.
- Untested in the game: everything since v0.34.0's short first session (1.5 minutes, 18 pulls, no errors; the hand's grab sphere is 18 cm across, centred 7.2 cm out from the palm), including the one sound page, the spawn gun screen and the Release build itself.

v0.34.0 is v0.33.2 plus William's requests:
- **One sound page, with only the sounds in use** (see Feedback): his custom lock, hum, release and flick sounds are built into the mod, and the select, catch and whoosh are the built-in ones he had. The "Sound choices" and "Custom sounds" pages, the custom sound folder and the unused built-in sounds are gone. Their 16 entries and the `GravityGlovesCustomSounds` category were taken out of his `MelonPreferences.cfg`; a backup is next to it as `MelonPreferences.cfg.before-0.34-sounds.bak`. His files are still in `UserData/GravityGloves/Sounds`, which the mod no longer reads. Check that every sound plays as before.
- **The in reach tick is stronger and longer**, with a length setting, and only comes once the object is within the hand's normal grab range (see In reach tick). Check the log's "The hand's grab sphere is …" line.
- **Reverse rotation is gone.** Its entry is taken out of his `MelonPreferences.cfg` on install (it was off, the old default); a backup is next to it as `MelonPreferences.cfg.before-0.34.bak`.
- **Pull again after** sets the wait before an uncaught object can be pulled again (see Pull again after).

- **A carried spawn gun gets a screen** in the new level (see Spawn gun screen). Check for "This level had no spawn gun screen" and that grabbing it gives no `SpawnGun.OnTriggerGripAttached` errors.

v0.33.2 was tested (47 minutes, three levels). It's v0.33.1 plus the cull check (see Cull check).

What the v0.33.2 test showed:
- **The culling errors are gone:** none in `Player.log`, against 1308 in the v0.33.1 test. Patching `OnCullResolve` moves any error it still throws into MelonLoader's log as "During invoking native->managed trampoline": there were 2, of another kind (`HibernationBodyInfo.CopyTo` on a body with no rigidbody, 10 s before a level change).
- **Whose they were:** 1, 81 and 126 objects in the three levels, each with exactly one empty slot (a null entry, not a destroyed object). Every one named was the level's own and never in a pocket: couches, tables, doors, sockets, NullBodies, skeletons, gacha capsules. 1 of the 126 had been in a pocket. 81 of them came at the level change itself. So the pockets don't break objects; why these levels' objects have empty slots is unknown, and the check makes it harmless.
- **The carried SPAWN_GUN broke** in the new level (see Spawn gun screen). It also logged missing 3 of 26 meshes when it came along. The milk crate came along fine.

v0.33.1 was tested (35 minutes). It's v0.33.0 plus crates kept loaded for what's in the pockets (see Crates kept loaded).

What the v0.33.1 test showed:
- **Pockets kept their objects between levels** (William: "the pocket items were kept this time"). At all four level changes the crates of everything in the pockets were kept, magazines and cartridges included, and the objects came along: a traffic cone and the milk crate, then an M9 and an MK18 twice. The MK18 was missing 1 of its 49 meshes both times, probably a part that never had one.
- **Pulls while moving:** of 33 pulls where the player moved 50 cm or more, 26 were caught. The 6 that fell ended 21–78 cm from the hand (in the v0.32 test most such pulls fell 50–99 cm short).
- **The game's culling errors** (`MarrowEntity.OnCullResolve` NullReferenceException) went on: 1308 in the session, 378, 100 and 826 in the three levels with carried objects, 2 at the start and 2 in the last level. Dropping dead references at a level load didn't stop them, so their cause is still unknown and may not be the pockets. Next step if wanted: a prefix on `OnCullResolve` that names the entity and cleans its `_behaviours`/`_cullables`. No visible effect reported.
- After coming out, objects were often found culled 0.5 s later and woken up (the watch), as before.

v0.33.0 answers William's v0.32.0 test:
- **Pulls while moving** follow the player (see Moving with the player). Check that close pulls while strafing or backing away reach the hand, and the arrival lines' "the player N cm".
- **Carried pocket objects** drop dead references to the last level (see Dead references). Asked William what he saw when they "weren't kept": an empty-looking pocket, nothing coming out, or something broken in the new level. Check the "came along" lines and the game's own `Player.log` (in `AppData/LocalLow/Stress Level Zero/BONELAB`) for `OnCullResolve` errors.

v0.32.0 was tested as above. It was v0.31.0 plus a Wrist pockets / Look page (William: settings for the pocket's look, like the line's): ring thickness, opacity, glow and size, and the pockets' own colour (`Palette.Pocket`), with the miniature, see-through strength and pull-in effect moved there from the Wrist pockets page.

v0.31.0 is v0.30.0 (also untested) plus weapons always fitting in the pockets, whatever the max size (see What fits). Check with a long rifle or sword and the max size set low.

v0.30.0 is v0.29.1 (also untested) plus:
- **Quick settings page** (see Settings).
- **"Gloves" and "Wrist pockets" are dropdowns** (Both / Left / Right / Off). They replace the "Left hand" and "Right hand" toggles and the on/off "Wrist pockets", under new names (`Gloves`, `PocketWrists`). The three old entries were taken out of William's `MelonPreferences.cfg`, all on, the same as the new defaults; a backup is next to it as `MelonPreferences.cfg.before-0.30.bak`.
- To check: changing a quick setting shows on its own page too, and back; Reset to defaults from either page; one wrist's pocket off drops what's in it and the other still works.
- Wanted before uploading (William agreed to wait; the defaults, the quieter log and the package were done for v1.0.0): the defaults (his own settings differ: grab assist 0.4 m, flick 400°/s, tolerance 60°, aim from Hand, max mass 50 kg, prediction 0%, slowdown 30% from 80%), a quieter log with the testing lines behind Debug, a fresh-install test, campaign level changes, avatar sizes, one hand only, Fusion, and the Thunderstore package (manifest, README, Release build, 1.0.0). The icon is William's own image since v1.0.0 (a glowing orange pistol with the line arcing from it to the bottom right, on black): `art/icon-1024.png`, and `art/icon.png` scaled to 256×256 for Thunderstore. The first icon, drawn from his sketch, came from `tools/Icon/make.py` (`python tools/Icon/render.py`, an SVG rendered with Edge); running that again would overwrite his.

v0.29.1 answers William's v0.29.0 test:
- **Hands pass through too** (see Passing through the body).
- **Magazines from the ammo pouch no longer open a wrist pocket from anywhere.** An object's colliders are listed once (`Target.Surfaces`), and pooled magazines and their cartridges get switched off or passed to other magazines. Unity gives a switched-off collider's closest point as the point asked about, so the nearest surface read as at the pocket from across the body: the pocket opened, drew its sparks from the magazine, and swallowed it when let go, after which the game despawned it ("The left pocket lost mag_1911: the game despawned it", 5 times in 10 s). Now a collider that's switched off or no longer on the object is skipped and the list is made again, and a closest point outside the collider's bounds is ignored. This also affects grab assist's reach and the in reach tick, which use the same nearest point.

What the v0.29.0 test showed (13 minutes, no errors):
- **Passing through worked** (William), for example an axe out "with Sphere, ForearmLf, ElbowLf, SoftHandRt", out of the body 0.2 s later.
- **Reverse grips are fixed** (v0.28.1): 39 handle take-outs (swords, axes, a bat, the cone, the milk crate), every one "the handle's hold put back", the cone always "held the other way round (z-flipped)" as it went in. 34 came out within 14°, the other 5 at 18–55° (a cone twice, the crate, a sword twice), none reversed. Magazines and guns 0–22°.

v0.29.0 was tested as above. It was v0.28.1 plus passing through the body after a take-out (see Passing through the body).

v0.28.1 answers William's v0.28.0 test, where knives and bars came out of the pockets in a reverse grip:
- **Handles are grabbed the way round they're held:** grab assist gives every grip the hand's root, as the game's own hover does, instead of the hand's handle (see Which way round). That covers pocket take-outs and grab assist's catches.
- **The pockets no longer turn a handle's state round after the grab** (see Handles), and `ignoreGripTargetOnAttach`, which the game never reads, is gone.
- To check first: take-out checks of handles (dagger, wrench, bat, sledgehammer, milk crate) near 0°, and every take-out line saying "the handle's hold put back". Fit checks of caught handles should stay where they were (11–45°).

What the v0.28.0 test showed (12 minutes, 74 pulls, 26 pocket stores, 17 take-outs, no errors at all):
- **Reversed handles:** every take-out was a cylinder grip. The 11 where the grab's way round differed from how it went in ("turning it round") came out 96–179° from how they went in; of the other 6, 4 came out within 7°, and 2 had gone in reversed from an earlier take-out and came out the original way. William: "a knife or a bar will get grabbed in a reverse grip".
- **Between levels:** a dagger in a pocket "came along to the new level" (the log ends right after).
- **Watch:** a bottle 0.5 s out (culled) and a sledgehammer 2.5 s out (culled, hibernating, switched off) were woken up.

v0.28.0 was tested as above. It answered William's v0.27.0 test:
- **Objects kept between levels** by keeping them out of the level while in a pocket, with spawning again as the fallback (see Between levels).
- **Separate put-in and take-out reach, a cone-shaped reach, yielding to the body log, and pockets scaled with the avatar** (see Reach).
- **Handles come out held the same way round** (see Handles). It didn't work (see the v0.28.0 test above).
- **Weapons that stop working or freeze after coming out** are watched for and woken up (see Watch).

What the v0.27.0 test showed (48 minutes, no errors from the mod; MelonLoader's `SLZ_Module.PostSceneEvent` NullReferenceException on level loads):
- **Spawning again never worked:** three tries, no spawn callback (see Between levels).
- **Take-outs:** guns 0–4 cm and 0–32°; handles sometimes reversed (see Handles); an enemy turret, held by its M16 charging handle, missed its teleport by 32–49 cm and came out 62–173° off.
- **William found:** put-in and take-out need separate distances; on small avatars the pocket sat where the hand grabbed the pocket's object instead of the body log; weapons sometimes stopped working or froze in the air after coming out; things should stay between levels consistently, including guns found in a level.

v0.27.0 was tested as above. It added William's requests after the v0.26.2 test:
- **Pockets keep their contents between levels** (see Between levels).
- **Tracking starts at** (see Hand tracking).
- **Line glow and line stiffness** (see Selection).
- **Pocket faces you**, drawn over the arm, with a ring that adds light, a pull-in effect and a hum (see The pocket and Storing).
- **Swapped-out copies glide** instead of jumping (see Swapping).
- **In reach tick** (see Arrival and catch).

What the v0.26.2 test showed (24 minutes, 236 pulls, no errors from the mod; MelonLoader's `SLZ_Module.PostSceneEvent` NullReferenceException once on a level load):
- **Take-outs held as they went in:** 0–1 cm and 0–1° every time, including a plank whose box grip wanted a hold 25 cm and 120° away (that was the target set where the hand is; see Holding where the hand is). No cylinder grip was taken out, so the knife isn't confirmed yet.
- **Even hovers:** of 74 floats that fell, 37 were under 0.2 s past their planned arrival, 35 at 0.2–0.4 s and 2 at 0.4–0.6 s (v0.26.1 had up to 1.04 s).
- **Catches:** catch checks 1–7 cm, handles in their own hold at 0 cm and 0°.

v0.26.2 was tested as above. It answered William's v0.26.1 test:
- **Take-outs and turned holds** keep the hand where it is (see Holding where the hand is). Check the take-out checks first.
- **Braking ends on time** (see Floating), so hovers should be even.
- **Guided flights** can't shove a blocked object through the floor, flying objects use continuous collision, and far objects aren't pulled (see Launch).

What the v0.26.1 test showed (34 minutes, no errors from the mod, no broken grabs, no stuck objects; 3 NullReferenceExceptions inside MelonLoader's `SLZ_Module.PostSceneEvent` on level loads):
- **Grabs:** grab assist confirmed every grab itself, and pockets took everything caught. Catch checks were 0–6 cm (the cone 11 cm). Guns stayed 0–2° from the fitted hold.
- **Own holds** of grips that aren't fixed, read right after the hover begins, were 15–106 cm and up to 180° from the hand, so nearly all were replaced by where the hand is. Handles fitted in flight (torch, sledgehammer, aluminium table) were at 0 cm and 0°.
- **Take-outs:** cylinder grips came out reversed (see Holding where the hand is), barrels 15–41 cm and 23–31° off, guns 0–1 cm.
- **A 48 kg crate** taken out of the pocket wasn't held 0.4 s later.
- **William found:** a knife looked right in the hand but came out in a reverse grip; a gun he took out may have fallen through the map (the log only shows the DevManipulator doing that, after a blocked flight, see Launch); some objects hovered longer than others at similar distances.

v0.26.1 was tested as above. It answered William's v0.26.0 test:
- **Grab assist confirms every grab itself, at once** (see Confirmed at once). Check for "held in a broken state" warnings, "held by nothing" refusals, take-out checks and catch checks.

What the v0.26.0 test showed (12 minutes, no errors):
- **Broken grabs:** every grab the game closed by itself, after grab assist's hover or the hand-over, ended with the hand on no grip (see Confirmed at once). Guns, confirmed by grab assist, were 0–2° from the hold.
- **The hand's handle** is 6.8 cm from its root and 3.1 cm from its palm.
- **Swaps** moved the old object 2–10 cm further out to clear the body.
- No stuck objects were logged, and no "held in a broken state" lines existed yet.
- **William found:** the crate's handle went inside the hand after a flick; a crate taken out of the pocket was held with a stretched hand; a crate, a sword and other weapons caught after a flick were held weirdly and wouldn't go in a pocket.

v0.26.0 was tested as above. It answers William's v0.25.0 test:
- **Objects that wouldn't go in** after a catch or take-out (see Not on a grip yet).
- **The watch pockets match** on both arms (see Watch).
- **Not from underneath** (see Storing).
- **Swapped-out objects** only move as far out as it takes to clear the body (see Swapping).
- **Stuck objects** coming out of a pocket are freed, and checked and logged (see Coming back out).
- **View angle** for selection (see Selection).
- **Grab assist handed grabs to the game's own hover** for anything without a fixed grip, and scored grips from the hand's handle. That broke the same way (see Confirmed at once).

What the v0.25.0 test showed (23 minutes, no errors):
- **Watch:** the learned axes were 17° apart between the arms in the second level (see Watch).
- **Pocket refusals:** "held by nothing" right after every take-out and several catches, and a few with no reason at all (see Not on a grip yet). The rest were real: a 262 cm gallows, a 1037 cm windmill, a 310–331 cm articulated arm, a 513 cm taxi.
- **Wrists:** at the joint, 14.0 cm from the palm with a 3.04 m avatar, forearm 66 cm.
- **Catches:** fitted guns and axes still 0–1° from the hold. Caught by hand, the traffic cone (85°, by its `GripPoint`, not the main box grip), a pickaxe (82°) and a wash bucket (68°) were held far from the fitted hold, by other grips. The 10 kg `dest_ammoBoxLight` was caught by grab assist where it touches, by its box grip, and its palm settled 10 cm from where the hand closed once.
- **William found:**
  - things sometimes grabbed weirdly, from a pocket and when catching (an ammo crate)
  - the left and right pockets didn't seem to match when he rotated them
  - things could go in from under the arm
  - a swapped-out object got stuck in the air, and once a grabbed one
  - long swapped-out objects went further out than needed
  - he asked for nothing behind him or out of view to be selected

v0.25.0 was tested as above. It only tidies the settings (William's choices from the full list):
- **Removed:** the pulse settings, the lift-off puff, spin damping, auto-grab with its catch radius, and fit to hand (see the settings table). Apart from his 25% puff going, nothing he uses changes: the line looks the same, spin damping stays off, and fitting stays on for everything.
- **Pages reordered** (see the settings table): Aiming (with ammo batching), Flick, Flight (with rotation), Arrival and catch, Wrist pockets (with a Placement page), Look (glow, line and colour, with an Advanced page), Sound and haptics (with Sound choices and Custom sounds inside), General.
- **Config cleaned:** 12 unused entries removed from his `MelonPreferences.cfg`.
- **Line preview:** the pulse sliders are gone, and it draws the solid line.
- The custom sounds README now gives the new menu path, so the mod rewrites it on the next start.

v0.24.0 was also untested when v0.25.0 replaced it. It answers William's v0.23.0 test.
- **Size measure fixed** (see What fits).
- **Refusals logged:** the held object is found from its grip, and every refusal is logged.
- **Rotation:** pocket rotation and tilt settings.
- **Swapping:** bigger objects come out further away.

What the v0.23.0 test showed (40 minutes, no errors):
- **Watch:** learned the back of the hand as `(-1.00, -0.02, 0.08)` on the left forearm and `(1.00, -0.02, 0.02)` on the right, so the forearm's axes match the hand's too.
- **Wrists:** both found at the hand–forearm joint, 5.1 cm from the palm, forearm 29 cm.
- **Pocketing:** many stores, swaps and take-outs worked, rifles and axes included. No PhysicsRig was pocketed.
- **Refusals:** every one was the bogus size of SPAWN_GUN or handgun_M9.
- **William found:** sometimes nothing would go in, and sometimes it was "grabbed wrong" so it wouldn't go in. He also wants a pocket rotation setting, and big swapped-out objects further away.

v0.23.0 had answered the v0.22.0 test:
- **Player rig:** never pocketed, and the hand's hover is locked while reaching.
- **The copy:** sits exactly in place between moves.
- **The pocket:** follows the hand's own axes, with no more learning.
- **Swapping:** always on the little finger's side.
- **Float:** settles at the float point.

What the v0.22.0 test showed (12 minutes, 137 floats, 17 pocket stores including the PhysicsRig, 8 swaps and 8 take-outs):
- **Wrist placement:** both wrists were found at the joint between the hand and the forearm, 6.1–6.2 cm from the palm, with forearms of 27 cm. The learned back of the hand was exactly `(-1, 0, 0)` on the left hand's frame and `(1, 0, 0)` on the right.
- **William found:**
  - the miniature lagged
  - the pocket slowly rotated sometimes
  - taking things out sometimes grabbed weirdly, and then nothing would go in
  - swaps sometimes put the old object on the other side
  - just before quitting his body teleported to the start with his head stretched toward it. The log's last line was "Right hand put PhysicsRig in the left pocket, held by spheregrip".
- **Take-outs by non-fixed grips** all logged "the game closed the hand on it after 1 of 3 hover steps", which is normal.
- **Floats:** 65 left the float (see Settling), 27 fell after their time, and 45 were caught. His float time was 0.05 s, his brake time 0.5 s and Float follow 50%.

v0.22.0 was also built in answer to the v0.21.0 test:
- **Float:** it follows the hand (Float follow), and only slow objects that aren't heading for their stop float on the spot.
- **Pockets:**
  - fixed the magazine bug
  - size measured in the object's own frame, max size 100 cm by default
  - placed from the physics hand, plus the watch option
  - wider position ranges and a sideways setting
  - see-through miniature
  - swapping

What the v0.21.0 test showed (20 minutes, 232 floats, about 60 pocket stores, no errors from the mod):
- **Pockets:** `Hide` always worked alone, and every take-out landed in place ("moving it 0 cm", no teleport corrections).
  - Guns went in with their magazines and cartridges.
  - The wrist was always estimated from the palm (`_forearmJoint` was empty).
  - The only refusals were sizes over 40 cm, and "plugged into something" for guns that had been given a magazine (the bug fixed in v0.22).
  - William raised the max size to 150 cm and moved the pocket onto the back of his hand (position −5 cm).
- **Float:** 111 floats fell after their float time, 41 + 36 were caught while still braking, 22 + 15 while floating, and 7 left the float. William shortened the float to 0.1 s and then 0.05 s, turned the slowdown off, and tried hand tracking 40–100% with the tracking stop at 0.
  - With 100% tracking, close pulls stopped 17–37 cm from the hand while the hand moved 41–70 cm after the flick. Fixed with Float follow.

v0.20.0 was also untested when v0.21.0 replaced it. It adds the float at the hand (see Arrival and catch), on by default.
- **Defaults:** 0.5 s float, 0.2 s brake time, stops up to 4 m/s.
- **Simulated outcomes:** a simulation of the flight math with a still hand gave these. It ignores the hand's collider, which would stop anything going past.
  - **Default flight settings:** pulls from up to about 2 m come in at 1.6–3.8 m/s and float within 6 cm of their stop, after about 0.17 s of braking. 3 m pulls go past at 3–4 m/s and float 40 cm on, or leave the float. From 5 m on they carry through at 6.5 m/s and more.
  - **William's settings** (0.45 s flight time, 40 ms/m, slowdown 40% from 70%): pulls from up to about 2 m float within 3 cm, and 3 m pulls 9–16 cm past their stop. Pulls from 5 m drift out of the float, and from 8 m they go through at 4–6 m/s. With his slowdown off, it's like the defaults: up to 2 m within 8 cm.
  - His v0.19.2 pulls were 16% from 1–2 m, 15% from 2–3 m, 25% from 3–5 m, 19% from 5–8 m and 26% from 8 m or more.
- **Untuned:** his hand tracking is still at 70%, and his slowdown is still on. Ask how the two feel together with the float.

v0.19.3 was also untested when v0.20.0 replaced it. It fixes the inaudible custom hum (see Even out levels and Loops start anywhere under Feedback). It also logs how far from the hand each flight ended: "X reached the end of its path N cm from the hand, which moved M cm since the flick (hand tracking P%)". The reason can also be the tracking stop, or being knocked off its path.

What the v0.19.2 test showed (246 pulls: 93 grab assist catches, 88 caught by hand, 4 ammo batches, 61 with no catch). The mod logged no errors. All seven custom files loaded. William found:
- **The custom hum didn't seem to play.** Its quiet start and his 10% lock hum made it inaudible. Fixed in v0.19.3.
- **Some objects didn't quite reach his hand.** Some objects were pulled again and again from 1–1.5 m without being caught, for example a shotgun_M4 four times in 6 s. That's the pattern of landing short and dropping near his feet.
  - The likely cause is his hand tracking at 70%. A guided flight ends at its launch-time destination plus only 70% of the correction toward the tracked hand, so it lands 30% of the hand's movement since the flick short.
  - His slowdown (40% from 70%) and 10 cm tracking stop leave it coasting slowly from there, so it drops.
  - The new arrival log line will confirm it. The fix to suggest is hand tracking at 100% (the default).

v0.19.2 read WAV files itself (`WavFile`). In v0.19.1 every custom sound failed to load with "Method not found: DownloadHandlerAudioClip..ctor(String, AudioType)". William's 7 files, all 16-bit stereo PCM at 44.1 kHz, now read correctly outside the game. A test project in the scratchpad also checked every supported encoding against generated files.

v0.19.1 added a Release folder, for a sound when a lock ends without a flick (William). The folder was created in his `UserData`. The mod now rewrites its `README.txt` whenever a new version changes the text.

v0.19.0 added custom sounds (see Feedback in Mechanic): William asked for sound folders where several files rotate at random, with no file playing twice in a row and a volume for each. The six folders were created in his `UserData`, and the mod writes a `README.txt` there on its first start. The built-in sounds still match the preview (worst 0.003).

v0.18.3 fixed flicks at targets almost straight below the hand. From 60° to 80° below, the flick axis blends from the world's horizontal axis to the controller's side-to-side axis (`controller.right` projected off the target direction), so it follows how the wrist really turns. Its sign doesn't matter, since a flick counts either way there. Pulls at targets 60° or more below log the angle off and how far below. William's direction tolerance was set from 50° to 60° in his config, for the left-hand misses.

v0.18.2 fixed catches the game finished itself. When the game closes the hand on the object during grab assist's hover, that now counts as the catch: it plays the thump and vibration, logs "Grab of X by Y: the game closed the hand on it after N of 3 hover steps", and queues the catch check.

What the v0.18.1 test showed (168 locks, 135 pulls, 46 grab assist catches, 5 ammo batches). The mod logged no errors. There was one NullReferenceException inside MelonLoader's `SLZ_Module.PostSceneEvent` on a level load.
- **Holds by the main grip are right:** guns 0–2° and 0 cm (MK18 10°), handles 0–4° and 1–6 cm. The milk crate is at 0° and 2 cm, where v0.15 put the hand 30 cm and 90° off.
- **The game grabs during the hover:** on all 26 catches by grips that aren't fixed (handles, and catches where it touches), the game closed the hand while it hovered, because the player is holding grip. Grab assist then gave up quietly: no thump or vibration, no "Grab of" or "Catch check" lines, and `SettleHold` never ran. The holds were still good.
  - Fit checks on touch catches compare with the default hold, so they can't show whether the hand stayed where it closed. The catch check will.
- **Left-hand flicks:** 31 pulls, and 6 fast flicks (641–1262°/s) missed at 50–61° off, against William's 50° tolerance. The right hand had 104 pulls and 1 fast miss. The flick axis is world geometry, the same for both hands, so the likely cause is how the left hand is rolled. A 60° tolerance would have caught 5 of the 6.
- **Targets almost straight below** (82–86°, for example a Morningstar at his feet): 5 misses, 58–78° off. That steep case uses the swing toward or away from the player, which assumes the palm faces the player or away from them.
- **Other results:**
  - Avoid obstacles changed 5 of 135 arcs.
  - Weight applied twice. A 10 kg chair took 1.31x the time and arced 71% as high.
  - With Fit to hand on All, the grips of ammo boxes and gacha capsules put the hand 57–164 m away, so those aren't turned.
  - The broom glowed (2 skinned meshes).

v0.18.1 only changed defaults, to William's preview picks: the sounds RankerClick, Clasp, Plasma, Reverse, Thump and Hum, and lock hum 10%. His `LockHum` was edited from 20 to 10 in his config. The sound-choice entries weren't in his config yet, so the game created them from these defaults. The preview opens with these picks too.

v0.18.0 added sound choices after William's feedback on v0.17. He didn't like any of the flicks, wanted more locks, and asked for the click from his image ranker:
- **Flick:** Thwoop (a resonant rising tone with air following its pitch), Reverse (air rushing in that cuts off into a thud), Yank (an elastic FM twang), Pop (a vacuum bloop with a ring), Bass (a sub punch with a knock) and RankerRise.
- **Lock:** Engage (a click, a servo whine and a seat tick), Bloop (two octave bloops), Tractor (a fifth and an octave swelling in with vibrato), Ping (a target-lock ping) and RankerLatch.
- **Select:** RankerClick and RankerChip.
- The checks were redone: the synthesized sounds match the preview (worst 0.003), the recordings match to 16-bit rounding, and the new ones' spectrograms look as designed.

v0.17.0 changed:
- **Defaults are William's preview picks:** glow 250%, glow in flight 60%, opacity 300%, grip response 100%, line flare 30%, pulses 2 m at 0.5 per metre, Additive glow, lock hum 20%, catch thump 80%, flight whoosh 70%, and the sounds Soft, TwoTone, Low, Fwip, Soft and Hum. His config file had all of these except line flare, which was set to 30% there.
- **Three new layered sounds of each kind** (see Feedback in Mechanic):
  - Select: Shimmer, Blip, Pluck
  - Lock: PowerUp, Clasp, Latch
  - Hum: Energy, Turbine, Plasma
  - Flick: Pull, Snap, Swoosh
  - Catch: Glove, Clunk, Magnet
  - Whoosh: Rush, Hiss, Aura
  - Spectrograms of all of them looked as designed. Two flaws they showed were fixed: Pluck was cut off while still ringing, and Glove clicked at the end of its dry part.
- The preview's hum fades after 3 seconds of Grab, and its looping sounds stop by themselves when the page stops drawing. William heard the old preview humming on.

v0.16.0 changed:
- **Grab assist no longer grabs "in the middle"** (the milk crate, `kitBash_medium_milkCrate_interactable`, like the cone before it):
  - In v0.15 every grab assist catch of the crate took its box grip `col_box_grip`. The game then put the hand 30 cm and 90° from the handle, the same spot each time, wherever it touched.
  - Handles (CylinderGrip main grips, like the crate's `gripObject` or a sword's) now go into the main grip's hold, like fixed grips.
  - Catches where it touches prefer a handle or fixed grip within 6 cm over a grip that can be held anywhere.
  - Grips that aren't fixed hover for 3 physics steps (`OnHandHoverUpdate`) before the confirm. If the grip's hold is still more than 8 cm from the hand, its target is set to where the hand is (`SetTargetInWorld`, offset by the grip's force pull hand transform).
  - `Target.ContactPoint` backs a palm that's already inside the object out along the object's motion.
  - The log has new lines: "Grab of X by Y: …" (whether the grip's own hold was kept) and "Catch check for X: the palm settled N cm from where the hand closed". Check these first.
- **Line flare** controls the line's start (see Mechanic).
- **Lock vibration** has its own setting, apart from the lock hum.
- **Sound choices**: three variations of each sound, also playable in the line preview.
- The line preview starts from William's in-game values (glow 250%, glow in flight 60%, opacity 300%, grip response 100%, pulses 2 m at 0.5 per metre, lock hum 20%, catch thump 80%, flight whoosh 70%, glow style Additive). It also has colour, glow style and sound controls.

What the v0.15.0 test showed (393 pulls, 177 grab assist catches, no errors):
- Guns were caught 0–2° and 0 cm from the hold.
- Additive glow works (`Hidden/Internal-Colored`). William settled on it.
- Hologram copied `hologram_Gacha` (`SLZ/Hologram Depth fade`). Its colour properties are `_HologramEdgeColor` and `_EmissionColor`, so the edge colour is now tinted too.
- William's avatars were 1.75–1.83 m tall.
- Everything pulled was 3 kg or less, so weight never applied.
- Avoid obstacles changed 14 arcs. 8 of those were "blocked, flying it anyway", often by `col_box` or numbered colliders, which may be the level's own boxes near the start or the end.
- William turned on sparks (25%) and the puff (25%). He set lock hum to 20% and asked for a separate vibration setting.

v0.15.0 added:
- **Sound and haptics** (new "Sound and haptics" page): a hum and buzz while locked that follow the squeeze (`LoopSound`, `Feedback.Buzz`), a catch thump and vibration (`Feedback.Catch`, from held catches, grab assist and ammo collected at a hand), a "fwip" at the hand on the flick, and a whoosh played from the flying object (`FlightSounds`). There's also a master volume.
- **Avoid obstacles** (Launch page, on): the arc is sphere-cast against the level (Default and Fixture layers) at launch. It tries 100/60/30/0% of the arc height, then 160/240%, and logs "arc lowered to …".
- **Weight** (Launch page, 100%): `Heaviness` is 0 up to 3 kg and 1 at 30 kg (log scale). Flight time is ×(1+0.6h), arc height ×1/(1+0.8h), and the catch lands harder.
- **Scale with avatar** (General page, off): distances × avatar height / 1.76 m (`BodyScale`). The log prints the avatar's height when it changes.
- **Effects page:**
  - Colour hue and saturation (`Palette`).
  - Glow style experiments (`GlowMaterials`): Additive uses `Hidden/Internal-Colored` with additive blending; Hologram copies a material using `SLZ/Hologram Depth fade` and logs its properties.
  - Sparks along the line and a lift-off puff (`Sparks`, a hand-made particle mesh). These are off by default.

v0.14.0 changed:
- **Grab assist by grip kind** (`src/GripKind.cs`):
  - Only fixed grips (exact `TargetGrip`, like gun handles) are moved into the stored hold.
  - Anything else is caught where it touches: the object slides straight to the palm, without turning, until its nearest surface meets it, and the hand hovers and confirms the grip nearest that point.
  - Flights still fit to any grip's default hold. William wants that kept: it lines the traffic cone up well for catching. Only the catch was wrong.
  - `FindGripNearest` measures to each grip's own colliders.
- **Selection stickiness** now counts from when the current target stopped being best, whatever beat it. Before, a new challenger restarted the wait, so sweeping across a row of weapons stayed stuck on the first one. A centring term (0.2 × the angle to the object's centre) breaks near-ties between neighbours. The lock log names the runner-up and the score gap, and says what makes a target a weapon.
- **Line flare** has its own size setting, off by default. Scaling it with the 280% thickness made a 50 cm blob (William).

What the v0.13.0 test showed:
- **Colour space is Linear**, so the dim colour fix was right.
- **Guns caught by grab assist:** 0–2° from the stored hold (MK18 4–8°). The main-grip fix works.
- **Traffic cone:** it counts as a weapon, and its main grip is `BoxCol`, a box grip. Grab assist moved it 22–75 cm into the box grip's default hold, inside the cone. Catching it by hand ended up 84–97° and 21–41 cm away from that hold.
- **Choosing between weapons:** William found it hard to pick one gun among several close together. He thought item priority was the cause, but weapons all get the same bonus, so it cancels out between them. The likely cause is the stickiness restart described above.

v0.13.0 changes:
- William's second set of preview values: thickness 280%, opacity 280%, pulse speed 3.6 m/s, pulse length 1.1 m, 1 pulse per metre. A pulse as long as the gap between pulses makes the line solid, so the pulse speed doesn't show with these values.
- The line follows the squeeze, shoots back into the object on the flick, and the glow fades out during the flight (see Mechanic).
- BoneMenu's Visuals page is split into Glow and Line pages.
- The preview (`tools/line-preview-widget.html`) has these too, plus a squeeze slider.

v0.12.0 applied the line look William picked in an interactive preview (see below):
- Line length 65%, glow intensity 160%, thickness 230%, opacity 180%, pulse speed 0.25 m/s (a tenth of the old 2.4 m/s). His saved config had 70% and 200%, so those two entries were edited in `UserData/MelonPreferences.cfg` too.
- **Dim colour fix:** the legacy particle shader computes `2 × vertex colour × _TintColor × texture`. Its default tint is sRGB grey 0.5, which linear colour space turns into 0.21, so everything drawn with it (line, flare, glow shells, debug lines) was at 43% of the intended brightness. Lines now set the tint with `SetVector(0.5)`, and the glow converts its colour to linear itself before `SetVector`, because `SetColor` would convert it. The startup log now prints the colour space (expected: Linear).
- **Pulses:** William found the old pulses too dot-like, so pulse length and pulse amount (per metre) are now settings. The beam texture is redrawn when they change (`GlowTextures.Beam(share)`, where share = length × amount). It tiles once per pulse.
- **The flare** at the object peaks at 30%. That came from misusing `Mathf.SmoothStep` (it blends between two values). William kept that look in the preview, so the dot texture now produces it on purpose.

v0.11.0 changes:
- **Grab assist and auto-grab:** weapons, and anything fitted to the hand in flight, are caught by their main grip and moved into exactly the hold the game gives that grip (the same `HoldPose` the flight fits to). The grip is only hovered once the object is in place. Other objects still take the nearest grip, held as `SolveHandTarget` says.
- **Glow:** copies skinned meshes, falls back to see-through meshes when nothing solid is left, no longer treats `Hologram Depth fade` as hidden, and logs per kind of object (instance numbers stripped) for up to 100 kinds.

What the v0.10.0 test showed:
- 142 grab assist catches, no errors.
- **Weird angles:** grab assist by the right grip still gave holds 25–47° and 2–6 cm off (SPAWN_GUN, handgun_1911, BalloonGun, Accelerator 2, portal gun). Catching by hand gave 0–1°, and so did v0.6.0, which confirmed from a distance without moving the object. So `SolveHandTarget`, asked right after the hover starts, returns a half-blended pose, not the grip's real hold.
- **Wrong grip:** the nearest grip was often the slide (`SlideGrip1` on Eder22, `SlideGrip` on 1911, 94–117° off), the charging handle (MK18, Constrainer), the mag grip, or the chainsaw's support handle.
- **Glow:** the 40-object log cap ran out at 02:44, before any modded weapons were tried, because every `[n]` instance counted separately. The chainsaw (Doom collection mod) has only skinned meshes. BoardGun's meshes all use `SLZ/Hologram Depth fade`, which the word "depth" filtered out.
- `TestProbeGod`'s grip puts the hand 20–30 m away, so it can't be fitted.

v0.10.0 aimed at the nearest point of each collider, made the line thicker at the object and hid it on the flick (the launch flash had made it flicker once), and stopped treating cut-out materials (render queue 2450) as see-through.

What the v0.9.0 test showed:
- 182 pulls, 45 grab assist catches, 1 ammo batch collected, no errors.
- Glow skips were all correct: weapons' see-through "Icon" quads, a hologram shader, TV damage decals.
- The broom lost its glow with no skip logged, so the cause is still unknown. Check the new "Glow on prop_broom" line.
- Flicks at targets steeply below the hand came in at 420–490°/s, under William's 500°/s setting.
- William still found grab assist weird sometimes, and found objects at his feet hard to aim at.

v0.9.0 made grab assist and auto-grab move the object into a natural hold before grabbing. v0.8.0 added ammo batches. v0.7.0 changes:
- **The line:** it now starts at the object and stops partway to the hand, appears only while locked by default, and the setting id is now `LineMode`.
- **The glow:** it skips meshes with hidden shaders, see-through meshes, and meshes much bigger than the object. It logs "Glow on X skipped ..." for the first 20 objects where it skipped something. It also has an intensity setting.
- **Selection:** a higher-ranked kind of object takes over at once, and more things count as ammo.

What the v0.6.0 test showed:
- Fit to hand is solved for fixed grips: handgun_M9 and rifle_PDRC checked at 0–1° and 0 cm.
- No errors, 188 pulls and 94 grab assist catches.
- William reported:
  - the line ran the wrong way (it should go from the object toward the hand)
  - the glow lit up a whole plane on some objects
  - debris near ammo sometimes stayed selected through the stickiness

v0.6.0 added:
- min mass for things without a grip
- item priority
- the object glow
- the Line mode and length settings, and less waver on the line
- a fix for the debug view's cone, which overflowed its point buffer and threw every frame, breaking the glove
- an inverted fit-to-hand offset

What the v0.5.0 test showed:
- 331 pulls, 67 grab assist catches.
- Weapon fit checks were 1–9° off once the force pull hand transform was inverted. Box and cylinder grips vary, because the hand can take them at any angle.
- Flicks at targets 59–71° below the hand now read 4–22° off, so the steep fix works.
- William had raised the flick speed to 500°/s and the tolerance to 90°.

v0.5.0 restyled the beam, and v0.4.0 added smoothed hand tracking with a stop distance, grab assist, reverse rotation, the aim settings, the debug view and the flick fix for targets below the hand. Both have since been tested.

What the v0.3.0 test showed:
- 362 pulls, no errors.
- Fit to hand failed on 351 of 352 tries, because `GetHandInTarget` returned world positions.
- 49 missed flicks were fast enough but 51–74° off. This came from the old "up and back" axis, which breaks down when the target is steeply below the hand.
- William found the hand tracking during the slowdown too sticky, and wanted to be able to move his hand out of the way at the end.

What the v0.2.0 test showed:
- 142 locks, 101 pulls, no errors.
- Wrist flicks peaked at about 330–600°/s, so the 300°/s default fits.
- The aim line fell back to `Legacy Shaders/Particles/Alpha Blended`, because the URP particle shader wasn't found at runtime.

What the v0.1.0 test showed:
- Selection and launching worked.
- The backward-pull flick felt wrong. On missed flicks the hand moved at 1.6–2.6 m/s but 55–84° off "toward the body", because a wrist flick mostly moves the hand upward.
- The old input read BONELAB's combined grip force, which needed grip and trigger together. Input now reads the finger curls: middle finger for grip, index for trigger.
- No errors in the log.

Implementation notes:
- The cone aims from an estimated shoulder through the palm, so it doesn't depend on the controller's tilt. Check that this feels right.
- BONELAB's built-in force pull is skipped for a hand while that glove has a target or an object in flight (`src/ForcePullPatches.cs`). LabFusion patches the same `ForcePullGrip.OnFarHandHoverUpdate`, and the two patches can run together.
- Grab assist (`src/GrabAssist.cs`) moves the object into place over 0.1 s, then starts the hover the way LabFusion does (`GripExtensions.TryAttach`) and confirms the grab in the same step (see Confirmed at once). Confirming straight from a distance gave holds that are impossible by hand, like inside the object (William, v0.6.0). LabFusion avoids it by passing the real grab's `targetInBase`.
  - **Weapons and fitted objects with a fixed main grip:** they move into the main grip's `HoldPose`.
  - **Everything else:** caught where it touches, by the grip nearest the contact point.
  - `SolveHandTarget`, asked right after the hover begins, returns a half-blended pose, so it's no longer used.
  - Grab assist measures its reach to the object's nearest surface point.
  - Until v0.26.0, grips that aren't fixed hovered for 3 physics steps first. With grip held, the game closed the hand itself during those steps, which counted as the catch (v0.18.2) but left the grab broken (see Confirmed at once).
- `Grip.Snatch(hand, isQuick)` (a `CoSnatch` coroutine) is how every grab with a grip target attaches: `OnGrabConfirm`, and the game's own hover with grip pressed, call it. Quick, it makes the joint at once: the hand's anchor at the handle (`GetHandleInHand`), the connected anchor at the grip's target (`GetTargetInHost`), and the rotation from `GetNeutralTargetInWorld` (see Which way round). Not quick, the skeleton hand first moves to the target (`SkeletonHand.StartMoveHandToTarget`).
- Fit to hand: every catch of a fitted object logs a "Fit check" comparing the real held pose with the prediction.
- The beam (`src/AimLine.cs`) is a soft orange glow line under a thin bright core, textured with a soft edge generated in `src/GlowTextures.cs`. Until v0.25 it had pulses of light scrolling along it, but William's pulse settings had joined them into a solid line. The only line shader BONELAB ships that `Shader.Find` can load is `Legacy Shaders/Particles/Alpha Blended`. There's no additive shader, so the glow comes from layering, not blending.
- **Line preview:** an interactive three.js widget in the chat redraws the line and glow with the mod's own numbers, in linear colour space. William uses it to pick visual values and sends them back with its "Use these values in the mod" button. Its source is `tools/line-preview-widget.html`, an HTML fragment for the chat's widget tool, not a standalone page. Keep it in sync when the line's look changes.
  - The widget's code is copied into the chat by hand, so repeats in the recordings' base64 are packed as `(pattern*count)` tokens, which `unpackRec` expands before decoding. Without that, a long run of `A`s was miscopied and RankerChip went silent. A chat copy may leave recordings out to stay small, and their buttons then hide. After showing it, check the shown copy against the file. The session transcript holds it.
- From the player's eye the line points almost straight at them (about 5° off the line of sight for an object 3 m away), so it's seen mostly end-on. The width at the object, the flare and the object glow carry most of the effect.
- BONELAB's own far hover only looks at the Interactable layer (mask 32768). The glove gathers from Dynamic and Interactable.
- **Game API notes, from the Il2Cpp assemblies:**
  - `MarrowEntity` has `Hide(isHidden, doApplyImmediately)` and `IsHidden`, part of an `InactiveStatus` with separate hide, cull and despawn flags. It also has `Teleport(position, rotation, doResetPose)`, `Ghost`, `Hibernate`, `EnableColliders`, `PreventDisableOnCull`, `_parasites` and `Pack`/`Unpack`.
  - Magazines attach to guns through `AlignPlug` (with a `ConfigurableJoint`) in a `Socket`, whose `LockedPlug` is the magazine's plug.
  - The game's own holsters (`InventorySlotReceiver`) only take objects with a `WeaponSlot`.
  - A hand's grip is `hand.AttachedReceiver` (a `HandReciever`, cast to `Grip`), and the hand joins the forearm with `hand._forearmJoint`.
  - BoneLib's `HelperMethods.SpawnCrate(barcode, …)` can respawn things by barcode, for keeping pocket items between levels.
  - `tools/ApiList` lists any game type's members with their types, from the metadata alone. In that folder, run `dotnet run -- "<BONELAB>/MelonLoader/Il2CppAssemblies/Il2CppSLZ.Marrow.dll" Il2CppSLZ.Marrow.Hand`. `?word` lists type names containing the word.
  - **Reading the game's code** (how v0.28.1 was found): MelonLoader's Cpp2IL can dump every method's disassembly. Run `"<BONELAB>/MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/Cpp2IL.exe" --game-path "<BONELAB>" --exe-name BONELAB_Steam_Windows64 --output-as isil --output-to <scratch folder>` (about 40 s, one text file per type, e.g. `IsilDump/SLZ.Marrow/SLZ/Marrow/CylinderGrip.txt`).
    - `tools/GameCode` reads it: `dotnet run -- method "<cpp2il_out>" <dump file> "<part of the signature>"` prints a method's disassembly with each call named, and `dotnet run -- fields "<cpp2il_out>/SLZ.Marrow.dll" SLZ.Marrow.HandToGripState` prints field offsets (e.g. `isZFlipped` at 0x76). `<cpp2il_out>` is `MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out`, whose DLLs carry every method's address and field's offset.
    - Virtual calls show as `call [reg+N]`: the slot is (N − 0x138) / 16. `HandReciever.ValidateGripScore` is slot 8 (0x1B8). Grip's three pool interface methods take slots 19–21, then its own virtuals follow in declaration order: `GetNeutralTargetInWorld` 24 (0x2B8), `GetPrimaryAxis` 27 (0x2E8), `GetSecondaryAxis` 28, `CancelSnatch` 38 (0x398), `Snatch` 39 (0x3A8).
    - Structs over 8 bytes come back through a hidden first argument (`rcx`), so `this` is then `rdx`. `SimpleTransform` is 28 bytes: position, then rotation at +0xC.
- `dotnet build` builds the mod and copies it into the game's `Mods` folder. Quit the game first, because it locks the DLL.
- Locks, launches and missed flicks are logged to `MelonLoader/Latest.log` to help with tuning. A missed flick logs the fastest flick in °/s.
