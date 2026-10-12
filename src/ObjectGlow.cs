using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GravityGloves
{
    // Makes the object a hand has selected glow. Each of its visible meshes gets a slightly enlarged,
    // see-through copy in the glove's colour as a child of the original, so it moves with the object. The copies
    // are removed as soon as the object stops glowing. What the copies are drawn with is the glow style
    // (GlowMaterials).
    internal static class ObjectGlow
    {
        public const string ShellName = "Gravity Gloves Glow";

        // How much bigger the copies are, so they sit just outside the object's surface.
        private const float Inflate = 1.015f;
        private const int MaxShells = 32;
        private const float FadeInTime = 0.15f;

        // A skinned mesh needs a helper object per bone, so very detailed rigs are left out.
        private const int MaxBones = 64;

        // Materials drawn from this render queue up are see-through effects like glow quads and glass.
        // Cut-out materials, like bristles and leaves, are drawn just before it and still count as solid.
        private const int TransparentQueue = 2501;

        // Shaders for meshes that are never meant to be seen. Plain "depth" also matched the hologram shader
        // the plank gun is drawn with.
        private static readonly string[] HiddenShaderWords =
            { "null", "hidden", "invisible", "occlu", "shadow", "depthonly", "depth only", "depthmask", "depth mask" };

        // Log, once per kind of object, what the glow skipped or why it found nothing, to track down odd meshes.
        private const int MaxLoggedObjects = 100;
        private static readonly HashSet<string> LoggedObjects = new HashSet<string>();
        private static readonly Regex InstanceNumber = new Regex(@"\s*\[\d+\]$");

        private sealed class Shell
        {
            public Renderer Source;
            public Renderer Renderer;

            // The copy, and for a skinned mesh the helpers on its bones.
            public readonly List<GameObject> Objects = new List<GameObject>();
        }

        private sealed class Slot
        {
            public readonly Dictionary<Target, List<Shell>> Glowing = new Dictionary<Target, List<Shell>>();
            public Target Main;
            public Material Material;
            public GlowShader Style;
            public int MaterialVersion;
            public float Strength;
        }

        private static readonly Slot[] Slots = { new Slot(), new Slot() };
        private static readonly HashSet<Target> Wanted = new HashSet<Target>();
        private static readonly List<Target> NoLongerWanted = new List<Target>();

        // Call every frame for each hand that has something to glow. The first target is the main one, and the
        // glow fades in again whenever that changes. Strong is for locked or flying objects, and fade dims the
        // glow as a pulled object nears the hand.
        public static void Show(int hand, IReadOnlyList<Target> targets, bool strong, float fade = 1f)
        {
            Slot slot = Slots[hand];
            // A new glow style, or a better material for it, rebuilds the glow.
            GlowShader style = Settings.GlowStyle.Value;
            GlowMaterials.Refresh(style);
            if (slot.Material == null || slot.Style != style || slot.MaterialVersion != GlowMaterials.Version)
            {
                Hide(hand);
                if (slot.Material != null)
                    Object.Destroy(slot.Material);
                slot.Material = GlowMaterials.Create(style);
                slot.Style = style;
                slot.MaterialVersion = GlowMaterials.Version;
            }
            if (slot.Material == null || targets.Count == 0)
                return;

            if (slot.Main != targets[0])
            {
                slot.Main = targets[0];
                slot.Strength = 0f;
            }

            // Add and remove glows one object at a time, so a batch changing doesn't make the rest flicker.
            Wanted.Clear();
            foreach (Target target in targets)
                Wanted.Add(target);
            NoLongerWanted.Clear();
            foreach (Target target in slot.Glowing.Keys)
            {
                if (!Wanted.Contains(target))
                    NoLongerWanted.Add(target);
            }
            foreach (Target target in NoLongerWanted)
            {
                DestroyShells(slot.Glowing[target]);
                slot.Glowing.Remove(target);
            }
            foreach (Target target in targets)
            {
                if (!slot.Glowing.ContainsKey(target))
                    slot.Glowing[target] = Build(target, slot.Material);
            }

            slot.Strength = Mathf.MoveTowards(slot.Strength, 1f, Time.unscaledDeltaTime / FadeInTime);
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * (strong ? 9f : 4f));
            float alpha = (strong ? 0.24f + 0.06f * pulse : 0.12f + 0.04f * pulse) * slot.Strength *
                          Settings.GlowIntensity.Value / 100f * Mathf.Clamp01(fade);
            GlowMaterials.SetTint(slot.Material, Palette.Apply(new Color(1f, 0.5f, 0.1f, Mathf.Clamp01(alpha))));

            // Only show a copy while its original is drawn, so level-of-detail meshes don't stack up.
            foreach (List<Shell> shells in slot.Glowing.Values)
            {
                foreach (Shell shell in shells)
                {
                    if (shell.Renderer != null)
                        shell.Renderer.enabled = shell.Source != null && shell.Source.enabled && shell.Source.isVisible;
                }
            }
        }

        public static void Hide(int hand)
        {
            Slot slot = Slots[hand];
            foreach (List<Shell> shells in slot.Glowing.Values)
                DestroyShells(shells);
            slot.Glowing.Clear();
            slot.Main = null;
        }

        private static List<Shell> Build(Target target, Material material)
        {
            var shells = new List<Shell>();
            GameObject root = target.IsAlive ? target.RootObject : null;
            if (root == null)
                return shells;
            bool hasBounds = target.TryGetBounds(out Bounds objectBounds);
            var skipped = new List<string>();

            // Meshes left out only for looking see-through or hidden. If nothing else glows, these do, since
            // some objects, like the plank gun, are drawn entirely with effect shaders.
            var lastResort = new List<(Renderer Source, Mesh Mesh)>();

            var sources = new List<Renderer>();
            foreach (MeshRenderer source in root.GetComponentsInChildren<MeshRenderer>())
                sources.Add(source);
            int meshCount = sources.Count;
            foreach (SkinnedMeshRenderer source in root.GetComponentsInChildren<SkinnedMeshRenderer>())
                sources.Add(source);
            int skinnedCount = sources.Count - meshCount;

            foreach (Renderer source in sources)
            {
                if (shells.Count >= MaxShells)
                    break;
                if (source == null || source.gameObject.name == ShellName)
                    continue;
                string name = source.gameObject.name;
                if (!source.enabled)
                {
                    skipped.Add($"{name} (switched off)");
                    continue;
                }
                Mesh mesh = MeshOf(source);
                if (mesh == null)
                {
                    skipped.Add($"{name} (no mesh)");
                    continue;
                }

                string reason = SkipReason(source, hasBounds, objectBounds, out bool lastResortOnly);
                if (reason != null)
                {
                    skipped.Add($"{name} ({reason})");
                    if (lastResortOnly)
                        lastResort.Add((source, mesh));
                    continue;
                }
                AddShell(shells, skipped, source, mesh, material);
            }

            bool usedLastResort = shells.Count == 0 && lastResort.Count > 0;
            if (usedLastResort)
            {
                foreach ((Renderer source, Mesh mesh) in lastResort)
                {
                    if (shells.Count >= MaxShells)
                        break;
                    AddShell(shells, skipped, source, mesh, material);
                }
            }

            int skinnedShells = 0;
            foreach (Shell shell in shells)
            {
                if (shell.Source.TryCast<SkinnedMeshRenderer>() != null)
                    skinnedShells++;
            }
            if (skipped.Count > 0 || shells.Count == 0 || skinnedShells > 0)
                LogOnce(target, shells.Count == 0
                    ? $"found nothing to glow: {meshCount} meshes, {skinnedCount} skinned meshes"
                    : (usedLastResort ? "found nothing solid to glow, so glows " : "glows ") +
                      $"{shells.Count} meshes{(skinnedShells > 0 ? $" ({skinnedShells} skinned)" : "")}", skipped);
            return shells;
        }

        // A mesh that fails to copy is skipped rather than breaking the glove.
        private static void AddShell(List<Shell> shells, List<string> skipped, Renderer source, Mesh mesh, Material material)
        {
            try
            {
                SkinnedMeshRenderer skinned = source.TryCast<SkinnedMeshRenderer>();
                Shell shell = skinned != null
                    ? CreateSkinnedShell(skinned, mesh, material, out string problem)
                    : CreateShell(source, mesh, material, out problem);
                if (shell != null)
                    shells.Add(shell);
                else
                    skipped.Add($"{source.gameObject.name} ({problem})");
            }
            catch (Exception e)
            {
                skipped.Add($"{source.gameObject.name} (couldn't copy it: {e.Message})");
            }
        }

        public static Mesh MeshOf(Renderer source)
        {
            SkinnedMeshRenderer skinned = source.TryCast<SkinnedMeshRenderer>();
            if (skinned != null)
                return skinned.sharedMesh;
            MeshFilter filter = source.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        // Why a mesh shouldn't glow, or null if it should. Copying hidden or effect meshes made whole
        // planes light up. Meshes that only look see-through or hidden can still glow as a last resort.
        public static string SkipReason(Renderer source, bool hasBounds, Bounds objectBounds, out bool lastResortOnly)
        {
            lastResortOnly = false;
            if (hasBounds && source.bounds.size.magnitude > objectBounds.size.magnitude * 2f + 0.2f)
                return "much bigger than the object";

            lastResortOnly = true;
            bool anySolid = false;
            foreach (Material material in source.sharedMaterials)
            {
                if (material == null)
                    continue;
                Shader shader = material.shader;
                string shaderName = shader != null ? shader.name : "";
                foreach (string word in HiddenShaderWords)
                {
                    if (shaderName.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                        return $"hidden shader {shaderName}";
                }
                if (material.renderQueue < TransparentQueue)
                    anySolid = true;
            }
            return anySolid ? null : "see-through";
        }

        private static Shell CreateShell(Renderer source, Mesh mesh, Material material, out string problem)
        {
            problem = null;
            GameObject shellObject = CreateShellObject(source);
            Transform shellTransform = shellObject.transform;
            // Grow around the middle of the mesh rather than its pivot.
            shellTransform.localScale = Vector3.one * Inflate;
            shellTransform.localPosition = mesh.bounds.center * (1f - Inflate);

            shellObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer shellRenderer = shellObject.AddComponent<MeshRenderer>();
            SetUp(shellRenderer, mesh, material);
            var shell = new Shell { Source = source, Renderer = shellRenderer };
            shell.Objects.Add(shellObject);
            return shell;
        }

        // A skinned mesh's vertices are placed by its bones, so scaling the copy does nothing. Instead each bone
        // gets an empty child, scaled up around the middle of the mesh, and the copy is skinned to those.
        private static Shell CreateSkinnedShell(SkinnedMeshRenderer source, Mesh mesh, Material material, out string problem)
        {
            Il2CppReferenceArray<Transform> bones = source.bones;
            if (bones == null || bones.Length == 0)
            {
                // Without bones it's drawn where the renderer is, like a plain mesh.
                return CreateShell(source, mesh, material, out problem);
            }
            if (bones.Length > MaxBones)
            {
                problem = $"skinned to {bones.Length} bones";
                return null;
            }

            var shell = new Shell { Source = source };
            Vector3 center = source.bounds.center;
            var helpers = new Il2CppReferenceArray<Transform>(bones.Length);
            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = bones[i];
                if (bone == null)
                {
                    DestroyShell(shell);
                    problem = "a missing bone";
                    return null;
                }
                var helper = new GameObject(ShellName);
                shell.Objects.Add(helper);
                Transform helperTransform = helper.transform;
                helperTransform.SetParent(bone, false);
                helperTransform.localScale = Vector3.one * Inflate;
                Vector3 centerInBone = bone.InverseTransformPoint(center);
                helperTransform.localPosition = IsFinite(centerInBone) ? centerInBone * (1f - Inflate) : Vector3.zero;
                helpers[i] = helperTransform;
            }

            GameObject shellObject = CreateShellObject(source);
            shell.Objects.Add(shellObject);
            SkinnedMeshRenderer shellRenderer = shellObject.AddComponent<SkinnedMeshRenderer>();
            shellRenderer.sharedMesh = mesh;
            shellRenderer.bones = helpers;
            shellRenderer.rootBone = source.rootBone;
            shellRenderer.quality = source.quality;
            shellRenderer.updateWhenOffscreen = source.updateWhenOffscreen;
            // The same culling box as the original, a little bigger, so the copy isn't culled at odd angles.
            Bounds localBounds = source.localBounds;
            localBounds.Expand(localBounds.size.magnitude * 0.05f);
            shellRenderer.localBounds = localBounds;
            SetUp(shellRenderer, mesh, material);
            shell.Renderer = shellRenderer;
            problem = null;
            return shell;
        }

        private static GameObject CreateShellObject(Renderer source)
        {
            var shellObject = new GameObject(ShellName);
            shellObject.layer = source.gameObject.layer;
            shellObject.transform.SetParent(source.transform, false);
            return shellObject;
        }

        private static void SetUp(Renderer shellRenderer, Mesh mesh, Material material)
        {
            var materials = new Material[Mathf.Max(mesh.subMeshCount, 1)];
            for (int i = 0; i < materials.Length; i++)
                materials[i] = material;
            shellRenderer.sharedMaterials = materials;
            shellRenderer.shadowCastingMode = ShadowCastingMode.Off;
            shellRenderer.receiveShadows = false;
        }

        private static void DestroyShells(List<Shell> shells)
        {
            foreach (Shell shell in shells)
                DestroyShell(shell);
            shells.Clear();
        }

        private static void DestroyShell(Shell shell)
        {
            foreach (GameObject shellObject in shell.Objects)
            {
                if (shellObject != null)
                    Object.Destroy(shellObject);
            }
            shell.Objects.Clear();
        }

        private static void LogOnce(Target target, string summary, List<string> skipped)
        {
            string kind = InstanceNumber.Replace(target.Name, "");
            if (LoggedObjects.Count >= MaxLoggedObjects || !LoggedObjects.Add(kind))
                return;
            string skips = skipped.Count > 0 ? $", skipped {string.Join(", ", skipped)}" : "";
            Mod.Trace($"Glow on {kind} {summary}{skips}");
        }

        private static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) &&
            !float.IsNaN(v.z) && !float.IsInfinity(v.z);
    }
}
