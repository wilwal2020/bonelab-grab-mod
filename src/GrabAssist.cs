using System;
using System.Collections.Generic;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Utilities;
using UnityEngine;

namespace GravityGloves
{
    // Grabs a pulled object for the player, for grab assist (and objects taken out of a wrist pocket, which
    // aren't moved). The object is moved into place over a moment, then the hand grabs it. Confirming from a
    // distance without moving the object first let the game pick holds you could never do by hand, like inside
    // the object.
    //
    // There are two ways to catch:
    // - Weapons, and anything turned to fit the hand on the way, whose main grip is a fixed one (a gun's
    //   handle, not its slide) or a handle (a sword's, or the milk crate's) are moved into exactly the hold the
    //   game gives that grip.
    // - Anything else is caught where it touches. It moves straight to the palm without turning until its
    //   nearest surface is just in front of the palm, the way a real catch works. This covers grips that can be
    //   held anywhere, like the traffic cone's box grip, whose default hold is inside the cone.
    //
    // Once it's in place the grip is hovered and the grab confirmed in the same physics step, whatever the grip.
    // Every grip is scored from the hand's root, as the game's own hover does, and a grip that isn't fixed whose
    // hold is still far from the hand is told to hold where the hand is.
    //
    // Until v0.26 grips that aren't fixed hovered for 3 physics steps first. The player is holding grip, so the
    // game always closed the hand by itself during those steps, and grab assist then ended the hover and unlocked
    // it while the game's grab was still finishing. Those grabs left the hand holding the object without being
    // on any of its grips, for the whole hold: stretched toward it, inside a handle, 36-73 cm off after a pocket
    // take-out, and refused by the pockets as "held by nothing" (William: "a weird location to grab, that
    // wouldn't be available when grabbing normally", the ammo crate, the cone, the milk crate, swords). Handing
    // the grab to the game's own hover (the first v0.26 build) did the same. Only the grabs grab assist confirmed
    // itself, all of them guns, were whole.
    internal static class GrabAssist
    {
        private const float MoveTime = 0.1f;
        private const float MaxTurnSpeed = 30f;

        // A grip whose hold is further than this from the hand holds where the hand is instead.
        private const float MaxHoldOffset = 0.08f;

        // How long after a catch where it touches to check where the hand ended up.
        private const float CheckDelay = 0.4f;

        // A catch where it touches stops this far in front of the palm, rather than in it.
        private const float PalmClearance = 0.01f;

        // The grip is only hovered once the object is in place, so it works out the hold from a hand that's
        // already where it should be. Asking it while the object was still turning gave holds 25-47 degrees off.
        private sealed class Grab
        {
            public Target Target;
            public Hand Hand;
            public Grip Grip;
            public Rigidbody Host;
            public Frame HostInHand;
            public Vector3 CenterInHost;
            public float EndTime;

            // Caught where it touches, so the log checks the hand stayed there.
            public bool Touching;

            // Taken out of a pocket, so it's held exactly as it was put away, and for a handle, the grip's own hold
            // as it was then, put back once the hand has it (see GripHold).
            public bool KeepHold;
            public GripHold Hold;
        }

        private sealed class CatchCheck
        {
            public Target Target;
            public Hand Hand;
            public Rigidbody Host;
            public string Grip;
            public Vector3 PalmInHost;
            public float Time;
        }

        // A grab the game started by itself, watched until the hand is on a grip of the object, for the log.
        private sealed class GameGrab
        {
            public Hand Hand;
            public Target Target;
            public float Start;
            public bool Logged;
        }

        // How long a grab the game started may take to put the hand on a grip before it's logged as stuck.
        private const float GameGrabFinishTime = 1f;

        // A grip's own hold turned further than this from the hand isn't kept.
        private const float MaxHoldAngle = 30f;

        private static readonly List<Grab> Pending = new List<Grab>();
        private static readonly List<CatchCheck> Checks = new List<CatchCheck>();
        private static readonly List<GameGrab> Finishing = new List<GameGrab>();
        private static bool _loggedError;

