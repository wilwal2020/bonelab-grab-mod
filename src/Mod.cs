using System;
using System.Diagnostics;
using BoneLib;
using Il2CppSLZ.Marrow;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(GravityGloves.Mod), "Gravity Gloves", "1.0.1", "William")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace GravityGloves
{
    public class Mod : MelonMod
    {
        // An error in an update loop repeats every frame, so only the first few are logged.
        private const int MaxLoggedErrors = 5;

        internal static MelonLogger.Instance Log { get; private set; }

        // Testing lines for MelonLoader's log: what was locked, pulled, caught, put in a pocket and so on, for tuning
        // from William's tests. Only the Debug build (TESTLOG, see the .csproj) has them: the Release build that goes
        // on Thunderstore leaves the calls out, arguments and all, and logs only what went wrong.
        [Conditional("TESTLOG")]
        internal static void Trace(string message) => Log.Msg(message);

        [Conditional("TESTLOG")]
        internal static void TraceWarning(string message) => Log.Warning(message);

        private static GloveHand[] _gloves = Array.Empty<GloveHand>();

        private int _loggedErrors;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            Settings.Init();
            GameErrors.Hook();
            SettingsMenu.Create();
            _gloves = new[] { new GloveHand(isRight: false), new GloveHand(isRight: true) };
            Hooking.OnLevelLoaded += _ => ResetAll();
        }

        public override void OnUpdate()
        {
            // Before the gloves, so a hand reaching into a wrist pocket is left alone by its glove the same frame.
            try
            {
                WristPockets.Update();
            }
            catch (Exception e)
            {
                LogError(e);
            }

            foreach (GloveHand glove in _gloves)
            {
                try
                {
                    glove.Update();
                }
                catch (Exception e)
                {
                    glove.Reset();
                    LogError(e);
                }
            }

            try
            {
                GloveRecordings.Load();
                SpawnGunScreen.Update();
            }
            catch (Exception e)
            {
                LogError(e);
            }

            try
            {
                FlightSounds.Update();
                Transform head = Player.Head;
                Sparks.Update(head != null ? head.position : Vector3.zero);
            }
            catch (Exception e)
            {
                FlightSounds.Clear();
                Sparks.Clear();
                LogError(e);
            }
        }

        public override void OnFixedUpdate()
        {
            try
            {
                Flights.FixedUpdate();
                GrabAssist.FixedUpdate();
                AmmoBatch.FixedUpdate();
            }
            catch (Exception e)
            {
                Flights.Clear();
                GrabAssist.Clear();
                AmmoBatch.Clear();
                LogError(e);
            }

            // After grab assist, so it starts once the hand has grabbed what came out of a pocket.
            try
            {
                PassThrough.FixedUpdate();
            }
            catch (Exception e)
            {
                PassThrough.Clear();
                LogError(e);
            }
        }

        // True while a glove is using this hand, so BONELAB's built-in force pull should stay out of the way.
        internal static bool BlocksForcePull(Hand hand)
        {
            if (WristPockets.Blocks(hand))
                return true;
            foreach (GloveHand glove in _gloves)
            {
                if (glove.Blocks(hand))
                    return true;
            }
            return false;
        }

        private static void ResetAll()
        {
            foreach (GloveHand glove in _gloves)
                glove.Reset();
            Flights.Clear();
            GrabAssist.Clear();
            AmmoBatch.Clear();
            PassThrough.Clear();
            CullCheck.Clear();
            GameErrors.Summarize();
            WristPockets.Clear();
            TargetCache.Clear();
            FlightSounds.Clear();
            Sparks.Clear();
            GlowMaterials.Reset();
        }

        private void LogError(Exception e)
        {
            if (_loggedErrors++ < MaxLoggedErrors)
                Log.Error(e.ToString());
        }
    }
}
