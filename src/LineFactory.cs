using UnityEngine;
using UnityEngine.Rendering;

namespace GravityGloves
{
    // Line renderers and materials made from a shader that ships with BONELAB.
    internal static class LineFactory
    {
        private const string ParticleShader = "Universal Render Pipeline/Particles/Unlit";
        public const string LegacyParticleShader = "Legacy Shaders/Particles/Alpha Blended";

        // Shaders that can draw a textured, vertex-coloured line, best first.
        private static readonly string[] ShaderNames =
        {
            ParticleShader, LegacyParticleShader, "UI/Default", "Sprites/Default",
        };

        public const string TintColor = "_TintColor";

        private static Shader _shader;
        private static string _shaderName;
        private static bool _searchedShaders;
        private static Material _plainMaterial;

        // A disabled line with the given material, or a plain one. Returns null if no usable shader was found.
        public static LineRenderer Create(string name, int points, Material material = null)
        {
            if (material == null)
                material = PlainMaterial();
            if (material == null)
                return null;

            var lineObject = new GameObject(name);
            Object.DontDestroyOnLoad(lineObject);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = points;
            line.sharedMaterial = material;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.numCapVertices = 4;
            line.alignment = LineAlignment.View;
            line.enabled = false;
            return line;
        }

        // A new line material, optionally textured. Returns null if no usable shader was found.
        public static Material CreateMaterial(Texture texture = null)
        {
            Shader shader = FindShader();
            if (shader == null)
                return null;

            var material = new Material(shader);
            // Keep it through level loads, which unload unused assets.
            material.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (_shaderName == ParticleShader)
            {
                // Additive blending, so lines glow instead of looking like solid wire.
                material.SetFloat("_Surface", 1f);
                material.SetFloat("_Blend", 2f);
                material.SetFloat("_SrcBlend", (float)BlendMode.One);
                material.SetFloat("_DstBlend", (float)BlendMode.One);
                material.SetFloat("_ZWrite", 0f);
                material.SetFloat("_Cull", 0f);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)RenderQueue.Transparent;
            }
            else if (_shaderName == LegacyParticleShader)
            {
                // The shader doubles its tint, so 0.5 shows the line's colours as they are. Its default tint is
                // a grey that linear colour space turns into 0.21, which drew every line at 43% brightness.
                // SetVector skips that conversion.
                material.SetVector(TintColor, new Vector4(0.5f, 0.5f, 0.5f, 0.5f));
            }
            if (texture != null)
                material.mainTexture = texture;
            return material;
        }

        public static void Destroy(LineRenderer line)
        {
            if (line != null)
                Object.Destroy(line.gameObject);
        }

        private static Material PlainMaterial()
        {
            if (_plainMaterial == null)
                _plainMaterial = CreateMaterial();
            return _plainMaterial;
        }

        private static Shader FindShader()
        {
            if (_shader != null || _searchedShaders)
                return _shader;
            _searchedShaders = true;

            foreach (string name in ShaderNames)
            {
                Shader shader = Shader.Find(name);
                if (shader == null)
                    continue;
                Mod.Trace($"Line shader: {name}, colour space: {QualitySettings.activeColorSpace}");
                _shader = shader;
                _shaderName = name;
                return shader;
            }

            Mod.Log.Warning("Couldn't find a shader for lines, so the aim beam and debug view won't be drawn.");
            return null;
        }
    }
}