        public static bool IsGrabbing(Target target)
        {
            foreach (Grab grab in Pending)
            {
                if (grab.Target == target)
                    return true;
            }
            return false;
        }

        // Starts grabbing the object into the hand, in its exact hold if it should go by its main grip and that
        // grip is a fixed one or a handle, otherwise where it touches. Returns false if it can't be grabbed. The
        // description says how, by which grip and how far the object has to move, for the log.
        public static bool Start(Target target, Hand hand, bool mainGrip, out string description)
        {
            description = null;
            if (IsGrabbing(target) || hand.HasAttachedObject())
                return false;

            bool locked = false;
            try
            {
                string note = "";
                if (mainGrip)
                {
                    Grip main = target.FindGrip();
                    if (main != null && GripKinds.Of(main) != GripKind.Surface)
                    {
                        HoldPose hold = HoldPose.TryCreate(target, hand, main, out string problem);
                        if (hold != null)
                        {
                            LockHover(hand);
                            locked = true;
                            description = Queue(target, hand, hold.Grip, hold.HostBody, hold.HostInHand,
                                "in the main grip's hold", false);
                            return true;
                        }
                        note = $" (the main grip gave no hold: {problem})";
                    }
                    else if (main != null)
                    {
                        note = $" (its main grip {main.name} is a {GripKinds.TypeName(main)}, which can be held anywhere)";
                    }
                }

                // Where it touches: the nearest point of its surface goes just in front of the palm, and the hand
                // closes on the grip there. Until v0.26 it went to the palm point itself, inside the hand.
                Vector3 palm = GloveHand.PalmPosition(hand);
                Vector3 surface = target.ContactPoint(palm);
                Grip grip = target.FindGripNearest(surface);
                if (grip == null)
                    return false;
                Rigidbody body = target.MainBody;
                Frame bodyFrame = Frame.Of(body.transform);
                Vector3 touchAt = palm + PalmNormal(hand) * (PalmClearance * BodyScale.Value);
                var touching = new Frame(bodyFrame.Position + (touchAt - surface), bodyFrame.Rotation);
                Frame bodyInHand = Frame.Of(hand.transform).Inverse().Then(touching);

                LockHover(hand);
                locked = true;
                description = Queue(target, hand, grip, body, bodyInHand, "where it touches", true) + note;
                return true;
            }
            catch (Exception e)
            {
                LogErrorOnce(e);
                if (locked)
                    Release(hand, null);
                return false;
            }
        }

        public static void FixedUpdate()
        {
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                Grab grab = Pending[i];
                bool done;
                try
                {
                    done = Step(grab);
                }
                catch (Exception e)
                {
                    LogErrorOnce(e);
                    Abort(grab);
                    done = true;
                }
                if (done)
                    Pending.RemoveAt(i);
            }
            if (Checks.Count > 0)
                RunChecks();
            if (Finishing.Count > 0)
                WatchGameGrabs();
        }

        public static void Clear()
        {
            foreach (Grab grab in Pending)
                Abort(grab);
            Pending.Clear();
            Checks.Clear();
            Finishing.Clear();
        }

        // Checks that a grab ends with the hand on a grip of the object. It says when a grab the game started got
        // there, and warns if any grab hasn't after a second: held by the hand but by no grip, as the game's own
        // grabs were in the v0.26 test.
        private static void WatchGameGrabs()
        {
            for (int i = Finishing.Count - 1; i >= 0; i--)
            {
                GameGrab grab = Finishing[i];
                try
                {
                    float waited = Time.time - grab.Start;
                    bool holding = grab.Hand != null && HeldObjects.KeyOf(grab.Hand) == grab.Target.Key;
                    HandReciever receiver = holding ? grab.Hand.AttachedReceiver : null;
                    if (holding && receiver != null)
                    {
                        if (!grab.Logged)
                            Mod.Trace($"The grab of {grab.Target.Name} finished on {receiver.name} {waited * 1000f:0} ms after the hand closed");
                        Finishing.RemoveAt(i);
                    }
                    else if (waited >= GameGrabFinishTime)
                    {
                        if (holding)
                        {
                            Mod.TraceWarning($"The hand still isn't on a grip of {grab.Target.Name} {waited:0.0} s after it " +
                                            "closed on it, so it's held in a broken state");
                        }
                        Finishing.RemoveAt(i);
                    }
                }
                catch (Exception e)
                {
                    LogErrorOnce(e);
                    Finishing.RemoveAt(i);
                }
            }
        }

