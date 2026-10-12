using System;
using System.IO;

namespace GravityGloves
{
    // The glove's own sounds: the select click, the catch thump and the whoosh of a flying object. The lock, the hum,
    // a lock let go and the flick are William's recordings (GloveRecordings). There's no Unity in here, so
    // tools/SynthCheck can run it outside the game and compare it with the line preview's JavaScript copy
    // (tools/line-preview-widget.html): keep the two in step.
    //
    // Until v0.34 there were 8 select, 11 lock, 6 hum, 12 flick, 6 catch and 6 whoosh sounds to choose from on a
    // "Sound choices" page. William asked to keep only the ones in use: RankerClick, Thump and Hum. The select click
    // is a recording from his image ranker (src/Recordings, Kenney's CC0 "click-ui").
    internal static class SoundSynth
    {
        public const int SampleRate = 44100;

        private const float Pi = MathF.PI;

        // The image ranker's click (RankerClick).
        public static float[] Select() => Loudness(Recording("ranker-click-ui"), 0.23f);

        // A low thump with a soft click (Thump): a pulled object landing in the hand.
        public static float[] Catch()
        {
            var noise = new Noise(11);
            float click = 0f;
            float phase = 0f;
            float smoothing = LowPass(2500f);
            return Generate(300, t =>
            {
                phase += 2f * Pi * (55f + 45f * Decay(t, 30f)) / SampleRate;
                click += smoothing * (noise.Next() - click);
                return 0.85f * MathF.Sin(phase) * Rise(t, 0.003f) * Decay(t, 14f) + 0.6f * click * Decay(t, 160f);
            });
        }

        // Low rushing air with a faint low tone (Hum): a flying object. A one-second loop.
        public static float[] Whoosh()
        {
            float[] air = BandNoiseLoop(3, 150f, 1200f);
            int i = 0;
            return Generate(1000, t => 0.55f * air[i++] + 0.25f * Wave(98f, t) + 0.15f * Wave(147f, t));
        }

        // Noise between two frequencies, one second long, with its end crossfaded into its start so it loops
        // without a click. It peaks at 0.7.
        private static float[] BandNoiseLoop(uint seed, float lowCut, float highCut)
        {
            int length = SampleRate;
            int fade = SampleRate / 10;
            var noise = new float[length + fade];
            var random = new Noise(seed);
            float high = LowPass(highCut);
            float low = LowPass(lowCut);
            float low1 = 0f, low2 = 0f, rumble = 0f, peak = 0f;
            for (int i = 0; i < noise.Length; i++)
            {
                low1 += high * (random.Next() - low1);
                low2 += high * (low1 - low2);
                rumble += low * (low2 - rumble);
                noise[i] = low2 - rumble;
                peak = Math.Max(peak, Math.Abs(noise[i]));
            }

            var data = new float[length];
            float gain = peak > 0f ? 0.7f / peak : 0f;
            for (int i = 0; i < length; i++)
            {
                float sample = noise[i];
                if (i < fade)
                {
                    float into = i / (float)fade;
                    sample = noise[i] * MathF.Sqrt(into) + noise[length + i] * MathF.Sqrt(1f - into);
                }
                data[i] = sample * gain;
            }
            return data;
        }

        // A recording built into the mod (src/Recordings): 16-bit mono WAV at the sample rate, with the standard
        // 44-byte header.
        private static float[] Recording(string name)
        {
            using (Stream stream = typeof(SoundSynth).Assembly.GetManifestResourceStream($"GravityGloves.Recordings.{name}.wav"))
            {
                if (stream == null)
                    return new float[1];
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int got = stream.Read(bytes, read, bytes.Length - read);
                    if (got <= 0)
                        break;
                    read += got;
                }
                var data = new float[Math.Max(1, (read - 44) / 2)];
                for (int i = 0; i < data.Length && 44 + 2 * i + 1 < read; i++)
                    data[i] = BitConverter.ToInt16(bytes, 44 + 2 * i) / 32767f;
                return data;
            }
        }

        // Scales a sound so its loudest 50 ms has the given RMS, which is closer to how loud it sounds than its peak,
        // without letting the peak pass 0.95.
        private static float[] Loudness(float[] data, float target)
        {
            int window = SampleRate / 20;
            float loudest = 0f;
            float peak = 0f;
            for (int start = 0; start + window <= data.Length; start += 220)
            {
                double energy = 0;
                for (int i = start; i < start + window; i++)
                    energy += data[i] * (double)data[i];
                loudest = Math.Max(loudest, (float)Math.Sqrt(energy / window));
            }
            foreach (float sample in data)
                peak = Math.Max(peak, Math.Abs(sample));
            if (loudest <= 0f || peak <= 0f)
                return data;
            float gain = Math.Min(target / loudest, 0.95f / peak);
            for (int i = 0; i < data.Length; i++)
                data[i] *= gain;
            return data;
        }

        private static float[] Generate(int milliseconds, Func<float, float> sample)
        {
            var data = new float[milliseconds * SampleRate / 1000];
            for (int i = 0; i < data.Length; i++)
                data[i] = sample(i / (float)SampleRate);
            return data;
        }

        private static float Wave(float hertz, float t) => MathF.Sin(2f * Pi * hertz * t);

        // Rises from 0 to 1 over the given time, so a sound doesn't start with a click.
        private static float Rise(float t, float time) => Math.Min(t / time, 1f);

        private static float Decay(float t, float rate) => MathF.Exp(-t * rate);

        // The smoothing factor of a one-pole low-pass filter at the given cutoff.
        private static float LowPass(float cutoff) => 1f - MathF.Exp(-2f * Pi * cutoff / SampleRate);

        // A tiny random number generator (xorshift). The preview has the same one, so its noise is the mod's.
        private sealed class Noise
        {
            private uint _state;

            public Noise(uint seed)
            {
                _state = seed;
            }

            // From -1 to 1.
            public float Next()
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return (float)(_state / 4294967296.0 * 2.0 - 1.0);
            }
        }
    }
}
