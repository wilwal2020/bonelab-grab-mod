using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace GravityGloves
{
    // Test build only: BONELAB's own errors and exceptions, copied into MelonLoader's log with the time, so they can
    // be matched with what the mod did just before (Player.log has no times). Each kind (message and top frame) is
    // logged the first time it happens in a level, and a level change says how often each repeated. William found
    // some modded guns broke after a while in the v1.0.0 release test, where Player.log had 17722 errors from
    // GunTrigger.Update and others, with nothing to say which gun or when.
    internal static class GameErrors
    {
        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
        private static bool _hooked;

        [Conditional("TESTLOG")]
        public static void Hook()
        {
            if (_hooked)
                return;
            _hooked = true;
            try
            {
                Application.add_logMessageReceived(
                    DelegateSupport.ConvertDelegate<Application.LogCallback>(new Action<string, string, LogType>(OnLog)));
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"Couldn't listen to the game's errors: {e.Message}");
            }
        }

        // At a change of level: how often each kind of error repeated in the last one.
        [Conditional("TESTLOG")]
        public static void Summarize()
        {
            foreach (var pair in Counts)
            {
                if (pair.Value > 1)
                    Mod.Log.Msg($"Game error repeated {pair.Value} times in that level: {pair.Key}");
            }
            Counts.Clear();
        }

        private static void OnLog(string message, string stackTrace, LogType type)
        {
            try
            {
                if (type != LogType.Exception && type != LogType.Error)
                    return;
                string key = $"{FirstLine(message)} at {TopFrame(stackTrace)}";
                Counts.TryGetValue(key, out int count);
                Counts[key] = count + 1;
                if (count == 0)
                    Mod.Log.Warning($"Game error: {key}");
            }
            catch (Exception)
            {
                // Never let the log listener throw back into the game's logging.
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "(no message)";
            int newline = text.IndexOf('\n');
            return (newline >= 0 ? text.Substring(0, newline) : text).Trim();
        }

        // The innermost frames, which say which script threw.
        private static string TopFrame(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace))
                return "(no stack)";
            var frames = new List<string>();
            foreach (string line in stackTrace.Split('\n'))
            {
                string frame = line.Trim();
                if (frame.Length == 0)
                    continue;
                if (frame.StartsWith("at "))
                    frame = frame.Substring(3);
                int args = frame.IndexOf(" [0x", StringComparison.Ordinal);
                if (args > 0)
                    frame = frame.Substring(0, args);
                frames.Add(frame);
                if (frames.Count == 3)
                    break;
            }
            return string.Join(" < ", frames);
        }
    }
}
