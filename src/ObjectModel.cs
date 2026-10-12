using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GravityGloves
{
    // A copy of how an object looks, made of its visible meshes, that can be shown anywhere and at any size
    // without the object itself: the miniature in a wrist pocket and the see-through copy in a reaching hand.
    // Parts are stored relative to a reference transform, usually the body the object is held by. Skinned meshes
    // are baked in the pose they're in, and only the most detailed level of a level-of-detail group is kept.
    internal sealed class ObjectModel
    {
        private const int MaxParts = 48;

        private sealed class Part
        {
            public Mesh Mesh;
            public bool Baked;
            public Il2CppReferenceArray<Material> Materials;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public int Layer;

            // Glass, scope lenses and glow quads: drawn with their own materials in the miniature, but left out
            // of a one-material copy unless there's nothing else.
            public bool SeeThrough;
        }

        private readonly List<Part> _parts = new List<Part>();

        private ObjectModel()
        {
        }

        // The model's bounds in the reference frame, and how many meshes it has.
        public Bounds Bounds { get; private set; }
        public int PartCount => _parts.Count;

        // The longest side of the model, in metres.
        public float Size
        {
            get
            {
                Vector3 size = Bounds.size;
                return Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            }
        }

        // Copies the visible meshes of the given objects. The first one's bounds decide which meshes are too big
        // to belong to it, like effect volumes.
        public static ObjectModel Capture(Target target, IReadOnlyList<GameObject> roots, Transform reference)
        {
            var model = new ObjectModel();
            Frame toReference = Frame.Of(reference).Inverse();
            foreach ((Renderer source, Mesh mesh, bool seeThrough) in VisibleMeshes(target, roots))
            {
                if (model._parts.Count >= MaxParts)
                    break;
                try
                {
                    model.Add(source, mesh, toReference, seeThrough);
                }
                catch (Exception e)
                {
                    Mod.Log.Warning($"Couldn't copy {source.gameObject.name} for a wrist pocket: {e.Message}");
                }
            }
            model.Bounds = model.MeasureBounds(PartLimit(target));
            return model;
        }

        // The longest side of the object's solid meshes, measured along its own axes so it's the same however it's
        // held. Collider bounds are lined up with the world, so they shrank and grew as the object turned: in v0.21
        // a 76 cm crowbar went in under a 40 cm limit, while a 57 cm gun didn't. See-through meshes only count if
        // there's nothing solid: in v0.23 the spawn gun and the M9 measured 3.7 to 50 m, a different length every
        // time, from an effect that stretches to what they point at (William's "can't put it in for no reason").
        public static float MeasureSize(Target target, Transform reference, out string largest)
        {
            largest = null;
            var roots = new List<GameObject> { target.RootObject };
            Frame toReference = Frame.Of(reference).Inverse();
            float limit = PartLimit(target);
            var box = new Bounds();
            bool found = false;
            float largestSize = 0f;
            var meshes = VisibleMeshes(target, roots);
            bool anySolid = meshes.Exists(m => !m.SeeThrough);
            foreach ((Renderer source, Mesh mesh, bool seeThrough) in meshes)
            {
                if (anySolid && seeThrough)
                    continue;
                Transform frame = source.transform;
                Bounds part = BoxIn(mesh.bounds, point => toReference.TransformPoint(frame.TransformPoint(point)));
                float partSize = Longest(part.size);
                if (partSize > limit)
                    continue;
                if (partSize > largestSize)
                {
                    largestSize = partSize;
                    largest = source.gameObject.name;
                }
                if (found)
                {
                    box.Encapsulate(part);
                }
                else
                {
                    box = part;
                    found = true;
                }
            }
            return found ? Longest(box.size) : 0f;
        }

        // A single part much bigger than the object's colliders is an effect, not the object: the colliders cover
        // the object, so their bounds' diagonal is at least its longest side.
        private static float PartLimit(Target target) =>
            target.TryGetBounds(out Bounds bounds) ? bounds.size.magnitude * 1.5f + 0.1f : float.MaxValue;

        private static float Longest(Vector3 size) => Mathf.Max(size.x, Mathf.Max(size.y, size.z));

        // A box's eight corners taken somewhere else, and the box around them there.
        private static Bounds BoxIn(Bounds local, Func<Vector3, Vector3> toThere)
        {
            Vector3 c = local.center;
            Vector3 e = local.extents;
            var box = new Bounds(toThere(c - e), Vector3.zero);
            for (int i = 1; i < 8; i++)
                box.Encapsulate(toThere(c + new Vector3(i % 2 == 0 ? -e.x : e.x, (i / 2) % 2 == 0 ? -e.y : e.y, i < 4 ? -e.z : e.z)));
            return box;
        }

        // The meshes worth copying: switched on, not a glow shell, not a distant level of detail, and not hidden
        // or much bigger than the object. See-through ones are marked.
        private static List<(Renderer Source, Mesh Mesh, bool SeeThrough)> VisibleMeshes(Target target,
            IReadOnlyList<GameObject> roots)
        {
            var meshes = new List<(Renderer, Mesh, bool)>();
            bool hasBounds = target.TryGetBounds(out Bounds objectBounds);
            var lesserLods = LesserLodRenderers(roots);
            var sources = new List<Renderer>();
            foreach (GameObject root in roots)
            {
                if (root == null)
                    continue;
                foreach (MeshRenderer source in root.GetComponentsInChildren<MeshRenderer>())
                    sources.Add(source);
                foreach (SkinnedMeshRenderer source in root.GetComponentsInChildren<SkinnedMeshRenderer>())
                    sources.Add(source);
            }

            foreach (Renderer source in sources)
            {
                if (source == null || !source.enabled || source.gameObject.name == ObjectGlow.ShellName ||
                    lesserLods.Contains(source.Pointer))
                {
                    continue;
                }
                Mesh mesh = ObjectGlow.MeshOf(source);
                if (mesh == null)
                    continue;
                string reason = ObjectGlow.SkipReason(source, hasBounds, objectBounds, out bool lastResortOnly);
                bool seeThrough = reason != null && lastResortOnly && reason == "see-through";
                if (reason == null || seeThrough)
                    meshes.Add((source, mesh, seeThrough));
            }
            return meshes;
        }

        // A new object showing the model, with its own materials, or all in one material for a see-through copy.
        // Its parts have no colliders, so it can't be touched.
        public GameObject Build(string name, Material material)
        {
            var root = new GameObject(name);
            Object.DontDestroyOnLoad(root);
            bool anySolid = false;
            foreach (Part part in _parts)
                anySolid |= !part.SeeThrough;

            Transform rootTransform = root.transform;
            foreach (Part part in _parts)
            {
                if (material != null && part.SeeThrough && anySolid)
                    continue;
                var partObject = new GameObject(name);
                partObject.layer = part.Layer;
                Transform partTransform = partObject.transform;
                partTransform.SetParent(rootTransform, false);
                partTransform.localPosition = part.Position;
                partTransform.localRotation = part.Rotation;
                partTransform.localScale = part.Scale;
                partObject.AddComponent<MeshFilter>().sharedMesh = part.Mesh;
                MeshRenderer renderer = partObject.AddComponent<MeshRenderer>();
                if (material != null)
                {
                    var materials = new Material[Mathf.Max(part.Mesh.subMeshCount, 1)];
                    for (int i = 0; i < materials.Length; i++)
                        materials[i] = material;
                    renderer.sharedMaterials = materials;
                }
                else
                {
                    renderer.sharedMaterials = part.Materials;
                }
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            return root;
        }

        // Frees the meshes baked from skinned meshes. Objects built from the model should be destroyed first.
        public void Destroy()
        {
            foreach (Part part in _parts)
            {
                if (part.Baked && part.Mesh != null)
                    Object.Destroy(part.Mesh);
            }
            _parts.Clear();
        }

        private void Add(Renderer source, Mesh mesh, Frame toReference, bool seeThrough)
        {
            Transform sourceTransform = source.transform;
            Vector3 scale = sourceTransform.lossyScale;
            bool baked = false;
            SkinnedMeshRenderer skinned = source.TryCast<SkinnedMeshRenderer>();
            if (skinned != null)
            {
                var snapshot = new Mesh { hideFlags = HideFlags.DontUnloadUnusedAsset };
                skinned.BakeMesh(snapshot, false);
                mesh = snapshot;
                baked = true;
                scale = BakedScale(skinned, snapshot, scale);
            }

            Frame pose = toReference.Then(Frame.Of(sourceTransform));
            _parts.Add(new Part
            {
                Mesh = mesh,
                Baked = baked,
                Materials = source.sharedMaterials,
                Position = pose.Position,
                Rotation = pose.Rotation,
                Scale = scale,
                Layer = source.gameObject.layer,
                SeeThrough = seeThrough,
            });
        }

        // Which way a baked mesh holds the renderer's scale depends on the Unity version, so it's measured: drawn
        // at the scale that matches the original's size on screen.
        private static Vector3 BakedScale(SkinnedMeshRenderer source, Mesh baked, Vector3 lossyScale)
        {
            float bakedSize = baked.bounds.size.magnitude;
            float shownSize = source.bounds.size.magnitude;
            float scaled = bakedSize * Mathf.Max(Mathf.Abs(lossyScale.x), Mathf.Max(Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z)));
            if (bakedSize <= 0f || shownSize <= 0f || scaled <= 0f)
                return Vector3.one;
            return Mathf.Abs(Mathf.Log(shownSize / scaled)) < Mathf.Abs(Mathf.Log(shownSize / bakedSize)) ? lossyScale : Vector3.one;
        }

        // The bounds of the solid parts, or of everything if nothing is solid. See-through parts are effects, like
        // a laser sight's beam, which stretches to whatever the gun points at.
        private Bounds MeasureBounds(float limit)
        {
            var bounds = new Bounds();
            bool found = false;
            bool anySolid = false;
            foreach (Part part in _parts)
                anySolid |= !part.SeeThrough;
            foreach (Part part in _parts)
            {
                if (anySolid && part.SeeThrough)
                    continue;
                Bounds box = BoxIn(part.Mesh.bounds, point => part.Position + part.Rotation * Vector3.Scale(point, part.Scale));
                if (Longest(box.size) > limit)
                    continue;
                if (found)
                {
                    bounds.Encapsulate(box);
                }
                else
                {
                    bounds = box;
                    found = true;
                }
            }
            return bounds;
        }

        // Renderers that only show at a distance. The most detailed level is enough for a close-up copy, and
        // copying them all would draw the same shape several times over.
        private static HashSet<IntPtr> LesserLodRenderers(IReadOnlyList<GameObject> roots)
        {
            var lesser = new HashSet<IntPtr>();
            var best = new HashSet<IntPtr>();
            foreach (GameObject root in roots)
            {
                if (root == null)
                    continue;
                foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>())
                {
                    Il2CppReferenceArray<LOD> lods = group.GetLODs();
                    for (int level = 0; level < lods.Length; level++)
                    {
                        Il2CppReferenceArray<Renderer> renderers = lods[level].renderers;
                        if (renderers == null)
                            continue;
                        foreach (Renderer renderer in renderers)
                        {
                            if (renderer != null)
                                (level == 0 ? best : lesser).Add(renderer.Pointer);
                        }
                    }
                }
            }
            lesser.ExceptWith(best);
            return lesser;
        }
    }
}
