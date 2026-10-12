using System;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace GravityGloves
{
    // Where an object sits relative to the hand when it's held by its grip. It's worked out from the grip
    // before the hand touches it, so the object can turn to fit the hand while it flies. For grips that can be
    // held anywhere it's only the grip's default hold. Turning the traffic cone to its box grip's default hold
    // lines it up well (William), but grabbing it there put the hand inside the cone, so grab assist only uses
    // it for fixed grips and handles.
    internal sealed class HoldPose
    {
        // A grip further than this from its object means the game gave us something we misread.
        private const float MaxGripDistance = 1.5f;

        private readonly Frame _handInTarget;

        private HoldPose(Grip grip, Frame handInTarget, Rigidbody hostBody, Frame hostInHand, Vector3 centerInHost)
        {
            Grip = grip;
            _handInTarget = handInTarget;
            HostBody = hostBody;
            HostInHand = hostInHand;
            CenterInHost = centerInHost;
        }

        // The grip it's held by, the body the grip is on, that body's pose relative to the hand's transform
        // when held, and the object's centre of mass relative to that body.
        public Grip Grip { get; }
        public Rigidbody HostBody { get; }
        public Frame HostInHand { get; }
        public Vector3 CenterInHost { get; }

        public Quaternion HeldRotation(Transform hand) => hand.rotation * HostInHand.Rotation;

        public Vector3 HeldCenter(Transform hand) => Frame.Of(hand).Then(HostInHand).TransformPoint(CenterInHost);

        // The hold by the object's main grip: a gun's handle rather than its slide.
        public static HoldPose TryCreate(Target target, Hand hand, out string problem) =>
            TryCreate(target, hand, target.FindGrip(), out problem);

        public static HoldPose TryCreate(Target target, Hand hand, Grip grip, out string problem)
        {
            if (grip == null)
            {
                problem = "no grip";
                return null;
            }
            Rigidbody host = grip.GetComponentInParent<Rigidbody>();
            if (host == null || !target.Contains(host))
            {
                problem = "the grip isn't on the object";
                return null;
            }

            // The grip's target is where the hand goes. The force pull hand transform is the target's pose
            // relative to the hand, the same for a grip wherever the hand is. Fit checks on guns and magazines
            // matched it within a few degrees once inverted.
            Frame gripTarget;
            Frame handInTarget;
            try
            {
                gripTarget = Frame.Of(grip.GetNeutralTargetInWorld(hand));
                handInTarget = Frame.Of(grip.GetForcePullHandTransform(hand)).Inverse();
            }
            catch (Exception e)
            {
                problem = $"the grip couldn't be read ({FirstLine(e)})";
                return null;
            }
            if (!gripTarget.IsValid || !handInTarget.IsValid)
            {
                problem = "the grip gave an invalid pose";
                return null;
            }

            Frame hostFrame = Frame.Of(host.transform);
            Frame hostInHand = gripTarget.Then(handInTarget).Inverse().Then(hostFrame);
            if (hostInHand.Position.magnitude > MaxGripDistance)
            {
                problem = $"the grip put the hand {hostInHand.Position.magnitude:0.0} m from the object";
                return null;
            }

            problem = null;
            Vector3 centerInHost = hostFrame.Inverse().TransformPoint(target.CenterOfMass);
            return new HoldPose(grip, handInTarget, host, hostInHand, centerInHost);
        }

        // Logs how far the real held pose is from the predicted one, once the object has settled in the hand.
        // Grips that can be held at any angle, like boxes and handles, will often be off. Fixed grips shouldn't be.
        public void LogCheck(Hand hand, string name)
        {
            try
            {
                Frame held = Frame.Of(Grip.GetNeutralTargetInWorld(hand)).Inverse().Then(Frame.Of(hand.transform));
                float angle = Quaternion.Angle(held.Rotation, _handInTarget.Rotation);
                float offset = (held.Position - _handInTarget.Position).magnitude * 100f;
                Mod.Trace($"Fit check for {name}: held hand is {angle:0} deg and {offset:0} cm from the prediction. " +
                            $"Hand relative to the grip target: held {held}, predicted {_handInTarget}");
            }
            catch (Exception e)
            {
                Mod.Trace($"Fit check for {name} failed: {FirstLine(e)}");
            }
        }

        private static string FirstLine(Exception e)
        {
            string message = e.Message ?? e.GetType().Name;
            int newline = message.IndexOf('\n');
            return newline >= 0 ? message.Substring(0, newline).Trim() : message.Trim();
        }
    }
}