        // Grabs the object by the given grip, held the given way. For taking an object out of a wrist pocket: it's
        // put back exactly how the hand held it when it went in, so there's nothing to move and the grip works out
        // the same hold as before.
        public static bool StartInPlace(Target target, Hand hand, Grip grip, Rigidbody host, Frame hostInHand,
            GripHold hold, out string description)
        {
            description = null;
            if (grip == null || host == null || IsGrabbing(target) || hand.HasAttachedObject())
                return false;
            try
            {
                LockHover(hand);
                description = Queue(target, hand, grip, host, hostInHand, "in the hold it was put away in", false, 0f, true);
                Pending[Pending.Count - 1].Hold = hold;
                return true;
            }
            catch (Exception e)
            {
                LogErrorOnce(e);
                Release(hand, null);
                return false;
            }
        }

        private static string Queue(Target target, Hand hand, Grip grip, Rigidbody host, Frame hostInHand, string how,
            bool touching, float moveTime = MoveTime, bool keepHold = false)
        {
            Frame hostFrame = Frame.Of(host.transform);
            Vector3 heldPosition = Frame.Of(hand.transform).Then(hostInHand).Position;
            Pending.Add(new Grab
            {
                Target = target,
                Hand = hand,
                Grip = grip,
                Host = host,
                HostInHand = hostInHand,
                CenterInHost = hostFrame.Inverse().TransformPoint(target.CenterOfMass),
                EndTime = Time.time + moveTime,
                Touching = touching,
                KeepHold = keepHold,
            });
            return $"{how}, by {grip.name} ({GripKinds.TypeName(grip)}), moving it " +
                   $"{Vector3.Distance(heldPosition, hostFrame.Position) * 100f:0} cm";
        }

        // Returns true once the grab is finished, one way or the other.
        private static bool Step(Grab grab)
        {
            Target target = grab.Target;
            if (!target.IsAlive || !target.IsActive || grab.Hand == null || grab.Host == null)
            {
                // Collected or despawned on the way, for example ammo that touched the hand.
                Abort(grab);
                return true;
            }
            if (grab.Hand.HasAttachedObject())
            {
                // The game grabbed something itself before grab assist confirmed. It's left alone to finish: the
                // game's grab takes a few physics steps, and the hand counts as holding from the first, so ending
                // the hover or unlocking it here would cut it off (see the class comment). Watched for the log.
                bool caughtIt = HeldObjects.KeyOf(grab.Hand) == target.Key;
                HandReciever taken = grab.Hand.AttachedReceiver;
                Finishing.Add(new GameGrab { Hand = grab.Hand, Target = target, Start = Time.time, Logged = taken != null });
                if (caughtIt)
                    Feedback.Catch(grab.Hand, target);
                Mod.Trace($"Grab of {target.Name}: the game closed the hand on " +
                            $"{(caughtIt ? "it" : "something else")} by {(taken != null ? taken.name : "no grip yet")} " +
                            "before grab assist confirmed it");
                return true;
            }

            Frame held = Frame.Of(grab.Hand.transform).Then(grab.HostInHand);
            float remaining = grab.EndTime - Time.time;
            if (remaining <= Time.fixedDeltaTime)
            {
                // In place: hovered and confirmed in this same step, so the game never gets to close the hand by
                // itself. A grip that isn't fixed holds where the hand is if its own hold is far from it or turned
                // away from it, and always for a pocket take-out.
                grab.Host.transform.SetPositionAndRotation(held.Position, held.Rotation);
                Rigidbody handBody = grab.Hand.rb;
                target.SetMotion(handBody != null ? handBody.velocity : Vector3.zero,
                    handBody != null ? handBody.angularVelocity : Vector3.zero);
                BeginHover(grab.Hand, grab.Grip);
                bool exact = GripKinds.Of(grab.Grip) == GripKind.Fixed;
                string hold = exact ? null : SettleHold(grab.Hand, grab.Grip, grab.KeepHold);
                Confirm(grab.Hand, grab.Grip, true);
                if (grab.Hold != null)
                {
                    try
                    {
                        hold += $"; {grab.Hold.Apply(grab.Grip, grab.Hand)}";
                    }
                    catch (Exception e)
                    {
                        hold += $"; couldn't put the handle's hold back ({e.Message})";
                    }
                }
                Feedback.Catch(grab.Hand, target);
                if (hold != null)
                    Mod.Trace($"Grab of {target.Name} by {grab.Grip.name}: {hold}");
                if (grab.Touching)
                    QueueCheck(grab, grab.Grip.name);
                if (!exact)
                    Finishing.Add(new GameGrab { Hand = grab.Hand, Target = target, Start = Time.time, Logged = true });
                return true;
            }

            // Steer it into the held pose by the end of the move.
            Vector3 heldCenter = held.TransformPoint(grab.CenterInHost);
            Vector3 velocity = (heldCenter - target.CenterOfMass) / remaining;
            Vector3 turn = RotationMath.ToAngleAxis(held.Rotation * Quaternion.Inverse(grab.Host.rotation)) *
                           Mathf.Deg2Rad / remaining;
            target.SetMotion(velocity, Vector3.ClampMagnitude(turn, MaxTurnSpeed));
            if (target.UsesGravity)
                target.AddAcceleration(-Physics.gravity);
            return false;
        }

