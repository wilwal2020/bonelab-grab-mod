using System;
using System.Collections.Generic;
using BoneLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Utilities;
using UnityEngine;

namespace GravityGloves
{
    internal readonly struct LaunchResult
    {
        public LaunchResult(float distance, float flightTime, float speed, string rotation, string notes)
        {
            Distance = distance;
            FlightTime = flightTime;
            Speed = speed;
            Rotation = rotation;
            Notes = notes;
        }

        public float Distance { get; }
        public float FlightTime { get; }
        public float Speed { get; }
        public string Rotation { get; }

        // What weight and obstacles changed about the flight, for the log, starting with ", ". Empty if nothing.
        public string Notes { get; }
    }

    // Objects in the air after a flick. Every physics step this steers the object along its arc toward the
    // hand, slows it down near the end, brakes it to float at the hand, turns it to fit the hand, and handles
    // grab assist.
    internal static class Flights
    {
        // Keep a flight around for a little while after it should have arrived, for grab assist.
        private const float GracePeriod = 0.75f;

        // An object further than this from its path has been knocked off course, so let it go.
        private const float MaxPathError = 0.5f;

        // Hand tracking smooths the hand position over this time at launch, shrinking to the near value by
        // arrival, so quick hand movements early on are ignored and late corrections are accurate.
        private const float FarSmoothing = 0.3f;
        private const float NearSmoothing = 0.03f;

        // Let the flick finish before a following object starts copying the hand.
        private const float FollowDelay = 0.12f;

        // How quickly a following object catches up with the hand's rotation.
        private const float FollowResponse = 0.1f;

        private const float MinTurnTime = 0.1f;
        private const float MaxTurnSpeed = 15f;

        // How long after a catch to compare the real held pose with the predicted one.
        private const float FitCheckDelay = 0.4f;

        private const int PathPoints = 24;

        // Ammo is collected the moment a hand touches it and just disappears. If that happens this close to a
        // hand, it was caught.
        private const float CollectReach = 0.35f;

        // The float at the hand (Settings.FloatTime), as in Half-Life: Alyx. In the second half of its flight, once
        // the object is close enough to the end of its path to stop there within the float brake time, it brakes
        // to a stop there without gravity. Then it floats for the float time while gravity fades back in over
        // FloatFade. Braking is capped, so only arrivals up to the float stop speed come to a full stop. Faster
        // ones, pulled from further away, carry on past and leave the float once they're out of its zone.
        private const float MinFloatProgress = 0.5f;
        private const float FloatFade = 0.2f;

        // An arrival this much faster than the float stop speed still settles at the float point. How quickly it
        // settles there (per second, critically damped).
        private const float SettleMargin = 1.25f;
        private const float SettleRate = 10f;

        // How smoothly the float point follows the hand, so a quick jerk of the hand isn't copied exactly.
        private const float FloatFollowSmoothing = 0.1f;

        // How quickly leftover motion, and the spin of anything not turned to the hand, die down while it floats.
        private const float FloatDamping = 0.1f;
        private const float FloatSpinSettle = 0.25f;

        // Slower than this, a braking object has stopped. Faster than PassedSpeed at its stop, it went past.
        private const float StoppedSpeed = 0.1f;
        private const float PassedSpeed = 0.5f;

        // A braking object that hasn't stopped by then, pinned against something, floats where it is. Otherwise
        // braking ends a little after it should have stopped: this share of the planned braking time, plus a bit.
        private const float MaxBrakeTime = 1f;
        private const float BrakeTimeMargin = 1.2f;
        private const float BrakeTimeSlack = 0.05f;

        // A guided flight is steered back onto its path at most this fast on top of the path's own speed. Steering
        // it back at once pushed a blocked object into what blocked it at up to 45 m/s, until it was 0.5 m off its
        // path. In the v0.26.1 test the DevManipulator, blocked 20 m out, ended up 4 km below the level.
        private const float MaxCatchUpSpeed = 4f;

        // The game's own hover sphere, which finds what a hand can grab (Hand.UpdateHovering): HoverSphereRadius
        // around a point that radius times this out along the palm. An object whose centre is further than the
        // radius plus MaxReachTickSize can't be in it, so its surface isn't measured.
        private const float HoverSphereOut = 0.8f;
        private const float MaxReachTickSize = 1.5f;
        private static bool _loggedHoverSphere;

        // The float zone is never smaller than this, so a nudge doesn't end the float.
        private const float MinFloatZone = 0.25f;

        // Static level geometry, which the arc is checked against: walls, floors, door frames.
        private const int WorldMask = (int)(GameLayers.DEFAULT | GameLayers.FIXTURE);

        // Arc heights to try, as shares of the planned one, when something's in the way: lower first for
        // ceilings and door frames, then higher for low walls and railings.
        private static readonly float[] ArcTries = { 1f, 0.6f, 0.3f, 0f, 1.6f, 2.4f };

        // How finely the arc is checked, and how close to the hand obstacles stop counting, since the object
        // is being caught there anyway.
        private const float CheckStep = 0.3f;
        private const float HandClearance = 0.3f;

        private enum FloatPhase
        {
            None,
            Braking,
            Floating,
            Done,
        }

        private sealed class Flight
        {
            public Target Target;
            public GloveHand Glove;
            public float StartTime;
            public float Duration;

            // Where it arrives relative to the palm. Batches are spread out a little.
            public Vector3 ArrivalOffset;

            // The planned arc: start, launch velocity, the gravity it was solved with, and where it ends.
            public Vector3 Start;
            public Vector3 LaunchVelocity;
            public Vector3 PathGravity;
            public Vector3 Destination;

            // Real gravity, and the extra acceleration that bends a free flight into the planned arc.
            public Vector3 Gravity;
            public Vector3 ExtraGravity;

