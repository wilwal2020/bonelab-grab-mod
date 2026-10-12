using System;
using BoneLib;
using UnityEngine;

namespace GravityGloves
{
    // How big the player's avatar is compared with a regular one. With "Scale with avatar" on, the distance
    // settings are multiplied by it, so the reach and catch feel the same on a tiny or a giant avatar.
    internal static class BodyScale
    {
        // Roughly a regular adult avatar, which every distance setting was tuned on. The log shows the real
        // height whenever it changes, to check this against.
        private const float ReferenceHeight = 1.76f;

        // Reading the avatar is cheap, but it only changes when the player swaps avatars.
        private const float CheckInterval = 1f;

        private static float _scale = 1f;
        private static float _loggedHeight;
        private static float _nextCheck;

        // The multiplier for distances: the avatar's scale with the setting on, otherwise 1.
        public static float Value
        {
            get
            {
                Refresh();
                return Settings.ScaleWithAvatar.Value ? _scale : 1f;
            }
        }

        // The avatar's scale whatever the setting, for things on the body itself, like the wrist pockets: on a small
        // avatar a pocket set off a fixed distance from the wrist ended up over the body log (William, v0.27).
        public static float Avatar
        {
            get
            {
                Refresh();
                return _scale;
            }
        }

        private static void Refresh()
        {
            float now = Time.unscaledTime;
            if (now < _nextCheck)
                return;
            _nextCheck = now + CheckInterval;
            Read();
        }

        private static void Read()
        {
            float height;
            try
            {
                Il2CppSLZ.VRMK.Avatar avatar = Player.Avatar;
                if (avatar == null)
                    return;
                height = avatar.height;
            }
            catch (Exception)
            {
                // No rig yet, while a level loads.
                return;
            }
            if (float.IsNaN(height) || height < 0.1f)
                return;

            _scale = Mathf.Clamp(height / ReferenceHeight, 0.1f, 20f);
            if (Mathf.Abs(height - _loggedHeight) > 0.01f)
            {
                _loggedHeight = height;
                Mod.Trace($"Avatar is {height:0.00} m tall, so with Scale with avatar on, distances are " +
                            $"{_scale:0.00}x (currently {(Settings.ScaleWithAvatar.Value ? "on" : "off")})");
            }
        }
    }
}
