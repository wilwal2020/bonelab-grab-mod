using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GravityGloves
{
    // Materials for the object glow, one per glow style.
    // - Standard is the line shader, a see-through tint.
    // - Additive adds light instead. It's Unity's built-in colored shader, which lets its blending be set, with the
    //   blending switched to additive.
    // - Hologram copies a material that uses the game's hologram shader. That shader is only loaded once
    //   something drawn with it, like the plank gun, has been spawned, so until then the glow stays standard.
    // Additive and Hologram are experiments, and log what they found so the next version can build on it.
    internal static class GlowMaterials
    {
        private const string ColoredShader = "Hidden/Internal-Colored";
        private const string HologramShader = "SLZ/Hologram Depth fade";

        // Asking for the hologram shader by name is quick, so it's done this often while it's wanted and missing.
        // Going through every loaded shader is slower, so that's only done now and then, in case the shader
        // goes by another name.
        private const float FindInterval = 2f;
        private const float ScanInterval = 30f;

        // Colour properties a borrowed shader might use. The hologram shader has _HologramEdgeColor and
        // _EmissionColor (v0.15 log).
        private static readonly string[] ColorProperties =
        {
            "_Color", "_BaseColor", "_TintColor", "_MainColor", "_EmissionColor", "_HologramEdgeColor",
            "_HologramColor", "_HoloColor", "_RimColor", "_FresnelColor",
        };

        // Words in shader names worth knowing about, logged when there's no hologram shader.
        private static readonly string[] InterestingShaderWords =
            { "holo", "additive", "glow", "fresnel", "rim", "emiss", "unlit", "particle", "outline", "highlight" };

        private static Material _hologramSource;
        private static float _nextFind;
        private static float _nextScan;
        private static bool _loggedAdditive;
        private static bool _loggedMissingHologram;

        // Goes up when a better material becomes available, so glows rebuild with it.
        public static int Version { get; private set; }

        // A new material for the style, or a standard one if the style isn't available. Returns null only if no
        // shader at all was found.
        public static Material Create(GlowShader style)
        {
            Material material = null;
            try
            {
                if (style == GlowShader.Additive)
                    material = CreateAdditive(false);
                else if (style == GlowShader.Hologram)
                    material = CreateHologram();
            }
            catch (Exception e)
            {
                Mod.Log.Error($"Couldn't make the {style} glow material, so the glow stays standard: {e.Message}");
            }
            return material != null ? material : LineFactory.CreateMaterial();
        }

        // Call every frame the glow shows. Keeps looking for the hologram shader while it's wanted and missing.
        public static void Refresh(GlowShader style)
        {
            if (style == GlowShader.Hologram && _hologramSource == null && FindHologramSource() != null)
                Version++;
        }

        // On a level load the borrowed material may be unloaded, so look again.
        public static void Reset()
        {
            _nextFind = 0f;
            if (_hologramSource == null)
                return;
            _hologramSource = null;
            Version++;
        }

        // Sets the glow's colour, with its alpha as the strength.
        public static void SetTint(Material material, Color color)
        {
            Shader shader = material.shader;
            string shaderName = shader != null ? shader.name : "";
            if (shaderName == ColoredShader)
            {
                // SetColor converts to linear colour space itself.
                material.SetColor("_Color", color);
                return;
            }
            if (shaderName == LineFactory.LegacyParticleShader)
            {
                // The legacy particle shader doubles its tint colour. Halving the colour before SetColor converted
                // it to linear made the glow 43% as bright as intended, so the colour is converted first and set
                // with SetVector, which leaves it alone.
                Color shade = QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;
                material.SetVector(LineFactory.TintColor,
                    new Vector4(shade.r * 0.5f, shade.g * 0.5f, shade.b * 0.5f, color.a * 0.5f));
                return;
            }
            bool any = false;
            foreach (string property in ColorProperties)
            {
                if (material.HasProperty(property))
                {
                    material.SetColor(property, color);
                    any = true;
                }
            }
            if (!any)
                material.color = color;
        }

        // A material that adds light, coloured by the vertex colours times its tint (white), like the additive object
        // glow. Lines drawn with it glow the way the object does. On top: drawn over everything, for the wrist
        // pockets, so turning to face the player never hides them in the arm. Null if the shader isn't in the game.
        public static Material CreateAdditive(bool onTop = false)
        {
            Shader shader = Shader.Find(ColoredShader);
            if (shader == null)
            {
                LogOnce(ref _loggedAdditive, $"Additive glow: {ColoredShader} isn't in the game, so the glow stays standard");
                return null;
            }
            var material = new Material(shader);
            material.hideFlags = HideFlags.DontUnloadUnusedAsset;
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.One);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_Cull", (float)CullMode.Back);
            material.renderQueue = (int)RenderQueue.Transparent;
            if (onTop)
                DrawOnTop(material, true);
            LogOnce(ref _loggedAdditive, $"Additive glow uses {ColoredShader}");
            return material;
        }

        // Draws a material over everything, or depth-tested as usual again. Only shaders with a depth test setting,
        // like the additive one, can do it.
        public static void DrawOnTop(Material material, bool onTop)
        {
            if (material == null || !material.HasProperty("_ZTest"))
                return;
            material.SetFloat("_ZTest", (float)(onTop ? CompareFunction.Always : CompareFunction.LessEqual));
            material.renderQueue = onTop ? (int)RenderQueue.Overlay : (int)RenderQueue.Transparent;
        }

        private static Material CreateHologram()
        {
            Material source = FindHologramSource();
            if (source == null)
                return null;
            var material = new Material(source);
            material.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return material;
        }

        private static Material FindHologramSource()
        {
            if (_hologramSource != null)
                return _hologramSource;
            float now = Time.unscaledTime;
            if (now < _nextFind)
                return null;
            _nextFind = now + FindInterval;

            Shader shader = Shader.Find(HologramShader);
            if (shader == null && now >= _nextScan)
            {
                _nextScan = now + ScanInterval;
                shader = ScanShaders();
            }
            if (shader == null)
                return null;

            // A material the game made with it keeps the game's settings and textures.
            Material source = FindMaterialUsing(shader);
            if (source == null)
            {
                source = new Material(shader);
                source.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }
            _hologramSource = source;
            LogProperties(source, shader);
            return source;
        }

        // Looks through every loaded shader for one with "hologram" in its name. The first time there isn't one,
        // logs the other effect shaders that are loaded, for future experiments.
        private static Shader ScanShaders()
        {
            Il2CppReferenceArray<Object> shaders = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Shader>());
            var interesting = new SortedSet<string>();
            for (int i = 0; i < shaders.Length; i++)
            {
                Shader shader = shaders[i] != null ? shaders[i].TryCast<Shader>() : null;
                string name = shader != null ? shader.name ?? "" : "";
                if (name.IndexOf("hologram", StringComparison.OrdinalIgnoreCase) >= 0)
                    return shader;
                foreach (string word in InterestingShaderWords)
                {
                    if (name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        interesting.Add(name);
                        break;
                    }
                }
            }
            if (!_loggedMissingHologram)
            {
                _loggedMissingHologram = true;
                Mod.Trace($"Hologram glow: no hologram shader is loaded yet, so the glow stays standard until " +
                            $"something drawn with it, like the plank gun, has been spawned. Other effect shaders " +
                            $"among the {shaders.Length} loaded: " +
                            $"{(interesting.Count > 0 ? string.Join(", ", interesting) : "none")}");
            }
            return null;
        }

        private static Material FindMaterialUsing(Shader shader)
        {
            Il2CppReferenceArray<Object> materials = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Material>());
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i] != null ? materials[i].TryCast<Material>() : null;
                if (material != null && material.shader != null && material.shader.Pointer == shader.Pointer)
                    return material;
            }
            return null;
        }

        private static void LogProperties(Material material, Shader shader)
        {
            var properties = new List<string>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
                properties.Add($"{shader.GetPropertyName(i)} ({shader.GetPropertyType(i)})");
            Mod.Trace($"Hologram glow copies {material.name} ({shader.name}, render queue {material.renderQueue}). " +
                        $"Its properties: {string.Join(", ", properties)}");
        }

        private static void LogOnce(ref bool logged, string message)
        {
            if (logged)
                return;
            logged = true;
            Mod.Trace(message);
        }
    }
}