            // A guided flight is moved along the arc step by step, so its end can track the hand and its
            // speed can drop. Released means it's coasting on its own again.
            //
            // It's moved in the player's frame: the arc, and where it ends, move with the player's own movement
            // (the tracking space, which locomotion moves), all of it and at once. Hand tracking and its smoothing
            // only follow the hand's movement relative to the player. Origin is where the player was at the flick.
            public bool Guided;
            public Vector3 Origin;
            public bool Released;
            public float PathTime;
            public Vector3 TrackedEnd;

            // Rotation: fit to the hand, or copy the hand's turning by this fraction.
            public HoldPose Hold;
            public float Follow;
            public bool Following;
            public Quaternion HandReference;
            public Quaternion ObjectReference;

            public LineRenderer PathLine;

            // Where it was last step, to tell whether ammo that vanished did so at a hand.
            public Vector3 LastCenter;

            // Caught by a hand, so its whoosh stops at once.
            public bool Caught;

            // The hand has had its tick for the object coming within reach.
            public bool ReachTicked;

            // When the flight last let go of the object: the end of its path, or of its float. Negative while it's
            // still moving it. The object can be pulled again "pull again after" that.
            public float LetGoAt = -1f;

            // For the log line saying how far from the hand the flight ended: the palm at the flick, why steering
            // stopped early (null if it reached the end of its path), and whether that's been noted. With the float
            // on, the line waits in ArrivalNote until the flight ends, and is dropped if the object floats, since
            // the float logs its own.
            public Vector3 LaunchPalm;
            public string EndReason;
            public bool EndLogged;
            public string ArrivalNote;

            // The float at the hand: braking toward FloatPoint at FloatBrake (m/s^2), then floating, both since
            // PhaseStart. For the log: its speed when it started braking, and its speed and distance from the hand
            // when it stopped or went past. FloatEnd is when the float ended, so the flight stays around a little
            // after it for grab assist.
            public FloatPhase Float;
            public Vector3 FloatPoint;

            // Where the stop was when the float started, and where the hand's arrival point was then, both relative
            // to the player. The stop moves with the player, and follows the hand's movement relative to the player
            // since then by the float follow share, smoothed. FloatOffset is where it is now, relative to the player.
            public Vector3 FloatBase;
            public Vector3 FloatAnchor;
            public Vector3 FloatOffset;

            // The float point's velocity that was added to the object last step. Taking it back off leaves the
            // object's own motion relative to the point, so the point's moves are copied at once instead of
            // being caught up with through the damping.
            public Vector3 FloatCarry;

            // Came in slow enough to stop, so if it slips past the float point it's pulled back to settle there.
            public bool Settles;
            public float FloatBrake;
            public float PhaseStart;
            public float BrakeUntil;
            public float BrakeSpeed;
            public float ArriveSpeed;
            public float ArriveDistance;
            public float FloatEnd;

            public Vector3 PathPoint(float time) =>
                Start + LaunchVelocity * time + 0.5f * time * time * PathGravity;
        }

        private sealed class FitCheck
        {
            public Hand Hand;
            public Target Target;
            public HoldPose Hold;
            public float Time;
        }

        private static readonly List<Flight> Active = new List<Flight>();

        // Objects whose flight ended without a catch before "pull again after" had passed, and when they can be pulled
        // again. A flight is only kept GracePeriod past its arrival, and the setting can be longer.
        private static readonly Dictionary<Target, float> Resting = new Dictionary<Target, float>();
        private static readonly List<Target> Rested = new List<Target>();
        private static readonly List<FitCheck> FitChecks = new List<FitCheck>();
        private static Il2CppStructArray<Vector3> _pathBuffer;
        private static readonly Il2CppStructArray<RaycastHit> Hits = new Il2CppStructArray<RaycastHit>(16);

        private static float SlowdownAmount => Settings.SlowdownAmount.Value / 100f;
        private static float SlowdownStart => Settings.SlowdownStart.Value / 100f;
        private static float Tracking => Settings.HandTracking.Value / 100f;
        private static float FloatTime => Settings.FloatTime.Value;
        private static float FloatBrakeTime => Settings.FloatBrakeTime.Value;

        // The hardest braking, which stops an arrival at the float stop speed within the float brake time, and how
        // far out such an arrival starts braking.
        private static float MaxFloatBrake => Settings.FloatStopSpeed.Value / FloatBrakeTime;
        private static float FloatZone => Settings.FloatStopSpeed.Value * FloatBrakeTime / 2f;

