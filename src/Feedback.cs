using BoneLib;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace GravityGloves
{
    internal enum FeedbackKind
    {
        Select,
        Lock,
        Launch,
    }

    // Haptics and sounds for selecting, locking, launching and catching, and the buzz while locked.
    internal static class Feedback
    {
        private const float MinPulse = 0.01f;

        // The buzz while locked is a stream of short pulses that overlap a little, so it doesn't stutter.
        private const float BuzzInterval = 0.05f;

        // One catch per hand at a time, so a batch of ammo landing together gives one thump.
        private const float CatchSpacing = 0.15f;

        private static readonly float[] NextBuzz = new float[2];
        private static readonly float[] NextCatch = new float[2];
        private static bool _loggedHaptics;

        private static float HapticStrength => Settings.HapticStrength.Value / 100f;

        public static void Play(FeedbackKind kind, BaseController controller, Vector3 position)
        {
            PlayHaptic(kind, controller);
            Sounds.Play(kind, position);
        }

        // A light tick, with no sound, as something held comes up to a wrist pocket.
        public static void Tick(BaseController controller)
        {
            Haptor haptor = controller != null ? controller.haptor : null;
            if (haptor != null && HapticStrength > 0f)
                Send(haptor, haptor.hap_tap_duration * 0.5f, haptor.hap_tap_frequency, haptor.hap_subtle_amplitude * HapticStrength);
        }

        // A pulled object coming close enough to grab: a tap as strong as the game's own, for the tick's length. Until
        // v0.34 it was 0.03 s at 0.4 of that, and William found it too weak and short.
        public static void ReachTick(BaseController controller)
        {
            Haptor haptor = controller != null ? controller.haptor : null;
            float strength = HapticStrength * Settings.ReachTick.Value / 100f;
            if (haptor != null && strength > 0f)
                Send(haptor, Settings.ReachTickLength.Value / 1000f, haptor.hap_tap_frequency, haptor.hap_tap_amplitude * strength);
        }

        // A slight, steady hum while something is at a wrist pocket, or a hand reaches into a full one.
        public static void PocketHum(int hand, BaseController controller)
        {
            float strength = HapticStrength * Settings.PocketHum.Value / 100f;
            float now = Time.unscaledTime;
            if (strength <= 0f || controller == null || now < NextBuzz[hand])
                return;
            Haptor haptor = controller.haptor;
            if (haptor == null)
                return;
            NextBuzz[hand] = now + BuzzInterval;
            Send(haptor, BuzzInterval * 1.6f, 60f, 0.035f * strength);
        }

        // A light vibration while a glove holds a lock, stronger the harder the player squeezes (0 to 1).
        public static void Buzz(int hand, BaseController controller, float squeeze)
        {
            float strength = HapticStrength * Settings.LockVibration.Value / 100f;
            float now = Time.unscaledTime;
            if (strength <= 0f || controller == null || now < NextBuzz[hand])
                return;
            Haptor haptor = controller.haptor;
            if (haptor == null)
                return;
            NextBuzz[hand] = now + BuzzInterval;
            Send(haptor, BuzzInterval * 1.6f, 80f + 60f * squeeze, (0.02f + 0.1f * squeeze) * strength);
        }

        // A pulled object landing in a hand. Heavy objects land harder, deeper and longer.
        public static void Catch(Hand hand, Target target)
        {
            float amount = Settings.CatchThump.Value / 100f;
            if (hand == null || amount <= 0f)
                return;
            int index = IndexOf(hand);
            float now = Time.unscaledTime;
            if (now < NextCatch[index])
                return;
            NextCatch[index] = now + CatchSpacing;

            float heaviness = Heaviness.Of(target.Mass);
            BaseController controller = hand.Controller;
            Haptor haptor = controller != null ? controller.haptor : null;
            float strength = HapticStrength * amount;
            if (haptor != null && strength > 0f)
            {
                Send(haptor, 0.07f + 0.1f * heaviness, haptor.hap_knock_frequency / (1f + 0.4f * heaviness),
                    (0.45f + 0.4f * heaviness) * strength);
            }
            Sounds.PlayCatch(GloveHand.PalmPosition(hand), amount, heaviness);
        }

        private static void PlayHaptic(FeedbackKind kind, BaseController controller)
        {
            float strength = HapticStrength;
            Haptor haptor = controller.haptor;
            if (strength <= 0f || haptor == null)
                return;
            LogPresetsOnce(haptor);

            // The game's own haptic styles, scaled by the strength setting.
            switch (kind)
            {
                case FeedbackKind.Select:
                    Send(haptor, haptor.hap_tap_duration * 0.5f, haptor.hap_tap_frequency,
                        haptor.hap_subtle_amplitude * strength);
                    break;
                case FeedbackKind.Lock:
                    Send(haptor, haptor.hap_tap_duration, haptor.hap_tap_frequency, haptor.hap_tap_amplitude * strength);
                    break;
                default:
                    Send(haptor, haptor.hap_knock_duration, haptor.hap_knock_frequency,
                        haptor.hap_knock_amplitude * strength);
                    break;
            }
        }

        private static int IndexOf(Hand hand)
        {
            Hand right = Player.RightHand;
            return right != null && right.Pointer == hand.Pointer ? 1 : 0;
        }

        private static void Send(Haptor haptor, float duration, float frequency, float amplitude) =>
            haptor.SENDHAPTIC(0f, Mathf.Max(duration, MinPulse), frequency, Mathf.Clamp01(amplitude));

        // The units of the game's haptic values aren't documented, so log them once for tuning.
        private static void LogPresetsOnce(Haptor haptor)
        {
            if (_loggedHaptics)
                return;
            _loggedHaptics = true;
            Mod.Trace($"Haptic presets: tap {haptor.hap_tap_duration} s / {haptor.hap_tap_frequency} / " +
                        $"{haptor.hap_tap_amplitude}, subtle amplitude {haptor.hap_subtle_amplitude}, knock " +
                        $"{haptor.hap_knock_duration} s / {haptor.hap_knock_frequency} / {haptor.hap_knock_amplitude}");
        }
    }
}