        // Compares where the grip would hold the hand with where the hand is. A grip that still wants the hand
        // far away, or turned away from it, is told to hold it where it is: the grip's target goes where the hand
        // is, offset the same way the hand is from every grip's target. A pocket take-out always holds where the
        // hand is, since the object is back in exactly the hold it was put away in. Says what happened, for the log.
        //
        // For a handle, the own hold compared here is its unflipped one, so a hand holding it the other way round
        // (see BeginHover) shows as turned about 180 degrees from it. The joint the grab makes takes which way round
        // from the hover, and only where the hand sits on the handle from this target.
        private static string SettleHold(Hand hand, Grip grip, bool alwaysKeep)
        {
            Frame target = Frame.Of(grip.GetTargetInWorld(hand));
            Frame handInTarget = Frame.Of(grip.GetForcePullHandTransform(hand)).Inverse();
            Frame now = Frame.Of(hand.transform);
            if (!target.IsValid || !handInTarget.IsValid)
                return "the grip gave no hold to check";

            Frame wanted = target.Then(handInTarget);
            float offset = Vector3.Distance(wanted.Position, now.Position);
            float angle = Quaternion.Angle(wanted.Rotation, now.Rotation);
            if (!alwaysKeep && offset <= MaxHoldOffset && angle <= MaxHoldAngle)
                return $"its own hold, {offset * 100f:0} cm and {angle:0} deg from the hand";

            Frame held = now.Then(handInTarget.Inverse());
            grip.SetTargetInWorld(hand, held.Position, held.Rotation);
            return alwaysKeep
                ? $"held as it was put away (its own hold was {offset * 100f:0} cm and {angle:0} deg from that)"
                : $"its own hold was {offset * 100f:0} cm and {angle:0} deg from the hand, so it holds where the hand is";
        }

        // The same steps LabFusion uses to make a hand take a grip (GripExtensions.TryAttach): stop the hand
        // hovering anything else, then hover the grip.
        private static void LockHover(Hand hand)
        {
            hand.GrabLock = false;
            InventoryHand inventoryHand = hand.GetComponent<InventoryHand>();
            if (inventoryHand != null)
                inventoryHand.IgnoreUnlock();
            hand.HoverLock();
        }

