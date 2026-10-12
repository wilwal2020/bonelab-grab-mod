using System;
using System.Collections.Generic;
using BoneLib;
using UnityEngine;

namespace GravityGloves
{
    // Plays the glove's sounds: William's recordings (GloveRecordings) for the lock, the hum, a lock let go and the
    // flick, and SoundSynth's for the select click, the catch thump and the whoosh of a flying object.
    internal static class Sounds
    {
        // How loud each one-shot plays, before the volume settings.
        private const float SelectLevel = 0.35f;
        private const float LockLevel = 0.5f;
        private const float ReleaseLevel = 0.5f;
        private const float FlickLevel = 0.6f;
        private const float CatchLevel = 0.6f;

        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();
        private static bool _loggedError;

        // The volume setting, or 0 with sound off.
        public static float Volume => Settings.Sound.Value ? Settings.Volume.Value / 100f : 0f;

        // The whoosh of a flying object, looped.
        public static AudioClip Whoosh => Clip("Whoosh", SoundSynth.Whoosh);

        public static void Play(FeedbackKind kind, Vector3 position)
        {
            switch (kind)
            {
                case FeedbackKind.Select:
                    PlayAt(Clip("Select", SoundSynth.Select), position, SelectLevel, 1f);
                    break;
                case FeedbackKind.Lock:
                    PlayRecording(SoundKind.Lock, position, LockLevel);
                    break;
                case FeedbackKind.Launch:
                    // The glove firing. The object's own whoosh is FlightSounds.
                    PlayRecording(SoundKind.Flick, position, FlickLevel);
                    break;
            }
        }

        // A pulled object landing in the hand. Heavier ones are louder and deeper.
        public static void PlayCatch(Vector3 position, float amount, float heaviness)
        {
            float volume = CatchLevel * amount * (1f + 0.5f * heaviness);
            float pitch = 1f / (1f + 0.35f * heaviness);
            PlayAt(Clip("Catch", SoundSynth.Catch), position, volume, pitch);
        }

        // A lock ending without a flick: the player let go, or the object got out of reach.
        public static void PlayRelease(Vector3 position) => PlayRecording(SoundKind.Release, position, ReleaseLevel);

        private static void PlayRecording(SoundKind kind, Vector3 position, float level)
        {
            RecordedSound sound = GloveRecordings.Pick(kind);
            if (sound != null)
                PlayAt(sound.Clip, position, level * sound.Volume, 1f);
        }

        public static void LogErrorOnce(Exception e)
        {
            if (_loggedError)
                return;
            _loggedError = true;
            Mod.Log.Error($"Couldn't play a sound: {e}");
        }

        private static void PlayAt(AudioClip clip, Vector3 position, float volume, float pitch)
        {
            volume *= Volume;
            if (volume <= 0f || clip == null)
                return;
            try
            {
                Audio.PlayAtPoint(clip, position, Audio.SoftInteraction, Mathf.Clamp01(volume), pitch);
            }
            catch (Exception e)
            {
                LogErrorOnce(e);
            }
        }

        // Each is made the first time it's needed.
        private static AudioClip Clip(string kind, Func<float[]> samples)
        {
            if (Clips.TryGetValue(kind, out AudioClip clip) && clip != null)
                return clip;
            float[] data = samples();
            clip = AudioClip.Create($"Gravity Gloves {kind}", data.Length, 1, SoundSynth.SampleRate, false);
            clip.SetData(data, 0);
            // Keep the clip through level loads, which unload unused assets.
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Clips[kind] = clip;
            return clip;
        }
    }
}
