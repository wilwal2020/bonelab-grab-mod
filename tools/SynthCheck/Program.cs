// Prints the sounds SoundSynth makes as JSON (length, peak, RMS and a sample every 100), for comparing with the
// line preview's JavaScript copy. Also writes them as WAV files for listening.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GravityGloves
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : ".";
            // Named by their place in the preview's lists, which still has every sound the mod had before v0.34.
            var sounds = new (string Name, Func<float[]> Make)[]
            {
                ("select6", SoundSynth.Select), ("catch0", SoundSynth.Catch), ("whoosh2", SoundSynth.Whoosh),
            };
            var json = new StringBuilder("{");
            bool first = true;
            foreach (var (name, make) in sounds)
            {
                float[] d = make();
                double peak = 0, sum = 0;
                foreach (float x in d) { peak = Math.Max(peak, Math.Abs(x)); sum += x * (double)x; }
                var picks = new List<string>();
                for (int i = 0; i < d.Length; i += 100) picks.Add(d[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                if (!first) json.Append(',');
                first = false;
                json.Append($"\"{name}\":{{\"n\":{d.Length},\"peak\":{peak.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},\"rms\":{Math.Sqrt(sum / d.Length).ToString("R", System.Globalization.CultureInfo.InvariantCulture)},\"s\":[{string.Join(",", picks)}]}}");
                WriteWav(Path.Combine(outDir, $"{name}.wav"), d);
            }
            json.Append('}');
            File.WriteAllText(Path.Combine(outDir, "csharp.json"), json.ToString());
            Console.WriteLine("wrote the sounds");
        }

        private static void WriteWav(string path, float[] data)
        {
            using var w = new BinaryWriter(File.Create(path));
            int bytes = data.Length * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(SoundSynth.SampleRate); w.Write(SoundSynth.SampleRate * 2);
            w.Write((short)2); w.Write((short)16); w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            foreach (float x in data) w.Write((short)Math.Clamp((int)Math.Round(x * 32767f), -32768, 32767));
        }
    }
}