        // Main is false for the rest of an ammo batch, which fly along quietly behind the one that was aimed at.
        public static LaunchResult Launch(Target target, GloveHand glove, Hand hand, Vector3 palm,
            Vector3 arrivalOffset = default, float extraTime = 0f, bool main = true)
        {
            // Everything is turned to fit the hand if its grip allows. Since v0.25 there's no setting for it: William
            // had it on for everything.
            HoldPose hold = HoldPose.TryCreate(target, hand, out string problem);
            string rotation = hold != null ? "fitted to hand" : $"couldn't fit to hand: {problem}";
            if (hold == null && Settings.FollowHand.Value > 0)
                rotation += $", follows hand {Settings.FollowHand.Value}%";

            // A fitted object aims for where it would sit in the hand, anything else for the palm.
            Vector3 start = target.CenterOfMass;
            Vector3 arrival = hold != null ? hold.HeldCenter(hand.transform) : palm + arrivalOffset;
            float distance = Vector3.Distance(start, arrival);

            // Heavy objects take longer, and arc lower so the longer flight doesn't lob them up high. Distances
            // count in avatar-sized metres.
            float heaviness = Heaviness.Of(target.Mass);
            float timeScale = Heaviness.TimeScale(heaviness);
            float baseTime = Settings.FlightTime.Value + Settings.ExtraTimePerMetre.Value / 1000f * distance / BodyScale.Value;
            float flightTime = baseTime * timeScale + extraTime;

            // If the player is moving, aim where the hand will be when the object arrives. A guided flight moves
            // with the player instead, so it doesn't need to guess.
            bool guided = Tracking > 0f || SlowdownAmount > 0f;
            Vector3 playerVelocity = glove.PlayerVelocity;
            Vector3 destination = guided
                ? arrival
                : arrival + playerVelocity * (flightTime * Settings.HandPrediction.Value / 100f);

            // Solving with scaled gravity, and adding the difference during the flight, changes the
            // height of the arc without changing the flight time. Dividing by the time scale squared keeps a
            // heavy object's arc from growing with its longer flight.
            Vector3 gravity = target.UsesGravity ? Physics.gravity : Vector3.zero;
            float arc = Settings.ArcHeight.Value * Heaviness.ArcScale(heaviness) / (timeScale * timeScale);
            string arcNote = null;
            if (Settings.AvoidObstacles.Value)
                arc = ClearArc(target, start, destination, gravity, arc, flightTime, out arcNote);
            Vector3 pathGravity = gravity * arc;
            Vector3 velocity = Ballistics.LaunchVelocity(start, destination, pathGravity, flightTime);
            // It keeps any spin it had. Turning to fit the hand takes over the rotation anyway.
            target.SetVelocity(guided ? velocity + playerVelocity : velocity, false);
            target.SetFastCollision(true);

            RemoveFlightOf(target);
            Resting.Remove(target);
            Active.Add(new Flight
            {
                Target = target,
                Glove = glove,
                StartTime = Time.time,
                Duration = flightTime,
                ArrivalOffset = hold != null ? Vector3.zero : arrivalOffset,
                Start = start,
                LaunchVelocity = velocity,
                PathGravity = pathGravity,
                Destination = destination,
                Gravity = gravity,
                ExtraGravity = pathGravity - gravity,
                Guided = guided,
                Origin = PlayerOrigin(),
                TrackedEnd = destination,
                Hold = hold,
                Follow = hold == null ? Settings.FollowHand.Value / 100f : 0f,
                LastCenter = start,
                LaunchPalm = palm,
            });
            if (main)
                FlightSounds.Start(target);

            string notes = "";
            if (heaviness > 0.005f)
                notes += $", weight {target.Mass:0} kg: {timeScale:0.00}x the time, arc {Heaviness.ArcScale(heaviness):0%} as high";
            if (arcNote != null)
                notes += $", {arcNote}";
            return new LaunchResult(distance, flightTime, velocity.magnitude, rotation, notes);
        }

        public static bool IsFlying(Target target) => FindFlightOf(target) != null;

        // Whether an object can be selected and pulled: not while it's flying, and not until "pull again after" has
        // passed since its flight let go of it at the hand. Until v0.34 that was whenever the flight ended, kept
        // GracePeriod (0.75 s) past its planned arrival or its float for grab assist, and William asked to choose it.
        public static bool CanPull(Target target)
        {
            Flight flight = FindFlightOf(target);
            if (flight != null)
                return flight.LetGoAt >= 0f && !InFloat(flight) && Time.time >= flight.LetGoAt + Settings.PullAgainAfter.Value;
            if (!Resting.TryGetValue(target, out float until))
                return true;
            if (Time.time < until)
                return false;
            Resting.Remove(target);
            return true;
        }

        public static bool HasFlightFrom(GloveHand glove)
        {
            foreach (Flight flight in Active)
            {
                if (flight.Glove == glove)
                    return true;
            }
            return false;
        }

        // The objects this glove pulled that still glow, most recent first, and how strongly the glow shows.
        // It fades out gradually over the flight and is gone by the "glow in flight" point.
        public static float AirborneGlowFrom(GloveHand glove, List<Target> targets)
        {
            targets.Clear();
            float end = Settings.GlowInFlight.Value / 100f;
            float strongest = 0f;
            if (end <= 0f)
                return strongest;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Flight flight = Active[i];
                if (flight.Glove != glove || !flight.Target.IsAlive)
                    continue;
                float glow = 1f - Smooth(Progress(flight) / end);
                if (glow <= 0f)
                    continue;
                targets.Add(flight.Target);
                strongest = Mathf.Max(strongest, glow);
            }
            return strongest;
        }

        // Called when a hand starts a grab: grabs the nearest flying object within reach of the hand. Reach is
        // measured to the nearest part of the object, so the near end of something long counts.
        public static bool TryAssistGrab(Hand hand, float reach, out string caught)
        {
            caught = null;
            Vector3 palm = GloveHand.PalmPosition(hand);
            Flight nearest = null;
            float nearestDistance = reach;
            foreach (Flight flight in Active)
            {
                if (!flight.Target.IsAlive || !flight.Target.IsActive)
                    continue;
                float distance = Vector3.Distance(flight.Target.SurfacePointToward(palm), palm);
                if (distance <= nearestDistance)
                {
                    nearest = flight;
                    nearestDistance = distance;
                }
            }
            if (nearest == null || !GrabAssist.Start(nearest.Target, hand, UsesMainGrip(nearest), out string grab))
                return false;

            caught = $"{nearest.Target.Name} from {nearestDistance * 100f:0} cm away {grab}";
            LogCatchInFloat(nearest, "was caught by grab assist");
            QueueFitCheck(nearest, hand);
            FlightSounds.Stop(nearest.Target, true);
            RemoveFlightOf(nearest.Target);
            return true;
        }

        public static void Clear()
        {
            foreach (Flight flight in Active)
            {
                LineFactory.Destroy(flight.PathLine);
                flight.Target.SetFastCollision(false);
            }
            Active.Clear();
            FitChecks.Clear();
            Resting.Clear();
        }

