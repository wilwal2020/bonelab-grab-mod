using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace GravityGloves
{
    // The beam from the selected object toward the hand, as in Half-Life: Alyx. It grows out of the object
    // and stops partway to the hand, fading out at its tip. A soft glow around a bright core (Alyx orange
    // unless the colour setting changes it), with a flare where it leaves the object. It tightens and brightens
    // when locked, gets stronger the harder the player squeezes, flashes on select and lock, and shoots back into
    // the object on the flick. It can also shed sparks. Thickness and opacity are settings, which William tuned in
    // a preview of this line.
    internal sealed class AimLine
    {
        private const int Points = 32;
        private const float FadeInTime = 0.12f;
        private const float FadeOutTime = 0.15f;
        private const float FlashTime = 0.3f;

        // How long the beam takes to grow out of the object to its full length.
        private const float GrowTime = 0.15f;

        // Smooths the squeeze, so a trembling finger doesn't make the beam flicker.
        private const float SqueezeSmoothing = 0.05f;

        // Shorter than this, the beam is just the flare.
        private const float MinReach = 0.002f;

        // With the flare at 0%, the beam starts at this share of its strength and fades in over this share of
        // its length. Rounded ends come back at 50%.
        private const float StartFade = 0.2f;
        private const float FadeInEnd = 0.15f;
        private const int CapVertices = 4;

        // At 100% line stiffness the line stays straight until the hand points this far away from the object.
        private const float MaxStiffAngle = 45f;

        private static Material _flareMaterial;
        private static Material _lightMaterial;
        private static bool _lightUnavailable;
        private static AnimationCurve _beamWidth;

        private readonly Il2CppStructArray<Vector3> _points = new Il2CppStructArray<Vector3>(Points);
        private readonly LineColors _glowColors = new LineColors();
        private readonly LineColors _coreColors = new LineColors();
        private readonly LineColors _lightColors = new LineColors();
        private readonly LineColors _hotColors = new LineColors();
        private Material _beamMaterial;
        private LineRenderer _glow;
        private LineRenderer _core;
        private LineRenderer _flare;

        // Light added over the line, with the same additive material as the object glow: a wider soft layer and a
        // narrow bright one, which together fall off toward the edges. The plain line is only blended over what's
        // behind it, and looked dim next to the glowing object (William, v0.26).
        private LineRenderer _light;
        private LineRenderer _hot;
        private bool _unavailable;

        private float _visibility;
        private float _flash;
        private float _reach;
        private float _squeeze = 1f;
        private bool _retracting;
        private float _retractRate;
        private bool _locked;
        private Vector3 _source;

        // Sparks owed by the emission rate, so fractions carry over between frames.
        private float _sparkDebt;

        // Whether any of the beam is on screen, including while it fades or retracts.
        public bool IsShowing => _visibility > 0f;

        // A brief brightening, stronger for bigger values. Used on select and lock, and as the beam goes back in.
        public void Flash(float strength) => _flash = Mathf.Max(_flash, strength);

        // Call every frame. Source is where the beam leaves the object, while there's an object to follow.
        // Squeeze is how hard the player grips, from 0 (just holding the lock) to 1 (a full squeeze). When it
        // stops being visible the beam fades out, or after Retract, shoots back into the object first.
        public void Update(bool visible, Vector3 hand, Vector3 aim, Vector3? source, bool locked, Vector3 eye,
            float squeeze)
        {
            float dt = Time.unscaledDeltaTime;
            if (source.HasValue)
                _source = source.Value;
            if (visible)
            {
                _locked = locked;
                _retracting = false;
            }
            _squeeze += (Mathf.Clamp01(squeeze) - _squeeze) * (1f - Mathf.Exp(-dt / SqueezeSmoothing));

            if (_retracting)
            {
                _reach = Mathf.MoveTowards(_reach, 0f, dt * _retractRate);
                if (_reach <= 0f)
                {
                    // In: the flare pops as the beam disappears into the object, then everything fades.
                    _retracting = false;
                    Flash(1f);
                }
            }
            else if (visible)
            {
                // Each time it appears, the beam grows out of the object again.
                if (_visibility <= 0f)
                    _reach = 0f;
                _reach = Mathf.MoveTowards(_reach, Settings.BeamLength.Value / 100f, dt / GrowTime);
            }
            bool showing = visible || _retracting;
            _visibility = Mathf.MoveTowards(_visibility, showing ? 1f : 0f, dt / (showing ? FadeInTime : FadeOutTime));
            _flash = Mathf.MoveTowards(_flash, 0f, dt / FlashTime);
            if (_visibility <= 0f)
            {
                SetEnabled(false);
                return;
            }
            if (!EnsureCreated())
                return;

            float distance = Vector3.Distance(hand, _source);
            float time = Time.unscaledTime;
            float flash = 1f + 0.6f * _flash;
            float scale = 1f + 0.05f * distance;
            float thickness = Settings.LineThickness.Value / 100f;
            float opacity = Settings.LineOpacity.Value / 100f;
            // Applied after the opacity is capped, so squeezing still shows on a very solid beam.
            float grip = 1f - Settings.LineGripResponse.Value / 100f * (1f - _squeeze);

            // The flare setting is the bright spot where the beam leaves the object. Seen from the player, the beam
            // points almost straight at them, so a solid round start looks like a blob on the object, even with
            // the flare sprite off (William). Below 100% the start fades in instead, and below 50% it loses its
            // rounded end.
            float flareSize = Settings.LineFlare.Value / 100f;
            float startShare = Mathf.Lerp(StartFade, 1f, Mathf.Clamp01(flareSize));
            int caps = flareSize >= 0.5f ? CapVertices : 0;

            if (_reach > MinReach)
            {
                DrawBeam(hand, aim, distance, eye, time);
                _glow.widthMultiplier = (_locked ? 0.09f : 0.06f) * scale * flash * thickness;
                _core.widthMultiplier = (_locked ? 0.022f : 0.014f) * scale * (1f + 0.3f * _flash) * thickness;
                _glow.numCapVertices = caps;
                _core.numCapVertices = caps;
                SetColors(_glow, _glowColors, Palette.Apply(new Color(1f, 0.5f, 0.1f,
                    Mathf.Clamp01((_locked ? 0.55f : 0.3f) * _visibility * flash * opacity) * grip)), startShare);
                SetColors(_core, _coreColors, Palette.Apply(new Color(1f, 0.85f, 0.6f,
                    Mathf.Clamp01((_locked ? 1f : 0.65f) * _visibility * flash * opacity) * grip)), startShare);
                DrawLight(scale, flash, thickness, grip, caps, startShare);

                // Sparks drift off the beam toward the hand, more of them the harder the player squeezes.
                EmitSparks(dt, visible ? Sparks.LineSparkRate(_locked, _squeeze) : 0f);
            }
            else
            {
                _glow.enabled = false;
                _core.enabled = false;
                SetLightEnabled(false);
            }

            // The flare sprite where the beam leaves the object throbs while locked, and swells with the squeeze. It
            // doesn't grow with the line's thickness, since that made a big blob (William).
            if (flareSize > 0f)
            {
                float throb = _locked ? 1f + 0.12f * Mathf.Sin(time * 14f) : 1f;
                Vector3 towardHand = distance > 0.01f ? (hand - _source) / distance : Vector3.zero;
                DrawFlare(_flare, _source + towardHand * 0.01f,
                    (_locked ? 0.18f : 0.12f) * scale * flash * throb * flareSize * (0.8f + 0.2f * grip),
                    Mathf.Clamp01((_locked ? 0.8f : 0.5f) * _visibility * opacity) * grip, eye);
            }
            else
            {
                _flare.enabled = false;
            }
        }

        // Shoots the beam back into the object over the given time, for the flick. With no time, or no beam
        // showing, it just goes.
        public void Retract(float time)
        {
            if (time <= 0f || _visibility <= 0f || _reach <= MinReach)
            {
                Hide();
                return;
            }
            _retracting = true;
            _retractRate = _reach / time;
        }

        public void Hide()
        {
            _visibility = 0f;
            _flash = 0f;
            _retracting = false;
            SetEnabled(false);
        }

        // The end of the curve from the hand to the object, drawn from the object back toward the hand as far
        // as the beam reaches. It has a slight waver, strongest in the middle and sideways as seen from the eye.
        //
        // The line is stiff: it heads out of the hand straight at the object until the hand points more than the
        // stiffness share of MaxStiffAngle away, and only bends by however much further it points. In Half-Life:
        // Alyx it stays straight until the hand points well away (William).
        private void DrawBeam(Vector3 hand, Vector3 aim, float distance, Vector3 eye, float time)
        {
            Vector3 toSource = distance > 1e-4f ? (_source - hand) / distance : aim;
            float straightFor = Settings.LineStiffness.Value / 100f * MaxStiffAngle;
            float pointedAway = Vector3.Angle(toSource, aim);
            Vector3 bentAim = pointedAway > straightFor
                ? Vector3.RotateTowards(toSource, aim, (pointedAway - straightFor) * Mathf.Deg2Rad, 0f)
                : toSource;
            Vector3 control = hand + bentAim * (distance * 0.45f);
            Vector3 side = Vector3.Cross(_source - hand, (hand + _source) * 0.5f - eye);
            side = side.sqrMagnitude > 1e-8f ? side.normalized : Vector3.up;
            float waver = (_locked ? 0.0015f : 0.004f) + distance * (_locked ? 0.0003f : 0.0008f);

            for (int i = 0; i < Points; i++)
            {
                float along = i / (float)(Points - 1);
                float t = 1f - along * _reach;
                float u = 1f - t;
                Vector3 point = u * u * hand + 2f * u * t * control + t * t * _source;
                float wave = 0.6f * Mathf.Sin(2f * Mathf.PI * (2.5f * t + 1.1f * time)) +
                             0.4f * Mathf.Sin(2f * Mathf.PI * (4.3f * t - 0.7f * time));
                _points[i] = point + side * (waver * Mathf.Sin(Mathf.PI * along) * wave);
            }
            _glow.SetPositions(_points);
            _core.SetPositions(_points);
            _glow.enabled = true;
            _core.enabled = true;
            if (_light != null && _hot != null)
            {
                _light.SetPositions(_points);
                _hot.SetPositions(_points);
            }
        }

        // The added light, as strong as the line glow setting, following the line's visibility, flash and squeeze.
        private void DrawLight(float scale, float flash, float thickness, float grip, int caps, float startShare)
        {
            float amount = Settings.LineGlow.Value / 100f;
            if (_light == null || _hot == null || amount <= 0f)
            {
                SetLightEnabled(false);
                return;
            }
            _light.widthMultiplier = (_locked ? 0.035f : 0.025f) * scale * flash * thickness;
            _hot.widthMultiplier = (_locked ? 0.012f : 0.008f) * scale * (1f + 0.3f * _flash) * thickness;
            _light.numCapVertices = caps;
            _hot.numCapVertices = caps;
            SetColors(_light, _lightColors, Palette.Apply(new Color(1f, 0.5f, 0.1f,
                Mathf.Clamp01((_locked ? 0.35f : 0.2f) * _visibility * flash * amount) * grip)), startShare);
            SetColors(_hot, _hotColors, Palette.Apply(new Color(1f, 0.75f, 0.45f,
                Mathf.Clamp01((_locked ? 0.5f : 0.3f) * _visibility * flash * amount) * grip)), startShare);
            SetLightEnabled(true);
        }

        private void SetLightEnabled(bool enabled)
        {
            if (_light != null)
                _light.enabled = enabled;
            if (_hot != null)
                _hot.enabled = enabled;
        }

        // A round glow facing the eye: a two-point line exactly as long as it's wide.
        private static void DrawFlare(LineRenderer flare, Vector3 center, float size, float alpha, Vector3 eye)
        {
            Vector3 right = Vector3.Cross(Vector3.up, center - eye);
            right = right.sqrMagnitude > 1e-8f ? right.normalized : Vector3.right;
            flare.SetPosition(0, center - right * (size * 0.5f));
            flare.SetPosition(1, center + right * (size * 0.5f));
            flare.widthMultiplier = size;
            Color color = Palette.Apply(new Color(1f, 0.6f, 0.2f, Mathf.Clamp01(alpha)));
            flare.startColor = color;
            flare.endColor = color;
            flare.enabled = true;
        }

        // Sparks at random points along the drawn beam, short of its faded tip, heading toward the hand.
        private void EmitSparks(float dt, float rate)
        {
            if (rate <= 0f)
            {
                _sparkDebt = 0f;
                return;
            }
            _sparkDebt += dt * rate;
            if (_sparkDebt < 1f)
                return;
            Color color = Palette.Apply(new Color(1f, 0.85f, 0.6f, 0.9f));
            while (_sparkDebt >= 1f)
            {
                _sparkDebt -= 1f;
                float along = Sparks.RandomValue() * 0.8f * (Points - 1);
                int i = Mathf.Min((int)along, Points - 2);
                Vector3 from = _points[i];
                Vector3 to = _points[i + 1];
                Vector3 step = to - from;
                Vector3 towardHand = step.sqrMagnitude > 1e-10f ? step.normalized : Vector3.zero;
                Sparks.EmitAlongLine(Vector3.Lerp(from, to, along - i), towardHand, color);
            }
        }

        // Fading to nothing at the tip. The start fades in from the given share of the strength over the first
        // stretch of the beam, so with a share of 1 it's full strength right at the object.
        private static void SetColors(LineRenderer line, LineColors colors, Color color, float startShare)
        {
            float alpha = Mathf.Clamp01(color.a);
            color.a = 1f;
            colors.ColorKeys[0] = new GradientColorKey(color, 0f);
            colors.ColorKeys[1] = new GradientColorKey(color, 1f);
            colors.AlphaKeys[0] = new GradientAlphaKey(alpha * startShare, 0f);
            colors.AlphaKeys[1] = new GradientAlphaKey(alpha * (1f - FadeInEnd), FadeInEnd);
            colors.AlphaKeys[2] = new GradientAlphaKey(0f, 1f);
            colors.Gradient.SetKeys(colors.ColorKeys, colors.AlphaKeys);
            line.colorGradient = colors.Gradient;
        }

        // A beam's colours along its length, kept to be filled in again every frame.
        private sealed class LineColors
        {
            public readonly Gradient Gradient = new Gradient();
            public readonly Il2CppStructArray<GradientColorKey> ColorKeys = new Il2CppStructArray<GradientColorKey>(2);
            public readonly Il2CppStructArray<GradientAlphaKey> AlphaKeys = new Il2CppStructArray<GradientAlphaKey>(3);
        }

        private void SetEnabled(bool enabled)
        {
            if (_glow != null)
                _glow.enabled = enabled;
            if (_core != null)
                _core.enabled = enabled;
            if (_flare != null)
                _flare.enabled = enabled;
            if (!enabled)
                SetLightEnabled(false);
        }

        private bool EnsureCreated()
        {
            if (_unavailable)
                return false;
            if (_beamMaterial == null)
            {
                _beamMaterial = LineFactory.CreateMaterial(GlowTextures.Beam);
                if (_beamMaterial == null)
                {
                    _unavailable = true;
                    return false;
                }
            }
            if (_flareMaterial == null)
                _flareMaterial = LineFactory.CreateMaterial(GlowTextures.Dot);

            if (_glow == null)
                _glow = CreateBeam("Gravity Gloves Beam Glow", 0);
            if (_core == null)
                _core = CreateBeam("Gravity Gloves Beam Core", 1);
            if (_flare == null)
                _flare = CreateFlare("Gravity Gloves Beam Flare");
            if (_lightMaterial == null && !_lightUnavailable)
            {
                _lightMaterial = GlowMaterials.CreateAdditive();
                _lightUnavailable = _lightMaterial == null;
            }
            if (_lightMaterial != null)
            {
                _light ??= CreateLight("Gravity Gloves Beam Light", 3);
                _hot ??= CreateLight("Gravity Gloves Beam Light Core", 4);
            }
            return _glow != null && _core != null && _flare != null;
        }

        private static LineRenderer CreateLight(string name, int sortingOrder)
        {
            LineRenderer line = LineFactory.Create(name, Points, _lightMaterial);
            if (line == null)
                return null;
            line.widthCurve = BeamWidth();
            line.sortingOrder = sortingOrder;
            return line;
        }

        private LineRenderer CreateBeam(string name, int sortingOrder)
        {
            LineRenderer line = LineFactory.Create(name, Points, _beamMaterial);
            if (line == null)
                return null;
            line.textureMode = LineTextureMode.Tile;
            line.widthCurve = BeamWidth();
            line.sortingOrder = sortingOrder;
            return line;
        }

        private static LineRenderer CreateFlare(string name)
        {
            LineRenderer flare = LineFactory.Create(name, 2, _flareMaterial);
            if (flare == null)
                return null;
            flare.numCapVertices = 0;
            flare.sortingOrder = 2;
            return flare;
        }

        // Thickest where it leaves the object, tapering off to a point at the tip.
        private static AnimationCurve BeamWidth()
        {
            if (_beamWidth != null)
                return _beamWidth;
            _beamWidth = new AnimationCurve();
            _beamWidth.AddKey(0f, 1f);
            _beamWidth.AddKey(0.35f, 0.65f);
            _beamWidth.AddKey(1f, 0.06f);
            return _beamWidth;
        }
    }
}
