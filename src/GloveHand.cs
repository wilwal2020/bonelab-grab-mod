using System.Collections.Generic;
using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace GravityGloves
{
    // One hand's gravity glove: select a target, lock it while the input button is held, and launch it
    // toward the hand when the wrist flicks the fingers up.
    internal sealed class GloveHand
    {
        // Hysteresis so a half squeeze doesn't flicker between held and released.
        private const float PressThreshold = 0.6f;
        private const float ReleaseThreshold = 0.35f;

        // An object further than this share of the max range away isn't pulled.
        private const float MaxPullShare = 1.5f;

        // Rough shoulder position relative to the headset, for aiming from the shoulder through the palm.
        private const float ShoulderDrop = 0.2f;
        private const float ShoulderOffset = 0.17f;

        // For targets further below the hand than this (the sine of 35 degrees), a flick counts in either
        // direction, since pointing down the fingers can swing up either way.
        private const float SteepBelow = -0.57f;

        // Between these angles below the hand, the flick axis blends from the world's horizontal axis to the
        // controller's own side-to-side axis. Pointing almost straight down, the world axis says nothing about how
        // the wrist is turned: at 82-86 degrees below, flicks read 58-78 degrees off (William, v0.18.1).
        private const float HandAxisFrom = 60f;
        private const float HandAxisTo = 80f;

        // An ammo batch is looked for again this often while aiming, since things around it can move.
        private const float BatchRefreshInterval = 0.25f;

        // A batch's arrival points are spread this far around the palm, and each arrives this much later than the one before.
        private const float BatchSpread = 0.07f;
        private const float BatchStagger = 0.05f;

        // How quickly the lock hum fades: gently when the lock is let go, at once on the flick so the launch is crisp.
        private const float HumRelease = 0.12f;
        private const float HumCut = 0.03f;

        private static bool _loggedRig;

        private readonly bool _isRight;
        private readonly Targeting _targeting = new Targeting();
        private readonly MotionTracker _controllerMotion = new MotionTracker();
        private readonly MotionTracker _rigMotion = new MotionTracker();
        private readonly AimLine _line = new AimLine();
        private readonly LoopSound _hum = new LoopSound("Gravity Gloves Hum", () => null, 0f);
        private float _humRelease = HumRelease;

        // Other ammo pickups selected along with the target, and scratch lists for launching and glowing.
        private readonly List<Target> _batch = new List<Target>();
        private readonly List<Target> _launched = new List<Target>();
        private readonly List<Target> _glowing = new List<Target>();

        private Target _target;

        // The object the beam comes from. It stays after a flick, so the beam can shoot back into it in flight.
        private Target _lineTarget;
        private bool _locked;
        private bool _gripHeld;
        private bool _waitForRelease;
        private float _cooldownUntil;
        private float _nextBatchRefresh;

        // Fastest flick during the current lock, logged if the lock ends without a pull.
        private float _peakFlickSpeed;
        private float _peakFlickAngle;

        public GloveHand(bool isRight)
        {
            _isRight = isRight;
        }

        public Hand Hand => _isRight ? Player.RightHand : Player.LeftHand;
        public bool IsInputHeld { get; private set; }

        // How fast the player is moving, for hand prediction.
        public Vector3 PlayerVelocity => _rigMotion.Velocity;

        private BaseController Controller => _isRight ? Player.RightController : Player.LeftController;
        private bool Enabled => Settings.Gloves.Value.Has(_isRight);
        private int Index => _isRight ? 1 : 0;
        private string Side => _isRight ? "Right" : "Left";

        public static Vector3 PalmPosition(Hand hand)
        {
            Transform palm = hand.palmPositionTransform;
            return palm != null ? palm.position : hand.transform.position;
        }

        // Whether the built-in force pull should be skipped for this hand right now.
        public bool Blocks(Hand hand)
        {
            if (hand == null || !Enabled || (_target == null && !Flights.HasFlightFrom(this)))
                return false;
            Hand own = Hand;
            return own != null && own.Pointer == hand.Pointer;
        }

        public void Reset()
        {
            _targeting.Reset();
            _controllerMotion.Clear();
            _rigMotion.Clear();
            HideVisuals();
            _target = null;
            _batch.Clear();
            _locked = false;
            _gripHeld = false;
            IsInputHeld = false;
            _waitForRelease = false;
        }

        public void Update()
        {
            OpenControllerRig rig = Player.ControllerRig;
            Hand hand = Hand;
            BaseController controller = Controller;
            Transform head = rig != null && rig.headset != null ? rig.headset : Player.Head;
            if (!Enabled || rig == null || hand == null || controller == null || head == null)
            {
                Reset();
                return;
            }
            LogRigOnce(rig, hand);

            float now = Time.unscaledTime;

            // Track the controller in tracking space, so walking and turning don't count as hand movement.
            Transform space = rig.vrRoot != null ? rig.vrRoot : rig.transform;
            Transform controllerTransform = controller.transform;
            _controllerMotion.AddSample(now, space.InverseTransformPoint(controllerTransform.position),
                Quaternion.Inverse(space.rotation) * controllerTransform.rotation);
            // The rig itself moves with the player, which is what hand prediction extrapolates.
            _rigMotion.AddSample(now, space.position, Quaternion.identity);

            // The grip drives the middle, ring and pinky fingers, and the trigger drives the index finger.
            float grip = controller.GetMiddleCurlAxis();
            float trigger = controller.GetIndexCurlAxis();

            // Grab assist listens to the grip, BONELAB's grab button, whatever the glove's input is.
            bool gripHeld = IsHeld(grip, _gripHeld);
            bool gripPressed = gripHeld && !_gripHeld;
            _gripHeld = gripHeld;

            float input = InputValue(grip, trigger);
            bool inputHeld = IsHeld(input, IsInputHeld);
            bool pressed = inputHeld && !IsInputHeld;
            IsInputHeld = inputHeld;
            if (!inputHeld)
                _waitForRelease = false;

            // A hand that's holding something is just a hand.
            if (hand.HasAttachedObject())
            {
                _target = null;
                _batch.Clear();
                _locked = false;
                HideVisuals();
                return;
            }

            // A hand reaching into a wrist pocket, or taking something out of one, isn't aiming.
            if (WristPockets.IsBusy(Index))
            {
                _target = null;
                _batch.Clear();
                _locked = false;
                HideVisuals();
                return;
            }

            if (gripPressed && Settings.GrabAssist.Value > 0f &&
                Flights.TryAssistGrab(hand, Settings.GrabAssist.Value * BodyScale.Value, out string caught))
            {
                Mod.Trace($"{Side} hand grab assist caught {caught}");
                _target = null;
                _batch.Clear();
                _locked = false;
                HideVisuals();
                return;
            }

            Vector3 palm = PalmPosition(hand);
            Vector3 aim = AimDirection(head, palm, controllerTransform);
            if (Settings.DebugView.Value)
                DebugView.ShowCone(Index, palm, aim, Settings.ConeAngle.Value, Settings.MaxRange.Value * BodyScale.Value);
            else
                DebugView.HideCone(Index);

            if (_locked)
            {
                UpdateLocked(controller, hand, head, space, palm, inputHeld, now);
            }
            else
            {
                Target selected = _targeting.Select(palm, aim, head, now);
                if (selected != _target)
                {
                    _target = selected;
                    _nextBatchRefresh = 0f;
                    if (selected != null)
                    {
                        Feedback.Play(FeedbackKind.Select, controller, palm);
                        _line.Flash(0.5f);
                    }
                }

                // Ammo lying next to the selected ammo comes along with it. Locking freezes the batch.
                if (now >= _nextBatchRefresh)
                {
                    _nextBatchRefresh = now + BatchRefreshInterval;
                    AmmoBatch.Find(_target, _batch);
                }

                if (pressed && _target != null && !_waitForRelease && now >= _cooldownUntil)
                {
                    _locked = true;
                    _humRelease = HumRelease;
                    _peakFlickSpeed = 0f;
                    _peakFlickAngle = 0f;
                    _hum.Recording = GloveRecordings.Pick(SoundKind.Hum);
                    Feedback.Play(FeedbackKind.Lock, controller, palm);
                    _line.Flash(1f);
                    string batch = _batch.Count > 0 ? $" with {_batch.Count} more ammo pickups" : "";
                    string weapon = _target.WeaponKind != null ? $", weapon: {_target.WeaponKind}" : "";
                    Target runnerUp = _targeting.RunnerUp;
                    string next = runnerUp != null && runnerUp != _target
                        ? $", next best {runnerUp.Name} by {_targeting.RunnerUpGap:0.0} deg"
                        : "";
                    Mod.Trace($"{Side} hand locked {_target.Name} ({_target.Mass:0.0} kg{weapon}){batch}{next}");
                }
            }

            // From just holding the lock to a full squeeze, for the beam's strength and the hum.
            float squeeze = Mathf.InverseLerp(ReleaseThreshold, 1f, input);
            UpdateVisuals(palm, aim, head.position, squeeze);
            UpdateHum(controller, palm, squeeze);
        }

        // A hum at the hand and a light vibration while locked, both stronger the harder the player squeezes.
        private void UpdateHum(BaseController controller, Vector3 palm, float squeeze)
        {
            float level = _locked ? Settings.LockHum.Value / 100f * Sounds.Volume * _hum.RecordingVolume : 0f;
            _hum.Update(palm, level * (0.12f + 0.4f * squeeze), 0.9f + 0.25f * squeeze, _humRelease);
            if (_locked)
                Feedback.Buzz(Index, controller, squeeze);
        }

        private void UpdateVisuals(Vector3 palm, Vector3 aim, Vector3 eye, float squeeze)
        {
            // The beam comes out of the object's surface on the side facing the hand. It keeps following the
            // object while it fades out or shoots back in.
            BeamMode mode = Settings.Beam.Value;
            bool showLine = _target != null && (mode == BeamMode.Always || (mode == BeamMode.Locked && _locked));
            if (showLine)
                _lineTarget = _target;
            Vector3? lineSource = _lineTarget != null && _lineTarget.IsAlive && _lineTarget.IsActive
                ? _lineTarget.SurfacePointToward(palm)
                : (Vector3?)null;
            _line.Update(showLine, palm, aim, lineSource, _locked, eye, squeeze);
            if (!_line.IsShowing)
                _lineTarget = null;

            // The selection glows, batch and all, and keeps glowing more strongly while it flies to the hand,
            // fading out along the way.
            bool strong = true;
            float fade = 1f;
            if (_target != null)
            {
                _glowing.Clear();
                _glowing.Add(_target);
                _glowing.AddRange(_batch);
                strong = _locked;
            }
            else
            {
                fade = Flights.AirborneGlowFrom(this, _glowing);
            }
            if (Settings.Glow.Value && _glowing.Count > 0)
                ObjectGlow.Show(Index, _glowing, strong, fade);
            else
                ObjectGlow.Hide(Index);
        }

        private void UpdateLocked(BaseController controller, Hand hand, Transform head, Transform space, Vector3 palm,
            bool inputHeld, float now)
        {
            Vector3 toTarget = (_target.CenterOfMass - palm).normalized;
            if (!inputHeld || !_targeting.CanStayLocked(_target, palm))
            {
                // Let go, or the object got out of reach.
                LogLockEnd(inputHeld, toTarget);
                Sounds.PlayRelease(palm);
                _locked = false;
                return;
            }

            Vector3 flickAxis = FlickAxis(head, palm, toTarget, controller.transform);
            if (flickAxis == Vector3.zero)
                return;

            Vector3 angularVelocity = space.TransformDirection(_controllerMotion.AngularVelocity);
            float flickSpeed = Vector3.Dot(angularVelocity, flickAxis);
            float flickAngle = Vector3.Angle(angularVelocity, flickAxis);
            if (toTarget.y < SteepBelow && flickSpeed < 0f)
            {
                flickSpeed = -flickSpeed;
                flickAngle = 180f - flickAngle;
            }
            if (flickSpeed > _peakFlickSpeed)
            {
                _peakFlickSpeed = flickSpeed;
                _peakFlickAngle = flickAngle;
            }

            if (flickSpeed >= Settings.FlickSpeed.Value && flickAngle <= Settings.DirectionTolerance.Value)
                Launch(controller, hand, palm, flickSpeed, flickAngle, toTarget, now);
        }

        private void Launch(BaseController controller, Hand hand, Vector3 palm, float flickSpeed, float flickAngle,
            Vector3 toTarget, float now)
        {
            Target target = _target;

            // An object far out of range isn't pulled. In the v0.26.1 test the DevManipulator, after falling out of
            // the level, still counted as near by its main body while its centre was 4 km away, and was pulled from
            // 4099 m at 793 m/s.
            float distance = Vector3.Distance(target.CenterOfMass, palm);
            float limit = Settings.MaxRange.Value * BodyScale.Value * MaxPullShare;
            if (distance > limit)
            {
                Mod.Trace($"{Side} hand didn't pull {target.Name}: it's {distance:0} m away, past {limit:0} m, " +
                            "so it may have fallen out of the level");
                _target = null;
                _batch.Clear();
                _locked = false;
                _waitForRelease = true;
                HideVisuals();
                return;
            }

            LaunchResult result = Flights.Launch(target, this, hand, palm);
            int batchSize = LaunchBatch(target, hand, palm, result.FlightTime);

            Feedback.Play(FeedbackKind.Launch, controller, palm);
            _humRelease = HumCut;
            // The beam shoots back into the object as it flies. A flash here used to make it flicker at full size.
            _line.Retract(Settings.LineRetractTime.Value);
            string batch = batchSize > 0 ? $" with {batchSize} more ammo pickups" : "";
            // For targets far below the hand, where the flick axis follows the controller, say how far off it was.
            float below = -Mathf.Asin(Mathf.Clamp(toTarget.y, -1f, 1f)) * Mathf.Rad2Deg;
            string steep = below >= HandAxisFrom ? $" ({flickAngle:0} deg off, target {below:0} deg below the hand)" : "";
            Mod.Trace($"{Side} hand pulled {target.Name}{batch} from {result.Distance:0.0} m: flick {flickSpeed:0} deg/s{steep}, " +
                        $"flight {result.FlightTime:0.00} s, launch speed {result.Speed:0.0} m/s, rotation: {result.Rotation}" +
                        result.Notes);

            _target = null;
            _batch.Clear();
            _locked = false;
            _waitForRelease = true;
            _cooldownUntil = now + Settings.Cooldown.Value;
        }

        // Sends the rest of an ammo batch after the target. Their arrival points are spread around the palm and
        // their arrivals staggered, so they don't all land on the same spot at once. Returns how many went.
        private int LaunchBatch(Target target, Hand hand, Vector3 palm, float targetFlightTime)
        {
            if (_batch.Count == 0)
                return 0;

            Vector3 toHand = palm - target.CenterOfMass;
            Vector3 side = Vector3.Cross(Vector3.up, toHand);
            side = side.sqrMagnitude > 1e-6f ? side.normalized : Vector3.right;

            _launched.Clear();
            _launched.Add(target);
            float longestFlight = targetFlightTime;
            foreach (Target member in _batch)
            {
                if (!member.IsAlive || !member.IsActive || member.IsKinematic || !Flights.CanPull(member))
                    continue;
                float angle = _launched.Count * 2.4f;
                Vector3 offset = (side * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle)) * (BatchSpread * BodyScale.Value);
                LaunchResult result = Flights.Launch(member, this, hand, palm, offset, BatchStagger * _launched.Count,
                    main: false);
                longestFlight = Mathf.Max(longestFlight, result.FlightTime);
                _launched.Add(member);
            }

            if (_launched.Count > 1)
                AmmoBatch.Track(_launched, longestFlight);
            return _launched.Count - 1;
        }

        private void LogLockEnd(bool inputHeld, Vector3 toTarget)
        {
            if (inputHeld)
            {
                Mod.Trace($"{Side} hand lost the lock on {_target.Name}");
                return;
            }
            float elevation = Mathf.Asin(Mathf.Clamp(toTarget.y, -1f, 1f)) * Mathf.Rad2Deg;
            Mod.Trace($"{Side} hand let go of {_target.Name} without a flick (target {Mathf.Abs(elevation):0} deg " +
                        $"{(elevation < 0f ? "below" : "above")} the hand). Fastest flick was {_peakFlickSpeed:0} deg/s, " +
                        $"{_peakFlickAngle:0} deg off (a flick needs {Settings.FlickSpeed.Value} deg/s within " +
                        $"{Settings.DirectionTolerance.Value} deg)");
        }

        private void HideVisuals()
        {
            _line.Hide();
            _lineTarget = null;
            _hum.FadeOut(_humRelease);
            DebugView.HideCone(Index);
            ObjectGlow.Hide(Index);
        }

        private static float InputValue(float grip, float trigger)
        {
            switch (Settings.Input.Value)
            {
                case InputButton.Trigger:
                    return trigger;
                case InputButton.Either:
                    return Mathf.Max(grip, trigger);
                default:
                    return grip;
            }
        }

        private static bool IsHeld(float value, bool wasHeld) =>
            wasHeld ? value > ReleaseThreshold : value >= PressThreshold;

        // The cone's direction. Arm aims along the line from an estimated shoulder through the palm, which
        // follows the arm whatever the controller's tilt. Hand aims along the controller. Either way it can be
        // tilted up or down (pitch) and turned outward or inward (yaw, mirrored so outward means the same on
        // both hands).
        private Vector3 AimDirection(Transform head, Vector3 palm, Transform controller)
        {
            float pitch = Settings.AimPitch.Value;
            float yaw = _isRight ? Settings.AimYaw.Value : -Settings.AimYaw.Value;
            if (Settings.AimFrom.Value == AimSource.Hand)
                return controller.rotation * Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;

            Vector3 right = head.right;
            right.y = 0f;
            right = right.sqrMagnitude > 1e-4f ? right.normalized : Vector3.right;
            float scale = BodyScale.Value;
            Vector3 shoulder = head.position + Vector3.down * (ShoulderDrop * scale) +
                               right * ((_isRight ? ShoulderOffset : -ShoulderOffset) * scale);
            Vector3 direction = palm - shoulder;
            direction = direction.sqrMagnitude > 1e-4f ? direction.normalized : head.forward;

            direction = Quaternion.AngleAxis(yaw, Vector3.up) * direction;
            Vector3 pitchAxis = Vector3.Cross(direction, Vector3.up);
            if (pitchAxis.sqrMagnitude > 1e-4f)
                direction = Quaternion.AngleAxis(pitch, pitchAxis.normalized) * direction;
            return direction;
        }

        // The axis a wrist flick turns the hand around to lift the fingers, while pointing along toTarget.
        // Pointing far down, the world's horizontal axis gives way to the controller's side-to-side axis, which
        // is what the wrist really turns around however the hand is rolled. Its sign doesn't matter there, since
        // below SteepBelow a flick counts either way. If neither works, it uses the swing toward or away from
        // the player.
        private static Vector3 FlickAxis(Transform head, Vector3 palm, Vector3 toTarget, Transform controller)
        {
            Vector3 axis = Vector3.Cross(toTarget, Vector3.up);
            float below = -Mathf.Asin(Mathf.Clamp(toTarget.y, -1f, 1f)) * Mathf.Rad2Deg;
            float handShare = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(HandAxisFrom, HandAxisTo, below));
            Vector3 handAxis = Vector3.ProjectOnPlane(controller.right, toTarget);
            if (handShare > 0f && handAxis.sqrMagnitude > 0.04f)
            {
                handAxis.Normalize();
                if (axis.sqrMagnitude < 1e-6f)
                    return handAxis;
                Vector3 worldAxis = axis.normalized;
                if (Vector3.Dot(handAxis, worldAxis) < 0f)
                    handAxis = -handAxis;
                return Vector3.Lerp(worldAxis, handAxis, handShare).normalized;
            }
            if (axis.sqrMagnitude < 0.04f)
            {
                Vector3 away = palm - head.position;
                away.y = 0f;
                if (away.sqrMagnitude < 1e-4f)
                {
                    away = head.forward;
                    away.y = 0f;
                }
                axis = Vector3.Cross(toTarget, toTarget.y < 0f ? away : -away);
            }
            return axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.zero;
        }

        private static void LogRigOnce(OpenControllerRig rig, Hand hand)
        {
            if (_loggedRig)
                return;
            _loggedRig = true;
            Mod.Trace($"Found the player rig. Head: {(rig.headset != null ? "headset" : "BoneLib fallback")}, " +
                        $"tracking space: {(rig.vrRoot != null ? "vrRoot" : "rig fallback")}, " +
                        $"built-in far hover layer mask: {hand.farHoverLayerMask.value}");
        }
    }
}