        // Every grip is scored from the hand's root, as the game's own hover does (Hand.UpdateHovering): a grip adds
        // where its handle sits in the hand itself (HandPoseAnimator.GetHandleInHandNeutral).
        //
        // That matters for handles. A CylinderGrip decides here which way round along it the hand holds it
        // (isZFlipped): the other way round if the grip's axis points against the same axis in the hand's handle.
        // The grab then makes the hand's joint for that way round (Grip.Snatch turns the hand to the grip's target,
        // flipped or not, while it makes the joint), and nothing turns it back later. Box, barrel, sphere and
        // generic grips don't use the hand's pose given here.
        //
        // From v0.26 to v0.28 grips that aren't fixed were given the hand's handle (Hand.GetHandleInWorld) instead,
        // so the handle's turn in the hand counted twice and the test came out either way. In the v0.28 test 11 of
        // 17 handles taken out of a pocket were grabbed the other way round from how they went in, and came out
        // reversed (96-179 degrees: a dagger in a reverse grip, William).
        private static void BeginHover(Hand hand, Grip grip)
        {
            SimpleTransform handTransform = SimpleTransform.Create(hand.transform);
            grip.ValidateGripScore(hand, handTransform);
            grip.OnHandHoverBegin(hand, true);
            grip.ValidateGripScore(hand, handTransform);
        }

        // The way the palm faces: away from the back of the hand, which is the physics hand's right on the right
        // hand and its left on the left.
        private static Vector3 PalmNormal(Hand hand)
        {
            Hand left = BoneLib.Player.LeftHand;
            bool isLeft = left != null && left.Pointer == hand.Pointer;
            return isLeft ? hand.transform.right : -hand.transform.right;
        }

        // Instant: the grip's snatch makes the hand's joint at once, inside this call.
        //
        // v0.26.2 to v0.28 also set the grip's ignoreGripTargetOnAttach for a moment around this, to attach the hand
        // where it is. The game never reads that field. Holding where the hand is comes from SettleHold's target.
        private static void Confirm(Hand hand, Grip grip, bool isInstant)
        {
            hand._mHoveringReceiver = grip;
            grip.OnGrabConfirm(hand, isInstant);
        }

        // Gives up before the hover started, so only the hover lock is undone.
        private static void Abort(Grab grab) => Release(grab.Hand, null);

        // Undoes the hover, so the hand is free to grab something else.
        private static void Release(Hand hand, Grip hovered)
        {
            try
            {
                if (hovered != null && hand != null)
                    hovered.OnHandHoverEnd(hand);
                if (hand != null)
                    hand.HoverUnlock();
            }
            catch (Exception e)
            {
                LogErrorOnce(e);
            }
        }

        // After a catch where it touches, the log says how far the palm ended up from where it closed on the
        // object. Far means the game moved the hand to a hold of its own, like the middle of a box.
        private static void QueueCheck(Grab grab, string gripName)
        {
            Checks.Add(new CatchCheck
            {
                Target = grab.Target,
                Hand = grab.Hand,
                Host = grab.Host,
                Grip = gripName,
                PalmInHost = Frame.Of(grab.Host.transform).Inverse().TransformPoint(GloveHand.PalmPosition(grab.Hand)),
                Time = Time.time + CheckDelay,
            });
        }

        private static void RunChecks()
        {
            for (int i = Checks.Count - 1; i >= 0; i--)
            {
                CatchCheck check = Checks[i];
                if (Time.time < check.Time)
                    continue;
                Checks.RemoveAt(i);
                if (!check.Target.IsAlive || check.Hand == null || check.Host == null ||
                    HeldObjects.KeyOf(check.Hand) != check.Target.Key)
                {
                    continue;
                }
                Vector3 palm = Frame.Of(check.Host.transform).Inverse().TransformPoint(GloveHand.PalmPosition(check.Hand));
                Mod.Trace($"Catch check for {check.Target.Name} by {check.Grip}: the palm settled " +
                            $"{Vector3.Distance(palm, check.PalmInHost) * 100f:0} cm from where the hand closed on it");
            }
        }

        private static void LogErrorOnce(Exception e)
        {
            if (_loggedError)
                return;
            _loggedError = true;
            Mod.Log.Error($"Grabbing a pulled object failed: {e}");
        }
    }
}