        public static void FixedUpdate()
        {
            if (FitChecks.Count > 0)
                RunFitChecks();
            if (Active.Count == 0)
                return;

            HeldObjects held = HeldObjects.Read();
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Flight flight = Active[i];
                if (!Step(flight, held))
                {
                    Rest(flight);
                    LogArrivalNote(flight);
                    FlightSounds.Stop(flight.Target, flight.Caught);
                    LineFactory.Destroy(flight.PathLine);
                    flight.Target.SetFastCollision(false);
                    Active.RemoveAt(i);
                }
            }
        }

        // A flight that ended without a catch: its object waits out the rest of "pull again after", if any is left.
        private static void Rest(Flight flight)
        {
            float now = Time.time;
            Rested.Clear();
            foreach (var pair in Resting)
            {
                if (now >= pair.Value)
                    Rested.Add(pair.Key);
            }
            foreach (Target done in Rested)
                Resting.Remove(done);

            if (flight.Caught || !flight.Target.IsAlive)
                return;
            float until = (flight.LetGoAt >= 0f ? flight.LetGoAt : now) + Settings.PullAgainAfter.Value;
            if (until > now)
                Resting[flight.Target] = until;
        }

        // Weapons are caught by their main grip, so a gun lands by its handle and not its slide. So is anything
        // that was turned to fit the hand on the way, since that's the grip it was turned for.
        private static bool UsesMainGrip(Flight flight) => flight.Hold != null || flight.Target.IsWeapon;

        // Returns false once the flight is over.
        private static bool Step(Flight flight, HeldObjects held)
        {
            Target target = flight.Target;
            Hand hand = flight.Glove.Hand;
            if (!target.IsAlive || !target.IsActive)
            {
                CheckCollected(flight);
                if (flight.Caught)
                    LogCatchInFloat(flight, "was collected");
                return false;
            }
            if (target.IsKinematic || hand == null)
                return false;
            if (held.Contains(target))
            {
                flight.Caught = true;
                LogCatchInFloat(flight, "was caught");
                Feedback.Catch(held.HandHolding(target), target);
                QueueFitCheck(flight, hand);
                return false;
            }

            // Kept a little after it should have arrived, or after its float, for grab assist.
            float elapsed = Time.time - flight.StartTime;
            float plannedEnd = flight.Guided ? flight.Duration / Mathf.Max(1f - SlowdownAmount, 0.1f) : flight.Duration;
            if (!InFloat(flight) && elapsed > plannedEnd + GracePeriod && Time.time > flight.FloatEnd + GracePeriod)
                return false;

            Transform handTransform = hand.transform;
            Vector3 arrival = flight.Hold != null
                ? flight.Hold.HeldCenter(handTransform)
                : GloveHand.PalmPosition(hand) + flight.ArrivalOffset;
            Vector3 center = target.CenterOfMass;
            flight.LastCenter = center;
            float distance = Vector3.Distance(arrival, center);
            if (!flight.ReachTicked)
                CheckInReach(flight, hand, center);

            if (InFloat(flight) || TryStartFloat(flight, center, arrival))
            {
                DrawPath(flight, -1f);
                StepFloat(flight, handTransform, elapsed, center, arrival);
                return true;
            }

            // Once it has arrived, the object coasts on its own. The flight stays around for grab assist.
            bool steering = !flight.Released && (flight.Guided ? flight.PathTime < flight.Duration : elapsed < flight.Duration);
            DrawPath(flight, steering ? (flight.Guided ? flight.PathTime : elapsed) : -1f);
            if (!steering)
            {
                if (flight.LetGoAt < 0f)
                    flight.LetGoAt = Time.time;
                if (!flight.EndLogged)
                    NoteArrival(flight, hand, distance);
                return true;
            }

            Vector3 velocity = Vector3.zero;
            bool guiding = flight.Guided && TryGuide(flight, arrival, center, distance, out velocity);
            bool turning = TryTurn(flight, handTransform, elapsed, out Vector3 turnRate);
            if (guiding || turning)
                target.SetMotion(guiding ? velocity : target.Velocity, turning ? turnRate : (Vector3?)null);

            // Guided objects get their velocity set every step, so gravity is cancelled to follow the path exactly.
            if (guiding)
                target.AddAcceleration(-flight.Gravity);
            else if (!flight.Guided && flight.ExtraGravity != Vector3.zero)
                target.AddAcceleration(flight.ExtraGravity);
            return true;
        }

        // Moves the object one step along its arc. After the slowdown point it moves along the arc more slowly,
        // and the end of the arc drifts toward a smoothed hand position, more firmly as the object gets closer.
        private static bool TryGuide(Flight flight, Vector3 arrival, Vector3 center, float distance, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (distance <= Settings.TrackingStop.Value * BodyScale.Value)
            {
                // Close enough: stop steering, so the player can still move their hand out of the way.
                flight.Released = true;
                flight.EndReason = "stopped steering inside the tracking stop";
                return false;
            }

            float dt = Time.fixedDeltaTime;
            float progress = flight.PathTime / flight.Duration;
            float slowing = Smooth((progress - SlowdownStart) / Mathf.Max(1f - SlowdownStart, 0.01f));
            float rate = 1f - SlowdownAmount * slowing;
            float nextTime = Mathf.Min(flight.PathTime + rate * dt, flight.Duration);

            float tracking = Tracking;
            if (tracking > 0f)
            {
                // The hand, where it would be had the player stayed where they were at the flick. The player's own
                // movement is followed in full by the path itself (see PathPointWithTracking), so it isn't smoothed:
                // until v0.33 it was, along with the hand's, and while walking or strafing the smoothing lagged up
                // to 40 cm behind (William: the tracking "would stop quite quickly and not go to my hand").
                Vector3 hand = arrival - PlayerShift(flight);
                float smoothing = Mathf.Lerp(FarSmoothing, NearSmoothing, Smooth(progress));
                flight.TrackedEnd += (hand - flight.TrackedEnd) * (1f - Mathf.Exp(-dt / smoothing));
            }

            // The path's own step, moving with the player, plus a capped pull back onto it.
            Vector3 current = PathPointWithTracking(flight, flight.PathTime);
            Vector3 next = PathPointWithTracking(flight, nextTime);
            Vector3 offPath = current - center;
            if (offPath.magnitude > MaxPathError)
            {
                flight.Released = true;
                flight.EndReason = $"was knocked {offPath.magnitude * 100f:0} cm off its path";
                return false;
            }
            flight.PathTime = nextTime;
            velocity = (next - current) / dt + flight.Glove.PlayerVelocity + Vector3.ClampMagnitude(offPath / dt, MaxCatchUpSpeed);
            return true;
        }

        // Where the flight left the object, compared with where the hand is. Below 100% hand tracking, only that
        // share of the hand's movement since the flick is followed, so the rest is how far short it lands.
        private static void NoteArrival(Flight flight, Hand hand, float distance)
        {
            flight.EndLogged = true;
            string reason = flight.EndReason ?? (flight.Guided ? "reached the end of its path" : "finished its arc");
            float moved = Vector3.Distance(GloveHand.PalmPosition(hand), flight.LaunchPalm);
            string line = $"{flight.Target.Name} {reason} {distance * 100f:0} cm from the hand, which moved " +
                          $"{moved * 100f:0} cm since the flick{PlayerMoved(flight)} (hand tracking {Settings.HandTracking.Value}%)";
            if (FloatTime > 0f)
                flight.ArrivalNote = line;
            else
                Mod.Trace(line);
        }

        // Logs the arrival line of a flight that ended without floating.
        private static void LogArrivalNote(Flight flight)
        {
            if (flight.ArrivalNote != null && flight.Float == FloatPhase.None)
                Mod.Trace(flight.ArrivalNote);
            flight.ArrivalNote = null;
        }

        private static bool InFloat(Flight flight) =>
            flight.Float == FloatPhase.Braking || flight.Float == FloatPhase.Floating;

        // Starts braking for the float in the second half of the flight, once the object is close enough to its
        // stop to brake within the float brake time. Something slow near its stop that isn't getting any closer,
        // like after bumping the hand, floats where it is. Something moving away from its stop at speed carries on
        // with its flight instead: in v0.21 those floated at once and "went past" at 1-1.7 m/s. The float takes
        // over from the path.
        private static bool TryStartFloat(Flight flight, Vector3 center, Vector3 arrival)
        {
            if (flight.Float != FloatPhase.None || FloatTime <= 0f || Progress(flight) < MinFloatProgress)
                return false;
            Vector3 point = StopPoint(flight, center);
            Vector3 toPoint = point - center;
            float distance = toPoint.magnitude;
            // Relative to the player, since the float point moves with them.
            Vector3 playerVelocity = flight.Glove.PlayerVelocity;
            Vector3 velocity = flight.Target.Velocity - playerVelocity;
            float speed = velocity.magnitude;
            bool approaching = speed > StoppedSpeed && Vector3.Dot(velocity, toPoint) > 0f;
            bool start = approaching
                ? distance <= Mathf.Min(speed * FloatBrakeTime / 2f, FloatZone)
                : speed <= PassedSpeed && distance <= Mathf.Max(FloatZone, MinFloatZone);
            if (!start)
                return false;

            flight.Float = FloatPhase.Braking;
            flight.LetGoAt = -1f;
            // Slow from here on, and near the hand, where speculative contacts could bump it off the hand.
            flight.Target.SetFastCollision(false);
            Vector3 origin = PlayerOrigin();
            flight.FloatPoint = point;
            flight.FloatBase = point - origin;
            flight.FloatAnchor = arrival - origin;
            flight.FloatOffset = flight.FloatBase;
            flight.FloatCarry = playerVelocity;
            flight.FloatBrake = Mathf.Min(speed * speed / (2f * Mathf.Max(distance, 0.01f)), MaxFloatBrake);
            flight.PhaseStart = Time.time;
            // Braking at an even rate to the point takes twice the distance over the speed. It ends by then, with a
            // little to spare, even if the point moved on with the hand: in the v0.26.1 test, braking toward a
            // point that kept following the hand lasted up to the 1 s limit, so some objects hovered over a second
            // past their arrival and others under 0.2 s (William: "some objects seemed to hover for longer").
            float plannedBrake = approaching ? 2f * distance / Mathf.Max(speed, StoppedSpeed) : 0f;
            flight.BrakeUntil = Time.time + Mathf.Min(plannedBrake * BrakeTimeMargin + BrakeTimeSlack, MaxBrakeTime);
            flight.BrakeSpeed = speed;
            flight.Settles = speed <= Settings.FloatStopSpeed.Value * SettleMargin;
            flight.Released = true;
            // The float logs its own line instead of the arrival line.
            flight.EndLogged = true;
            return true;
        }

        // Where a floating object stops: the end of its path. Objects that aren't fitted to the hand stop where their
        // near side reaches it, the way grab assist catches them, instead of with their middle in the palm.
        private static Vector3 StopPoint(Flight flight, Vector3 center)
        {
            Vector3 end = PathPointWithTracking(flight, flight.Duration);
            Vector3 toEnd = end - center;
            float distance = toEnd.magnitude;
            if (flight.Hold != null || distance < 1e-3f)
                return end;
            Vector3 direction = toEnd / distance;
            float depth = Vector3.Dot(flight.Target.SurfacePointToward(end) - center, direction);
            return end - direction * Mathf.Clamp(depth, 0f, distance);
        }

        // Brakes the object to a stop at its float point, then lets it float there without gravity while its
        // leftover motion dies down, and fades gravity back in. Too fast to stop, it goes past and leaves the float
        // once it's out of the float zone.
        //
        // The float point follows the hand: it moves by the float follow share of how far the hand's arrival point
        // has moved since the float started, smoothed. The braking and floating work relative to it, so a still
        // float point gives a still float. In v0.21 the point stayed put, and with the hand still rising after the
        // flick, objects stopped 17-37 cm short even at 100% hand tracking (William).
        private static void StepFloat(Flight flight, Transform hand, float elapsed, Vector3 center, Vector3 arrival)
        {
            Target target = flight.Target;
            float dt = Time.fixedDeltaTime;
            float maxChange = MaxFloatBrake * dt;

            // The player's own movement is copied in full and at once; the hand's movement relative to the player by
            // the float follow share, smoothed. Until v0.33 the share and the smoothing applied to all of it, so while
            // walking away the float point kept up at half the player's speed, and objects pulled from close by
            // stopped 50-99 cm short and fell (William).
            Vector3 origin = PlayerOrigin();
            Vector3 followed = flight.FloatBase + (arrival - origin - flight.FloatAnchor) * (Settings.FloatFollow.Value / 100f);
            Vector3 lastOffset = flight.FloatOffset;
            flight.FloatOffset += (followed - flight.FloatOffset) * (1f - Mathf.Exp(-dt / FloatFollowSmoothing));
            flight.FloatPoint = origin + flight.FloatOffset;
            Vector3 playerVelocity = flight.Glove.PlayerVelocity;
            Vector3 followVelocity = (flight.FloatOffset - lastOffset) / dt;

            Vector3 velocity = target.Velocity - flight.FloatCarry;
            Vector3 toPoint = flight.FloatPoint - center;
            float distance = toPoint.magnitude;

            if (flight.Float == FloatPhase.Braking)
            {
                float along = distance > 1e-4f ? Vector3.Dot(velocity, toPoint) / distance : 0f;
                if (along > 0f && velocity.magnitude > StoppedSpeed && Time.time < flight.BrakeUntil)
                {
                    // Brake on the way in to stop at the point, and lose any sideways drift so it heads straight
                    // for it. The braking is capped, so something too fast only slows down.
                    Vector3 direction = toPoint / distance;
                    Vector3 sideways = velocity - direction * along;
                    float wanted = Mathf.Min(Mathf.Sqrt(2f * flight.FloatBrake * distance), along);
                    velocity = direction * Mathf.Max(wanted, along - maxChange) +
                               Vector3.MoveTowards(sideways, Vector3.zero, maxChange);
                }
                else
                {
                    flight.Float = FloatPhase.Floating;
                    flight.PhaseStart = Time.time;
                    flight.ArriveSpeed = velocity.magnitude;
                    flight.ArriveDistance = flight.Hold != null
                        ? Vector3.Distance(center, arrival)
                        : Vector3.Distance(target.SurfacePointToward(arrival), arrival);
                }
            }

            float hold = 1f;
            if (flight.Float == FloatPhase.Floating)
            {
                float floated = Time.time - flight.PhaseStart;
                hold = 1f - Mathf.Clamp01((floated - FloatTime) / FloatFade);
                if (hold <= 0f || distance > Mathf.Max(FloatZone, MinFloatZone) * (flight.Settles ? 2f : 1f))
                {
                    EndFloat(flight, hold <= 0f ? $"floated {FloatTime:0.00} s before it fell" : $"left the float after {floated:0.00} s");
                    return;
                }
                float speed = velocity.magnitude;
                if (flight.Settles)
                {
                    // Pulled back to the float point and critically damped, so something that slipped past it, like
                    // when the hand rose into an object coming down at it, settles in front of the hand instead of
                    // drifting off. In v0.22, 65 of 137 floats went past at nearly full speed and left the float.
                    Vector3 pull = toPoint * (SettleRate * SettleRate) - velocity * (2f * SettleRate);
                    velocity += Vector3.ClampMagnitude(pull, MaxFloatBrake) * (dt * hold);
                }
                else if (speed > 1e-4f)
                {
                    velocity *= 1f - Mathf.Min(1f - Mathf.Exp(-dt / FloatDamping), maxChange / speed) * hold;
                }
            }

            // Anything turned to fit the hand, or following its turning, keeps doing so. Anything else stops spinning.
            Vector3 spin = TryTurn(flight, hand, elapsed, out Vector3 turnRate)
                ? turnRate
                : target.MainBody.angularVelocity * Mathf.Exp(-dt * hold / FloatSpinSettle);
            // Back from moving with the float point to the world. It lets go of following the hand as gravity fades
            // back in, but keeps moving with the player, as momentum it falls with.
            flight.FloatCarry = playerVelocity + followVelocity * hold;
            target.SetMotion(velocity + flight.FloatCarry, spin);
            target.AddAcceleration(-flight.Gravity * hold);
        }

        private static void EndFloat(Flight flight, string ending)
        {
            LogFloat(flight, ending);
            flight.Float = FloatPhase.Done;
            flight.FloatEnd = Time.time;
            flight.LetGoAt = Time.time;
            flight.Target.WakeUp();
            FlightSounds.Stop(flight.Target, false);
        }

        private static void LogCatchInFloat(Flight flight, string how)
        {
            if (flight.Float == FloatPhase.Floating)
                LogFloat(flight, $"{how} after floating {Time.time - flight.PhaseStart:0.00} s");
            else if (flight.Float == FloatPhase.Braking)
                LogFloat(flight, $"{how} while slowing down");
        }

        // One line per float: how fast the object came in, where it stopped or how fast it went past, and how the
        // float ended. The hand's movement since the flick shows how much hand tracking had to make up.
        private static void LogFloat(Flight flight, string ending)
        {
            string arrival = flight.Float == FloatPhase.Braking ? ""
                : flight.ArriveSpeed <= PassedSpeed
                    ? $", stopped {flight.ArriveDistance * 100f:0} cm from the hand"
                    : $", went past {flight.ArriveDistance * 100f:0} cm from the hand at {flight.ArriveSpeed:0.0} m/s";
            Hand hand = flight.Glove.Hand;
            float moved = hand != null ? Vector3.Distance(GloveHand.PalmPosition(hand), flight.LaunchPalm) : 0f;
            Mod.Trace($"{flight.Target.Name} came in at {flight.BrakeSpeed:0.0} m/s{arrival} and {ending} " +
                        $"(the hand moved {moved * 100f:0} cm since the flick{PlayerMoved(flight)}, hand tracking " +
                        $"{Settings.HandTracking.Value}%)");
        }

        // How far the player moved during the flight, for the log, if they did.
        private static string PlayerMoved(Flight flight)
        {
            float shift = PlayerShift(flight).magnitude;
            return shift >= 0.05f ? $", the player {shift * 100f:0} cm" : "";
        }

        // A point on a guided flight's path: the planned arc, moved with the player since the flick, with its end
        // drifting toward the hand by the tracking.
        private static Vector3 PathPointWithTracking(Flight flight, float time)
        {
            Vector3 point = flight.PathPoint(time);
            if (!flight.Guided)
                return point;
            return point + PlayerShift(flight) + (flight.TrackedEnd - flight.Destination) * TrackingWeight(flight, time);
        }

        // How far the player has moved since the flick.
        private static Vector3 PlayerShift(Flight flight) => PlayerOrigin() - flight.Origin;

        // Where the player is: the origin of the tracking space, which moves when the player moves with the stick,
        // but not when they walk around the room or move their hand.
        private static Vector3 PlayerOrigin()
        {
            OpenControllerRig rig = Player.ControllerRig;
            if (rig == null)
                return Vector3.zero;
            Transform space = rig.vrRoot != null ? rig.vrRoot : rig.transform;
            return space.position;
        }

        // How much of the hand's movement the path follows at a moment in the flight: none until "tracking starts
        // at", then easing in to the full hand tracking share at the end. Until v0.27 it eased in from the flick,
        // so the path already followed the hand's swing from the flick itself, and William saw it start tracking
        // "right after I flicked it". Waiting makes it home in at the end instead, as in Half-Life: Alyx.
        private static float TrackingWeight(Flight flight, float time)
        {
            float start = Settings.TrackingStart.Value / 100f;
            float progress = time / Mathf.Max(flight.Duration, 0.01f);
            return Tracking * Smooth((progress - start) / Mathf.Max(1f - start, 0.01f));
        }

        // The turn rate (radians per second) that fits the object to the hand by the time it arrives, or that
        // copies the hand's rotation since the flick.
        private static bool TryTurn(Flight flight, Transform hand, float elapsed, out Vector3 turnRate)
        {
            turnRate = Vector3.zero;
            Quaternion desired;
            Quaternion current;
            float turnTime;
            if (flight.Hold != null)
            {
                desired = flight.Hold.HeldRotation(hand);
                current = flight.Hold.HostBody.rotation;
                float pathTime = flight.Guided ? flight.PathTime : elapsed;
                turnTime = Mathf.Max(flight.Duration - pathTime, MinTurnTime);
            }
            else if (flight.Follow > 0f && elapsed >= FollowDelay)
            {
                if (!flight.Following)
                {
                    flight.Following = true;
                    flight.HandReference = hand.rotation;
                    flight.ObjectReference = flight.Target.MainBody.rotation;
                }
                Quaternion handTurn = hand.rotation * Quaternion.Inverse(flight.HandReference);
                desired = Quaternion.Slerp(Quaternion.identity, handTurn, flight.Follow) * flight.ObjectReference;
                current = flight.Target.MainBody.rotation;
                turnTime = FollowResponse;
            }
            else
            {
                return false;
            }

            Vector3 turn = RotationMath.ToAngleAxis(desired * Quaternion.Inverse(current)) * Mathf.Deg2Rad;
            turnRate = Vector3.ClampMagnitude(turn / turnTime, MaxTurnSpeed);
            return true;
        }

        // An object that vanished mid-flight was collected, like ammo, or despawned. Collected at a hand, it was caught.
        private static void CheckCollected(Flight flight)
        {
            float reach = CollectReach * BodyScale.Value;
            Hand nearest = null;
            foreach (Hand hand in new[] { Player.LeftHand, Player.RightHand })
            {
                if (hand == null)
                    continue;
                float distance = Vector3.Distance(GloveHand.PalmPosition(hand), flight.LastCenter);
                if (distance <= reach)
                {
                    nearest = hand;
                    reach = distance;
                }
            }
            if (nearest == null)
                return;
            flight.Caught = true;
            Feedback.Catch(nearest, flight.Target);
        }

        // The arc height to fly at: the planned one if nothing's in the way, otherwise the first of the others to
        // try that's clear. Only the level itself counts, like walls, floors and door frames. The note says
        // what changed, for the log.
        private static float ClearArc(Target target, Vector3 start, Vector3 end, Vector3 gravity, float arc, float time,
            out string note)
        {
            note = null;
            if (gravity == Vector3.zero || time <= 0f)
                return arc;
            float radius = 0.03f;
            if (target.TryGetBounds(out Bounds bounds))
            {
                Vector3 extents = bounds.extents;
                radius = Mathf.Clamp(Mathf.Min(extents.x, Mathf.Min(extents.y, extents.z)), 0.02f, 0.1f);
            }
            float clearance = HandClearance * BodyScale.Value;

            Collider firstObstacle = null;
            foreach (float share in ArcTries)
            {
                Collider obstacle = FindObstacle(target, start, end, gravity * (arc * share), time, radius, clearance);
                if (obstacle == null)
                {
                    if (firstObstacle != null)
                        note = $"arc {(share < 1f ? "lowered" : "raised")} to {share:0%} to clear {firstObstacle.name}";
                    return arc * share;
                }
                firstObstacle ??= obstacle;
                // A straight path is the same at any share.
                if (arc <= 0f)
                    break;
            }
            note = $"arc blocked by {firstObstacle.name}, flying it anyway";
            return arc;
        }

        // The first piece of the level a sphere of the given radius would hit flying the path, or null. Things it
        // starts out touching, like the table it lies on, and anything near the hand don't count.
        private static Collider FindObstacle(Target target, Vector3 start, Vector3 end, Vector3 pathGravity, float time,
            float radius, float clearance)
        {
            Vector3 velocity = Ballistics.LaunchVelocity(start, end, pathGravity, time);
            float length = Vector3.Distance(start, end) + 0.5f * pathGravity.magnitude * time * time * 0.25f;
            int steps = Mathf.Clamp(Mathf.CeilToInt(length / CheckStep), 4, 32);
            Vector3 previous = start;
            for (int i = 1; i <= steps; i++)
            {
                float t = time * i / steps;
                Vector3 point = start + velocity * t + 0.5f * t * t * pathGravity;
                Vector3 step = point - previous;
                float stepLength = step.magnitude;
                if (stepLength > 1e-4f)
                {
                    int count = Physics.SphereCastNonAlloc(previous, radius, step / stepLength, Hits, stepLength,
                        WorldMask, QueryTriggerInteraction.Ignore);
                    for (int h = 0; h < count; h++)
                    {
                        RaycastHit hit = Hits[h];
                        Collider collider = hit.collider;
                        // A zero distance means the sphere started out touching it.
                        if (collider == null || hit.distance <= 0f || target.Contains(collider.attachedRigidbody))
                            continue;
                        if (Vector3.Distance(hit.point, end) <= clearance)
                            continue;
                        return collider;
                    }
                }
                previous = point;
            }
            return null;
        }

        // Draws the rest of the planned path from the given time, or hides it when that's negative.
        private static void DrawPath(Flight flight, float fromTime)
        {
            if (fromTime < 0f || !Settings.DebugView.Value)
            {
                if (flight.PathLine != null)
                    flight.PathLine.enabled = false;
                return;
            }
            if (flight.PathLine == null)
                flight.PathLine = DebugView.CreateLine("Gravity Gloves Path", PathPoints, new Color(0.3f, 1f, 0.3f));
            if (flight.PathLine == null)
                return;

            _pathBuffer ??= new Il2CppStructArray<Vector3>(PathPoints);
            for (int i = 0; i < PathPoints; i++)
            {
                float time = Mathf.Lerp(fromTime, flight.Duration, i / (float)(PathPoints - 1));
                _pathBuffer[i] = PathPointWithTracking(flight, time);
            }
            flight.PathLine.SetPositions(_pathBuffer);
            flight.PathLine.enabled = true;
        }

        // How far along its flight an object is, from 0 at the flick to 1 when it arrives. A guided flight let go
        // near the hand counts as arrived.
        private static float Progress(Flight flight)
        {
            if (flight.Guided)
                return flight.Released ? 1f : flight.PathTime / flight.Duration;
            return (Time.time - flight.StartTime) / flight.Duration;
        }

        // A tick once the object is close enough to grab the normal way, as in Half-Life: Alyx: when the hand's own
        // hover (what the game would grab if the hand closed now) is on one of its grips, or its nearest part is inside
        // the game's hover sphere. Until v0.34 it came within grab assist's reach, or 25 cm without it, and William
        // wanted it only once the object is really close to the hand (his grab assist reach is 40 cm).
        private static void CheckInReach(Flight flight, Hand hand, Vector3 center)
        {
            if (!HoversOver(hand, flight.Target) && !InHoverSphere(hand, flight.Target, center))
                return;
            flight.ReachTicked = true;
            Feedback.ReachTick(hand.Controller);
        }

        private static bool HoversOver(Hand hand, Target target)
        {
            HandReciever hovered = hand.HoveringReceiver;
            if (hovered == null)
                return false;
            Rigidbody body = hovered.GetComponentInParent<Rigidbody>();
            return body != null && target.Contains(body);
        }

        private static bool InHoverSphere(Hand hand, Target target, Vector3 center)
        {
            float radius = hand.HoverSphereRadius;
            Transform palm = hand.palmPositionTransform;
            if (radius <= 0f || palm == null)
                return false;
            Vector3 point = palm.position + palm.forward * (radius * HoverSphereOut);
            if (!_loggedHoverSphere)
            {
                _loggedHoverSphere = true;
                Mod.Trace($"The hand's grab sphere is {radius * 200f:0.0} cm across, centred {radius * HoverSphereOut * 100f:0.0} cm " +
                            "out from the palm (the in reach tick comes once a pulled object is in it)");
            }
            if (Vector3.Distance(center, point) > radius + MaxReachTickSize)
                return false;
            return Vector3.Distance(target.SurfacePointToward(point), point) <= radius;
        }

        private static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static Flight FindFlightOf(Target target)
        {
            foreach (Flight flight in Active)
            {
                if (flight.Target == target)
                    return flight;
            }
            return null;
        }

        private static void RemoveFlightOf(Target target)
        {
            Flight flight = FindFlightOf(target);
            if (flight == null)
                return;
            LogArrivalNote(flight);
            LineFactory.Destroy(flight.PathLine);
            flight.Target.SetFastCollision(false);
            Active.Remove(flight);
        }

        private static void QueueFitCheck(Flight flight, Hand hand)
        {
            if (flight.Hold != null)
                FitChecks.Add(new FitCheck { Hand = hand, Target = flight.Target, Hold = flight.Hold, Time = Time.time + FitCheckDelay });
        }

        private static void RunFitChecks()
        {
            for (int i = FitChecks.Count - 1; i >= 0; i--)
            {
                FitCheck check = FitChecks[i];
                if (Time.time < check.Time)
                    continue;
                FitChecks.RemoveAt(i);
                if (check.Target.IsAlive && check.Hand != null && HeldObjects.KeyOf(check.Hand) == check.Target.Key)
                    check.Hold.LogCheck(check.Hand, check.Target.Name);
            }
        }
    }
}
