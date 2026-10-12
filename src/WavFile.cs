using System;
using System.Text;

namespace GravityGloves
{
    // Reads the WAV recordings built into the mod (GloveRecordings). BONELAB's Unity build is missing the code Unity
    // decodes audio files with (DownloadHandlerAudioClip's constructor was stripped, so
    // UnityWebRequestMultimedia.GetAudioClip fails with "Method not found"), so the mod reads them itself. No Unity in here, so tools can test it.
    internal static class WavFile
    {
        private const int Pcm = 1;
        private const int Float = 3;
        private const int Extensible = 0xFFFE;

        // The file's samples mixed down to mono, the channels averaged. It reads integer PCM (8, 16, 24 and 32 bit)
        // and float (32 and 64 bit), plain or in the extensible format. Returns null with the reason if it can't.
        public static float[] ReadMono(byte[] file, out int sampleRate, out string problem)
        {
            sampleRate = 0;
            problem = null;
            if (file.Length < 12 || Ascii(file, 0) != "RIFF" || Ascii(file, 8) != "WAVE")
            {
                problem = "it isn't a WAV file inside";
                return null;
            }

            int format = 0, channels = 0, bits = 0, blockAlign = 0;
            int dataStart = -1, dataLength = 0;
            int at = 12;
            while (at + 8 <= file.Length)
            {
                string id = Ascii(file, at);
                int body = at + 8;
                // Some programs write the size as 0 or 0xFFFFFFFF when they stream, so a size past the end means
                // "to the end of the file".
                long size = BitConverter.ToUInt32(file, at + 4);
                int length = (int)Math.Min(size, file.Length - body);
                if (id == "fmt " && length >= 16)
                {
                    format = BitConverter.ToUInt16(file, body);
                    channels = BitConverter.ToUInt16(file, body + 2);
                    sampleRate = BitConverter.ToInt32(file, body + 4);
                    blockAlign = BitConverter.ToUInt16(file, body + 12);
                    bits = BitConverter.ToUInt16(file, body + 14);
                    // The extensible format names the real one in the first two bytes of its sub-format GUID.
                    if (format == Extensible && length >= 26)
                        format = BitConverter.ToUInt16(file, body + 24);
                }
                else if (id == "data")
                {
                    dataStart = body;
                    dataLength = size == 0 ? file.Length - body : length;
                }
                at = body + length + (length & 1);
            }

            if (format == 0 || channels <= 0 || sampleRate <= 0)
            {
                problem = "its format chunk is missing";
                return null;
            }
            if (dataStart < 0)
            {
                problem = "it has no sound data";
                return null;
            }
            int bytesPerSample = bits / 8;
            bool supported = format == Pcm && (bits == 8 || bits == 16 || bits == 24 || bits == 32) ||
                             format == Float && (bits == 32 || bits == 64);
            if (!supported)
            {
                problem = $"it's {FormatName(format)} at {bits} bit, which the mod can't read. Save it as 16-bit PCM WAV.";
                return null;
            }
            if (blockAlign < channels * bytesPerSample)
                blockAlign = channels * bytesPerSample;

            int frames = dataLength / blockAlign;
            var mono = new float[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                int start = dataStart + frame * blockAlign;
                float sum = 0f;
                for (int channel = 0; channel < channels; channel++)
                    sum += Sample(file, start + channel * bytesPerSample, format, bits);
                mono[frame] = sum / channels;
            }
            return mono;
        }

        private static float Sample(byte[] file, int at, int format, int bits)
        {
            if (format == Float)
                return bits == 32 ? BitConverter.ToSingle(file, at) : (float)BitConverter.ToDouble(file, at);
            switch (bits)
            {
                case 8:
                    // 8-bit WAV is unsigned, centred on 128.
                    return (file[at] - 128) / 128f;
                case 16:
                    return BitConverter.ToInt16(file, at) / 32768f;
                case 24:
                    return ((file[at] | file[at + 1] << 8 | (sbyte)file[at + 2] << 16) / 8388608f);
                default:
                    return BitConverter.ToInt32(file, at) / 2147483648f;
            }
        }

        private static string FormatName(int format)
        {
            switch (format)
            {
                case Pcm:
                    return "PCM";
                case Float:
                    return "float";
                case 2:
                    return "MS ADPCM";
                case 0x11:
                    return "IMA ADPCM";
                case 0x55:
                    return "MP3 in a WAV file";
                default:
                    return $"format {format}";
            }
        }

        private static string Ascii(byte[] file, int at) => Encoding.ASCII.GetString(file, at, 4);
    }
}
