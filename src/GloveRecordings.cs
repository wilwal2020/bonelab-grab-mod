using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GravityGloves
{
    // The moments the glove makes a sound.
    internal enum SoundKind
    {
        Select,
        Lock,
        Hum,
        Release,
        Flick,
        Catch,
        Whoosh,
    }

    // One recording, loaded as a mono clip, and how loud it plays.
    internal sealed class RecordedSound
    {
        public string Name;
        public AudioClip Clip;

        // Its moment's volume, times how much louder it plays than its samples when levelling wanted more gain than
        // its peaks allowed.
        public float Volume = 1f;
    }

    // William's recordings for the lock, the hum while locked, a lock let go without a flick, and the flick (three, a
    // random one each time, never the same one twice in a row), built into the mod from src/Recordings.
    //
    // Until v0.34 these were his custom sounds in UserData/GravityGloves/Sounds, where anyone could put their own
    // files for any moment, with a page for each moment, a volume for each file and a choice of built-in sounds. He
    // asked for one sound page with only the sounds in use. They're levelled and played as they were then, with
    // "Even out levels" and "Loops start anywhere" on, at his volumes: the lock at 40%, the rest at 100%.
    internal static class GloveRecordings
    {
        // A nearly silent recording isn't played more than this much louder, so it can't turn into loud noise.
        private const float MaxLevelBoost = 8f;

        // Each moment's recordings, the loudness they're levelled to (that of the glove's built-in sounds, which
        // SoundSynth levels the same way), and its volume. Release has no built-in sound, so it's levelled like the
        // lock.
        private static readonly (SoundKind Kind, float Loudness, float Volume, string[] Files)[] Moments =
        {
            (SoundKind.Lock, 0.27f, 0.4f, new[] { "glove-lock" }),
            (SoundKind.Hum, 0.28f, 1f, new[] { "glove-hum" }),
            (SoundKind.Release, 0.27f, 1f, new[] { "glove-release" }),
            (SoundKind.Flick, 0.3f, 1f, new[] { "glove-flick-1", "glove-flick-2", "glove-flick-3" }),
        };

        private static readonly Dictionary<SoundKind, List<RecordedSound>> Loaded = new Dictionary<SoundKind, List<RecordedSound>>();
        private static readonly Dictionary<SoundKind, RecordedSound> LastPicked = new Dictionary<SoundKind, RecordedSound>();
        private static readonly System.Random Variety = new System.Random();
        private static bool _loaded;

        // Makes the clips the first time it's called, from the mod's update, once Unity is ready.
        public static void Load()
        {
            if (_loaded)
                return;
            _loaded = true;
            var problems = new List<string>();
            foreach (var moment in Moments)
            {
                var sounds = new List<RecordedSound>();
                foreach (string file in moment.Files)
                {
                    try
                    {
                        RecordedSound sound = Read(moment.Kind, file, moment.Loudness, moment.Volume, out string problem);
                        if (sound != null)
                            sounds.Add(sound);
                        else
                            problems.Add($"{file}: {problem}");
                    }
                    catch (Exception e)
                    {
                        problems.Add($"{file}: {e.Message}");
                    }
                }
                Loaded[moment.Kind] = sounds;
            }
            foreach (string problem in problems)
                Mod.Log.Warning($"Couldn't load the recording {problem}");
        }

        // A random recording for the moment, never the one picked last time when there are several, or null if it
        // has none.
        public static RecordedSound Pick(SoundKind kind)
        {
            if (!Loaded.TryGetValue(kind, out List<RecordedSound> sounds) || sounds.Count == 0)
                return null;
            LastPicked.TryGetValue(kind, out RecordedSound last);
            bool skipLast = sounds.Count > 1 && last != null;
            int pick = Variety.Next(skipLast ? sounds.Count - 1 : sounds.Count);
            foreach (RecordedSound sound in sounds)
            {
                if (skipLast && sound == last)
                    continue;
                if (pick-- == 0)
                {
                    LastPicked[kind] = sound;
                    return sound;
                }
            }
            return null;
        }

        // Reads one recording into a mono clip that survives level loads, levelled to its moment.
        private static RecordedSound Read(SoundKind kind, string name, float loudness, float volume, out string problem)
        {
            byte[] bytes;
            using (Stream stream = typeof(GloveRecordings).Assembly.GetManifestResourceStream($"GravityGloves.Recordings.{name}.wav"))
            {
                if (stream == null)
                {
                    problem = "it isn't in the mod";
                    return null;
                }
                bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int got = stream.Read(bytes, read, bytes.Length - read);
                    if (got <= 0)
                        break;
                    read += got;
                }
            }
            float[] mono = WavFile.ReadMono(bytes, out int rate, out problem);
            if (mono == null)
                return null;
            if (mono.Length == 0)
            {
                problem = "it has no sound in it";
                return null;
            }
            bool loop = kind == SoundKind.Hum;
            float boost = Level(mono, loudness, rate, loop);

            AudioClip clip = AudioClip.Create($"Gravity Gloves {name}", mono.Length, 1, rate, false);
            clip.SetData(mono, 0);
            // Keep the clip through level loads, which unload unused assets.
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return new RecordedSound { Name = name, Clip = clip, Volume = volume * Math.Min(boost, MaxLevelBoost) };
        }

        // Brings a sound to the given loudness, measured in 50 ms windows: by its loudest window for one-shots, as
        // SoundSynth does, and by its typical (median) one for loops, which play for as long as they're held.
        // Levelled by its loudest moment, the 13 s hum, which starts 16-21 dB below its peak, came out nearly silent
        // during a lock (v0.19.2). The samples are scaled as far as their peak allows (0.95), and the gain still
        // missing is returned, to play the sound louder instead. A clip shorter than one window is measured whole.
        private static float Level(float[] data, float target, int rate, bool typical)
        {
            int window = Math.Max(1, Math.Min(rate / 20, data.Length));
            int step = typical ? window : Math.Max(1, rate / 200);
            var levels = new List<float>();
            for (int start = 0; start + window <= data.Length; start += step)
            {
                double energy = 0;
                for (int i = start; i < start + window; i++)
                    energy += data[i] * (double)data[i];
                levels.Add((float)Math.Sqrt(energy / window));
            }
            float peak = 0f;
            foreach (float sample in data)
                peak = Math.Max(peak, Math.Abs(sample));
            if (levels.Count == 0 || peak <= 0f)
                return 1f;
            levels.Sort();
            float level = typical ? levels[levels.Count / 2] : levels[levels.Count - 1];
            if (level <= 0f)
                return 1f;

            float wanted = target / level;
            float applied = Math.Min(wanted, 0.95f / peak);
            for (int i = 0; i < data.Length; i++)
                data[i] *= applied;
            return wanted / applied;
        }
    }
}
