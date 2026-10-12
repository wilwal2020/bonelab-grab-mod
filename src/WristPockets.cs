using System;
using System.Collections.Generic;
using BoneLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Interaction;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GravityGloves
{
    // Half-Life: Alyx's wrist pockets. Each wrist has a pocket that the other hand fills: let go of an object at
    // the pocket and it's put away, shown in the pocket as a see-through miniature. Reach for a full pocket with an
    // open hand and the copy moves into the hand, held the way it went in, with nothing to bump into. Grab, and
    // the object itself is back in the hand by the same grip. Bring another object to a full pocket and the copy
    // moves out beside the pocket; let go, and the old object drops out there while the new one goes in.
    //
    // A stored object is hidden with the game's own entity hiding, which takes it out of physics without the game
    // treating it as despawned. Anything plugged into it, like a gun's magazine, is an entity of its own and is
    // hidden and brought back with it.
    internal static class WristPockets
    {
        private const string PocketName = "Gravity Gloves Pocket";

        // The same grip thresholds as the glove, so a half squeeze doesn't flicker.
        private const float PressThreshold = 0.6f;
        private const float ReleaseThreshold = 0.35f;

        // The ring: its radius when empty, full, and open around an object about to go in, its line widths, and
        // how quickly it changes between them.
        private const float EmptyRadius = 0.03f;
        private const float FullRadius = 0.035f;
        private const float OpenRadius = 0.055f;
        private const float CoreWidth = 0.003f;
        private const float GlowWidth = 0.012f;
        private const int RingPoints = 32;
        private const float RingResponse = 12f;
        private const float FlashDecay = 3f;

        // The see-through copy of what's inside: how long it takes to move between the pocket, the hand and beside
        // the pocket, and to shrink into the pocket from where it was let go. Between moves it sits exactly where
        // it belongs: in v0.22 it was eased toward its place every frame and trailed behind the wrist (William).
        // Then how strongly it shows in each place, how fast the miniature turns (degrees per second), how far it's
        // lifted off the wrist, and how far clear of the ring it waits when it's about to be swapped out.
        private const float MoveTime = 0.15f;
        private const float AbsorbTime = 0.25f;
        private const float PocketAlpha = 0.55f;
        private const float HandAlpha = 0.4f;
        private const float SwapAlpha = 0.35f;
        private const float MiniatureSpin = 25f;
        private const float MiniatureLift = 0.01f;
        private const float SwapGap = 0.05f;

        // The wrist is the anchor of the joint between the physics hand and its forearm. If there's no such joint,
        // or it gives something odd, the wrist is this far back from the palm.
        private const float FallbackWristToPalm = 0.08f;
        private const float MinWristToPalm = 0.02f;
        private const float MaxWristToPalm = 0.2f;
        private const float JointSearchInterval = 2f;

        // A hand reaching into a pocket stays reaching until it's this much further out than the reach, so it
        // doesn't flicker in and out at the edge.
        private const float ReachHysteresis = 1.25f;

        // Something held up to a pocket goes in from its face or its sides, not from under the arm: its nearest
        // point can be at most this far below the pocket's face.
        private const float UndersideMargin = 0.02f;

        // A hand on an object but not on a grip of it, for this long, gets logged. Right after a grab it's normal.
        private const float NoGripLogDelay = 0.5f;

        // The same refusal of the same object is logged again only after this long.
        private const float RefusalRepeat = 30f;

        // A swapped-out object comes out clear of the player's body: it starts beside the pocket and is moved
        // out in steps of this until it's this clear of every part of the body, up to that many steps.
        private const float SwapStep = 0.02f;
        private const float SwapClearance = 0.01f;
        private const int MaxSwapSteps = 30;

        // Something that came out of a pocket is checked this long after: whether the hand that took it holds it
        // the way it was put away, or whether it's stuck where it came out instead of falling.
        private const float OutingCheckDelay = 0.4f;
        private const float StuckDistance = 0.01f;

        // The pull-in effect: sparks per second at 100% while something is at a pocket, and how long each takes to
        // reach it, and how many rush in as it goes in.
        private const float PullInRate = 30f;
        private const float PullInTime = 0.3f;
        private const int PullInBurstCount = 24;

        // Something in a pocket at the change of level is spawned again this long after the new level loads, and
        // given up on if it hasn't appeared this long after that. It appears this far above the player's head,
        // out of the way, and is hidden at once.
        private const float RespawnDelay = 1.5f;
        private const float RespawnTimeout = 10f;
        private const float RespawnHeight = 3f;
        private const float RespawnSettleTime = 0.1f;

        // With "Pocket faces you", how quickly the ring turns toward the eyes or a hand (per second).
        private const float FaceResponse = 10f;

        // How quickly the copy waiting to be swapped out eases to where it's clear of the body.
        private const float ClearingSmoothing = 0.1f;

        // A taken-out object that doesn't land within this of where it should is put there directly.
        private const float MaxTeleportError = 0.02f;

        // A stored object is kept from being switched off if the game culls the area it was in. Taken out, that
        // goes back to how it was after this long, once the game has seen it in the player's area. Restoring it
        // at once could switch off an object still counted as being in an area that was culled while it was away.
        private const float CullRestoreDelay = 2f;

        private sealed class HandState
        {
            public bool GripHeld;

            // While holding something: the object, the grip it's held by, how that grip's body sits in the hand,
            // and whether it's at the other wrist's pocket, as of the last frame it was held.
            public Target Holding;
            public Grip Grip;
            public Rigidbody Host;
            public Frame HostInHand;
            public bool AtPocket;

            // How the hand holds a handle, noted while it's at the pocket (see GripHold).
            public GripHold Hold;

            // The held object, and the body it's held by, it was last worked out for whether it fits, why not
            // (null if it does), and whether that's been logged. Worked out again for each hold, since a gun can
            // get a magazine in between.
            public Target Checked;
            public Rigidbody CheckedHost;
            public string Unstorable;
            public bool LoggedUnstorable;
            public bool Measured;

            // Since when the hand has held something without being on one of its grips (negative if it is), and
            // whether that's been logged.
            public float NoGripSince = -1f;
            public bool LoggedNoGrip;

            // Reaching into a full pocket, so the copy is in the hand. Took: just took something out, until the
            // grip is let go. WaitForLeave: just put something away, so the hand has to leave the pocket before
            // reaching into it counts.
            public bool Reaching;
            public bool Took;
            public bool WaitForLeave;

            // The hand's hover is locked while it reaches into a pocket.
            public bool HoverLocked;
        }

        private enum Placement
        {
            Pocket,
            Hand,
            Beside,
        }

        private sealed class Stored
        {
            public Target Target;

            // The object's own entity first, then anything plugged into it, each with its pose relative to the
            // body the object was held by, and whether the game was already kept from switching it off when culled.
            public readonly List<MarrowEntity> Entities = new List<MarrowEntity>();
            public readonly List<Frame> EntitiesInHost = new List<Frame>();
            public readonly List<bool> WasCullPrevented = new List<bool>();

            // Each of their bodies, and whether it was kinematic and how it was constrained when it went in, so it
            // comes out able to move again.
            public readonly List<(Rigidbody Body, bool Kinematic, RigidbodyConstraints Constraints)> Bodies =
                new List<(Rigidbody, bool, RigidbodyConstraints)>();

            public Grip Grip;
            public Rigidbody Host;
            public Frame HostInHand;
            public GripHold Hold;

            // Each entity's parent before it was moved out of the level while in the pocket, and whether it was.
            public readonly List<Transform> Parents = new List<Transform>();
            public readonly List<UnityEngine.SceneManagement.Scene> Scenes = new List<UnityEngine.SceneManagement.Scene>();
            public readonly List<bool> MovedOut = new List<bool>();

            // For spawning it again in the next level if it didn't survive the change anyway: the barcode of the
            // crate it was spawned from (null if it's part of the level), and the paths from its entity to the
            // grip and the body it was held by.
            public string Barcode;
            public string GripPath;
            public string HostPath;

            // It was in the pocket at a change of level, so it may still hold references to the last level.
            public bool CrossedLevel;

            // The see-through copy and its material, and the turn that lays it along the forearm. On top: the
            // material is drawn over everything, while the copy is in a pocket that faces the player.
            public bool OnTop;
            public ObjectModel Model;
            public GameObject Ghost;
            public Material GhostMaterial;
            public Quaternion LyingFlat;

            // Where the copy belongs, and its move there: from where it was shown when the move started, over
            // MoveDuration since MoveStart. Then where it's shown now.
            public Placement Placement;
            public float MoveStart;
            public float MoveDuration;
            public Vector3 FromPosition;
            public Quaternion FromRotation;
            public float FromScale;
            public float FromAlpha;
            public Vector3 GhostPosition;
            public Quaternion GhostRotation;
            public float GhostScale;
            public float GhostAlpha;
        }

        private sealed class Pocket
        {
            public LineRenderer Core;
            public LineRenderer Glow;
            public bool HasFrame;

            // This frame's pose: the centre, out of the back of the wrist, along the hand or forearm toward the
            // fingers, and across the wrist toward the thumb.
            public Vector3 Center;
            public Vector3 Outward;
            public Vector3 Along;
            public Vector3 Side;

            public float Radius = EmptyRadius;
            public float Brightness;
            public float Flash;

            // The way the ring faces: out of the wrist, or with "Pocket faces you" toward the eyes, or the hand that
            // reaches in or brings something to it, turning smoothly. Then the ring's own axes in its plane.
            public Vector3 Face;
            public Vector3 FaceAlong;
            public Vector3 FaceSide;

            // Something that can go in is at it, and if it's full, the one inside is about to be swapped out.
            public bool Open;
            public bool Swapping;
            public Stored Item;

            // How far out the copy waiting to be swapped out is shown, easing toward where it would be clear of the
            // body, so it doesn't jump in steps as the arm moves.
            public float ShownClearing;

            // Sparks owed to the pull-in effect, so fractions carry over between frames.
            public float EffectDebt;

            // What was in it in the last level, to spawn again in this one.
            public Carried Carried;

            // The joint between the hand and the forearm, whose anchor is the wrist.
            public Joint WristJoint;
            public float NextJointSearch;

            // For the log, the first time the pocket is used.
            public bool LoggedWrist;
            public bool FromJoint;
            public bool HasElbow;
            public float WristToPalm;
            public float ForearmLength;
        }

        // An object that was in a pocket when the level changed, to be spawned again from its crate and put back in.
        private sealed class Carried
        {
            public string Name;
            public string Barcode;
            public string GripPath;
            public string HostPath;
            public Frame HostInHand;
            public float SpawnAfter;
            public bool Spawning;
            public float GiveUpAt;

            // Once it has spawned, it's put away a moment later, so the game has finished setting it up first.
            public GameObject Spawned;
            public float PutBackAt;

            // The objects of its pool already out before the spawn was asked for.
            public HashSet<IntPtr> Before;
        }

        // Something that just came out of a pocket, to check on after OutingCheckDelay: taken out by a hand, or
        // dropped out beside the pocket by a swap. A second check follows one that found it stuck and freed it.
        private sealed class Outing
        {
            public Stored Item;
            public Hand TakenBy;
            public Vector3 Start;
            public float CheckTime;
            public bool Freed;
        }

        private static readonly HandState[] Hands = { new HandState(), new HandState() };

        // Crates kept loaded for the pockets through the last change of level, so each is logged once.
        private static readonly HashSet<string> KeptCrates = new HashSet<string>();

        // Every entity that's been in a pocket this session, and those that came along to a new level in one, for
        // the log (see CullCheck).
        private static readonly HashSet<IntPtr> EverPocketed = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> CameAlong = new HashSet<IntPtr>();

        // Whether an entity is in a pocket now, or has been, for the log. Null if never.
        public static string HistoryOf(MarrowEntity entity)
        {
            IntPtr key = entity.Pointer;
            for (int i = 0; i < 2; i++)
            {
                Stored item = Pockets[i].Item;
                if (item == null)
                    continue;
                foreach (MarrowEntity stored in item.Entities)
                {
                    if (stored != null && stored.Pointer == key)
                        return $"in the {SideOf(i)} pocket";
                }
            }
            if (CameAlong.Contains(key))
                return "came along from another level in a pocket";
            return EverPocketed.Contains(key) ? "has been in a pocket" : null;
        }
        private static readonly List<Outing> Outings = new List<Outing>();

        // What came out of a pocket, watched at these times after (see WatchOutings).
        private static readonly float[] WatchTimes = { 0.5f, 2.5f, 6f, 12f };
        private static readonly List<(Stored Item, int Stage, float Due)> Watches = new List<(Stored, int, float)>();
        private static string _lastRefusal;
        private static float _lastRefusalTime;
        private static readonly Pocket[] Pockets = { new Pocket(), new Pocket() };
        private static readonly List<(MarrowEntity Entity, bool Prevented, float Due)> CullRestores =
            new List<(MarrowEntity, bool, float)>();
        private static Il2CppStructArray<Vector3> _ringPoints;
        private static Material _ringMaterial;
        private static bool _ringMaterialTried;
        private static bool _ringOnTop;
        private static bool _loggedLevelScene;

        // Whether a hand is busy with a pocket, so its glove leaves it alone: reaching into a full pocket, or still
        // holding the grip it took something out with. 0 is the left hand, 1 the right.
        public static bool IsBusy(int hand) => Hands[hand].Reaching || Hands[hand].Took;

        // Whether BONELAB's built-in force pull should leave this hand alone.
        public static bool Blocks(Hand hand)
        {
            if (hand == null)
                return false;
            Hand left = Player.LeftHand;
            Hand right = Player.RightHand;
            if (left != null && left.Pointer == hand.Pointer)
                return IsBusy(0);
            return right != null && right.Pointer == hand.Pointer && IsBusy(1);
        }

        public static void Update()
        {
            if (CullRestores.Count > 0)
                RestoreCulling();
            if (Watches.Count > 0)
                WatchOutings();
            if (Outings.Count > 0)
                CheckOutings();
            Hand left = Player.LeftHand;
            Hand right = Player.RightHand;
            BaseController leftController = Player.LeftController;
            BaseController rightController = Player.RightController;
            HandChoice wrists = Settings.WristPockets.Value;
            bool on = wrists != HandChoice.Off;
            if (!on || left == null || right == null || leftController == null || rightController == null)
            {
                if (!on)
                    EmptyAll();
                for (int i = 0; i < 2; i++)
                {
                    LockHover(Hands[i], HandAt(i), false);
                    Hands[i].Reaching = false;
                    Hands[i].Took = false;
                    Hands[i].Holding = null;
                    Pockets[i].HasFrame = false;
                    Draw(Pockets[i], i);
                }
                return;
            }

            Pockets[0].HasFrame = TryPlace(Pockets[0], 0, left, leftController);
            Pockets[1].HasFrame = TryPlace(Pockets[1], 1, right, rightController);
            for (int i = 0; i < 2; i++)
            {
                // A wrist whose pocket is turned off drops what's in it beside it, and then has no pocket.
                if (!wrists.Has(i == 1))
                {
                    Empty(i);
                    Pockets[i].Carried = null;
                    Pockets[i].HasFrame = false;
                }
                Pockets[i].Open = false;
                Pockets[i].Swapping = false;
            }
            HeldObjects held = HeldObjects.Read();
            UpdateHand(0, left, leftController, held);
            UpdateHand(1, right, rightController, held);
            for (int i = 0; i < 2; i++)
            {
                CheckStillThere(Pockets[i], i);
                if (Pockets[i].Carried != null)
                    SpawnCarried(Pockets[i], i);
                Draw(Pockets[i], i);
            }
        }

        // Spawns what was in the pocket in the last level, a moment after this one loaded, and puts it back in as
        // soon as it appears. It appears high above the player and is hidden at once.
        private static void SpawnCarried(Pocket pocket, int wrist)
        {
            Carried carried = pocket.Carried;
            float now = Time.unscaledTime;
            if (pocket.Item != null)
            {
                Mod.Trace($"{carried.Name} from the last level wasn't put back in the {SideOf(wrist)} pocket, " +
                            "since something else went in first");
                pocket.Carried = null;
                return;
            }
            if (carried.Spawned != null)
            {
                if (now >= carried.PutBackAt)
                    PutBackCarried(pocket, wrist, carried, carried.Spawned);
                return;
            }
            if (carried.Spawning)
            {
                // In the v0.27 test the spawn callback never came, so the pool it spawns into is watched too.
                GameObject found = NewInPool(carried);
                if (found != null)
                {
                    Mod.Trace($"{carried.Name} turned up in its pool without the spawn callback");
                    carried.Spawned = found;
                    carried.PutBackAt = now + RespawnSettleTime;
                }
                else if (now > carried.GiveUpAt)
                {
                    Mod.TraceWarning($"{carried.Name} from the last level didn't spawn ({carried.Barcode}; " +
                                    $"{SpawnDiagnosis(carried)}), so the {SideOf(wrist)} pocket stays empty");
                    pocket.Carried = null;
                }
                return;
            }
            if (now < carried.SpawnAfter || !pocket.HasFrame || Player.Head == null)
                return;

            carried.Spawning = true;
            carried.GiveUpAt = now + RespawnTimeout;
            carried.Before = PooleesOf(carried.Barcode);
            Vector3 at = Player.Head.position + Vector3.up * RespawnHeight;
            Mod.Trace($"Spawning {carried.Name} ({carried.Barcode}) for the {SideOf(wrist)} pocket");
            try
            {
                HelperMethods.SpawnCrate(carried.Barcode, at, Quaternion.identity, Vector3.one, true,
                    spawned =>
                    {
                        if (pocket.Carried != carried || spawned == null)
                            return;
                        carried.Spawned = spawned;
                        carried.PutBackAt = Time.unscaledTime + RespawnSettleTime;
                    }, null);
            }
            catch (Exception e)
            {
                Mod.Log.Error($"Couldn't spawn {carried.Name} again for the {SideOf(wrist)} pocket: {e.Message}");
                pocket.Carried = null;
            }
        }

        private static void PutBackCarried(Pocket pocket, int wrist, Carried carried, GameObject spawned)
        {
            pocket.Carried = null;
            try
            {
                MarrowEntity entity = spawned != null ? spawned.GetComponentInParent<MarrowEntity>() : null;
                if (entity == null && spawned != null)
                    entity = spawned.GetComponentInChildren<MarrowEntity>();
                if (entity == null)
                {
                    Mod.TraceWarning($"{carried.Name} spawned again without a game entity, so it stays out");
                    return;
                }
                Transform root = entity.transform;
                Transform hostTransform = FindPath(root, carried.HostPath);
                Rigidbody host = hostTransform != null ? hostTransform.GetComponent<Rigidbody>() : null;
                if (host == null)
                    host = entity.GetComponentInChildren<Rigidbody>();
                Transform gripTransform = FindPath(root, carried.GripPath);
                Grip grip = gripTransform != null ? gripTransform.GetComponent<Grip>() : null;
                Target target = host != null ? TargetCache.Get(host) : null;
                if (target == null || grip == null)
                {
                    Mod.TraceWarning($"{carried.Name} spawned again, but its {(target == null ? "body" : "grip")} wasn't " +
                                    "found, so it stays out");
                    return;
                }
                if (pocket.Item != null)
                    return;
                PutAway(pocket, wrist, target, grip, host, carried.HostInHand, Frame.Of(host.transform), true);
                Mod.Trace($"{target.Name} from the last level is back in the {SideOf(wrist)} pocket");
            }
            catch (Exception e)
            {
                Mod.Log.Error($"Couldn't put {carried.Name} back in the {SideOf(wrist)} pocket: {e}");
            }
        }

        // The pool the game spawns a crate's objects into, if there is one yet.
        private static Il2CppSLZ.Marrow.Pool.Pool PoolOf(string barcode)
        {
            try
            {
                Il2CppSLZ.Marrow.Pool.AssetSpawner spawner = Il2CppSLZ.Marrow.Pool.AssetSpawner._instance;
                if (spawner == null || spawner._barcodeToPool == null)
                    return null;
                foreach (var entry in spawner._barcodeToPool)
                {
                    if (entry.Key != null && entry.Key.ID == barcode)
                        return entry.Value;
                }
            }
            catch (Exception)
            {
                // Read again next frame.
            }
            return null;
        }

        // The objects of a crate's pool that are out in the level.
        private static HashSet<IntPtr> PooleesOf(string barcode)
        {
            var found = new HashSet<IntPtr>();
            Il2CppSLZ.Marrow.Pool.Pool pool = PoolOf(barcode);
            if (pool == null || pool._spawned == null)
                return found;
            foreach (Il2CppSLZ.Marrow.Pool.Poolee poolee in pool._spawned)
            {
                if (poolee != null)
                    found.Add(poolee.Pointer);
            }
            return found;
        }

        // An object of the crate's pool that's out now but wasn't before the spawn was asked for.
        private static GameObject NewInPool(Carried carried)
        {
            Il2CppSLZ.Marrow.Pool.Pool pool = PoolOf(carried.Barcode);
            if (pool == null || pool._spawned == null)
                return null;
            foreach (Il2CppSLZ.Marrow.Pool.Poolee poolee in pool._spawned)
            {
                if (poolee != null && (carried.Before == null || !carried.Before.Contains(poolee.Pointer)))
                    return poolee.gameObject;
            }
            return null;
        }

        // What there was to see when a spawn didn't come, for the log.
        private static string SpawnDiagnosis(Carried carried)
        {
            try
            {
                Il2CppSLZ.Marrow.Pool.AssetSpawner spawner = Il2CppSLZ.Marrow.Pool.AssetSpawner._instance;
                if (spawner == null)
                    return "the game's spawner wasn't there";
                Il2CppSLZ.Marrow.Pool.Pool pool = PoolOf(carried.Barcode);
                if (pool == null)
                    return $"no pool for it among the spawner's {spawner._poolList?.Count ?? 0}";
                return $"its pool has {pool._spawned?.Count ?? 0} out and {pool._despawned?.Count ?? 0} waiting, " +
                       $"template {(pool._hasTemplateObject ? "loaded" : pool._isTemplateLoading ? "loading" : "not loaded")}";
            }
            catch (Exception e)
            {
                return $"couldn't look: {e.Message}";
            }
        }

        // A transform by its path from a root, as made by PathFrom. An empty path is the root itself.
        private static Transform FindPath(Transform root, string path) =>
            path == null ? null : path.Length == 0 ? root : root.Find(path);

        // The path of names from a root down to one of its children, or null if it isn't under the root.
        private static string PathFrom(Transform root, Transform child)
        {
            var names = new List<string>();
            for (Transform t = child; t != null; t = t.parent)
            {
                if (t.Pointer == root.Pointer)
                {
                    names.Reverse();
                    return string.Join("/", names);
                }
                names.Add(t.name);
            }
            return null;
        }

        // The barcode of the crate an entity was spawned from, or null if it wasn't spawned from one.
        // Whether a crate is one the pockets need kept loaded through a change of level: the crate of something in a
        // pocket, or of anything plugged into it (see CratePatches). Says so in the log once per crate per level.
        public static bool KeepsCrate(string barcode)
        {
            if (string.IsNullOrEmpty(barcode))
                return false;
            // Called from inside the game's unloading, so nothing may escape from here.
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    Stored item = Pockets[i].Item;
                    if (item == null)
                        continue;
                    foreach (MarrowEntity entity in item.Entities)
                    {
                        if (entity == null || entity.IsDestroyed || BarcodeOf(entity) != barcode)
                            continue;
                        if (KeptCrates.Add(barcode))
                            Mod.Trace($"Kept the crate of {entity.name} loaded for {item.Target.Name} in the {SideOf(i)} pocket");
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"Couldn't check whether a pocket needs the crate {barcode}: {e.Message}");
            }
            return false;
        }

        // A part's mesh that's gone, like after its crate was unloaded, leaves nothing to draw. How many of an
        // object's mesh parts have lost theirs, out of how many, for the log.
        private static (int Missing, int Parts) MissingMeshes(GameObject root)
        {
            if (root == null)
                return (0, 0);
            int missing = 0, parts = 0;
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                parts++;
                if (filter.sharedMesh == null)
                    missing++;
            }
            foreach (SkinnedMeshRenderer skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                parts++;
                if (skinned.sharedMesh == null)
                    missing++;
            }
            return (missing, parts);
        }

        private static string MeshNote(string what, GameObject root)
        {
            (int missing, int parts) = MissingMeshes(root);
            return missing > 0 ? $", {what} missing {missing} of {parts} meshes" : "";
        }

        private static string BarcodeOf(MarrowEntity entity)
        {
            try
            {
                var poolee = entity._poolee;
                var crate = poolee != null ? poolee.SpawnableCrate : null;
                var barcode = crate != null ? crate.Barcode : null;
                string id = barcode != null ? barcode.ID : null;
                return string.IsNullOrEmpty(id) ? null : id;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // A level change takes everything in it, pocketed objects included. Anything spawned from a crate is
        // spawned again in the new level and put back in its pocket (see SpawnCarried), held the same way.
        public static void Clear()
        {
            for (int i = 0; i < 2; i++)
            {
                Pocket pocket = Pockets[i];
                string lostBy = null;
                if (pocket.Item != null && IsIntact(pocket.Item, out lostBy))
                {
                    // Kept out of the level while in the pocket, so it's still here.
                    Stored item = pocket.Item;
                    item.CrossedLevel = true;
                    foreach (MarrowEntity entity in item.Entities)
                    {
                        if (entity != null)
                            CameAlong.Add(entity.Pointer);
                    }
                    int dead = DropDeadReferences(item);
                    MarrowEntity own = item.Entities.Count > 0 ? item.Entities[0] : null;
                    Mod.Trace($"{item.Target.Name} in the {SideOf(i)} pocket came along to the new level" +
                                (dead > 0 ? $", dropping {dead} references to things of the last level" : "") +
                                (item.Ghost != null ? MeshNote("the copy", item.Ghost) : ", its copy is gone") +
                                MeshNote("the object", own != null ? own.gameObject : null));
                    SpawnGunScreen.CameAlong(own != null ? own.gameObject : null);
                    item.Placement = Placement.Pocket;
                }
                else if (pocket.Item != null)
                {
                    Stored item = pocket.Item;
                    string name = item.Target.Name;
                    Mod.Trace($"{name} in the {SideOf(i)} pocket didn't survive the level change: {lostBy}");
                    if (Settings.PocketKeep.Value && item.Barcode != null)
                    {
                        pocket.Carried = new Carried
                        {
                            Name = name,
                            Barcode = item.Barcode,
                            GripPath = item.GripPath,
                            HostPath = item.HostPath,
                            HostInHand = item.HostInHand,
                        };
                        Mod.Trace($"{name} was in the {SideOf(i)} pocket when the level changed, so it's spawned " +
                                    $"again ({item.Barcode})");
                    }
                    else
                    {
                        string why = !Settings.PocketKeep.Value ? "\"Keep between levels\" is off"
                            : "it's part of the level, not spawned from a crate";
                        Mod.Trace($"{name} was in the {SideOf(i)} pocket when the level changed, and didn't come " +
                                    $"along: {why}");
                    }
                    DestroyVisuals(item);
                    pocket.Item = null;
                }
                if (pocket.Carried != null)
                {
                    pocket.Carried.Spawning = false;
                    pocket.Carried.SpawnAfter = Time.unscaledTime + RespawnDelay;
                }
                pocket.WristJoint = null;
                pocket.NextJointSearch = 0f;
                LockHover(Hands[i], HandAt(i), false);
                Hands[i] = new HandState();
            }
            CullRestores.Clear();
            Outings.Clear();
            Watches.Clear();
            PlayerBody.Clear();
            KeptCrates.Clear();
        }

        private static void UpdateHand(int index, Hand hand, BaseController controller, HeldObjects held)
        {
            HandState state = Hands[index];
            int wrist = 1 - index;
            Pocket pocket = Pockets[wrist];
            float curl = controller.GetMiddleCurlAxis();
            bool gripHeld = state.GripHeld ? curl > ReleaseThreshold : curl >= PressThreshold;
            bool gripPressed = gripHeld && !state.GripHeld;
            state.GripHeld = gripHeld;
            if (!gripHeld)
                state.Took = false;
            float reach = Settings.PocketReach.Value * BodyScale.Avatar;
            float takeReach = Settings.PocketTakeReach.Value * BodyScale.Avatar;

            if (hand.HasAttachedObject())
            {
                state.Reaching = false;
                state.Took = false;
                LockHover(state, hand, false);
                bool wasAtPocket = state.AtPocket;
                NoteHeld(state, hand, HandAt(wrist), pocket, reach);
                if (state.AtPocket)
                {
                    pocket.Open = true;
                    pocket.Swapping = pocket.Item != null;
                    // A tick as it comes into the pocket, so you know letting go will put it away, then a slight
                    // hum and sparks drawn from it into the pocket while it's there, as in Half-Life: Alyx.
                    if (!wasAtPocket)
                        Feedback.Tick(controller);
                    else
                        Feedback.PocketHum(index, controller);
                    PullInStream(pocket, state.Holding);
                }
                return;
            }

            // Just let go of something at the pocket: put it away, unless the other hand still has it. Whatever
            // was in the pocket drops out beside it first.
            if (state.Holding != null)
            {
                Target released = state.Holding;
                if (state.AtPocket && pocket.HasFrame && released.IsAlive && !held.Contains(released))
                {
                    if (pocket.Item != null)
                        Evict(pocket, wrist, released);
                    if (pocket.Item == null)
                        Store(pocket, wrist, controller, state);
                }
                state.Holding = null;
                state.AtPocket = false;
                state.Checked = null;
                state.CheckedHost = null;
                state.NoGripSince = -1f;
                state.LoggedNoGrip = false;
            }

            Vector3 palm = GloveHand.PalmPosition(hand);
            float reachNow = state.Reaching ? takeReach * ReachHysteresis : takeReach;
            bool inReach = pocket.HasFrame && Vector3.Distance(palm, pocket.Center) <= ReachToward(pocket, palm, reachNow);
            if (!inReach)
                state.WaitForLeave = false;
            // A hand that's already about to grab something else there, like the body log, gets it: the pocket
            // only starts reaching if the hand hovers nothing but the player's own arm. Once reaching, the hover is
            // locked, so the arm can't be grabbed instead (v0.23).
            bool wantsOther = !state.Reaching && HoversSomethingElse(hand);
            state.Reaching = pocket.Item != null && inReach && !state.WaitForLeave && !state.Took && !wantsOther &&
                             (!gripHeld || gripPressed);
            if (state.Reaching && gripPressed && TryTake(pocket, wrist, hand))
            {
                state.Reaching = false;
                state.Took = true;
                // Grab assist holds the hover now, until the hand has the object.
                state.HoverLocked = false;
            }
            if (state.Reaching)
                Feedback.PocketHum(index, controller);
            LockHover(state, hand, state.Reaching);
        }

        // Sparks drawn into the pocket from all over the object at it, more of them the bigger the setting.
        private static void PullInStream(Pocket pocket, Target target)
        {
            float amount = Settings.PocketEffect.Value / 100f;
            if (amount <= 0f || target == null || !target.TryGetBounds(out Bounds bounds))
                return;
            pocket.EffectDebt += Time.unscaledDeltaTime * PullInRate * amount;
            Color color = Palette.Pocket(new Color(1f, 0.75f, 0.45f, 0.9f));
            while (pocket.EffectDebt >= 1f)
            {
                pocket.EffectDebt -= 1f;
                Sparks.Toward(RandomPointIn(bounds), pocket.Center, PullInTime * (0.7f + 0.6f * Sparks.RandomValue()),
                    color);
            }
        }

        // A burst of sparks rushing into the pocket from where the object was, as it goes in.
        private static void PullInBurst(Pocket pocket, Target target)
        {
            float amount = Settings.PocketEffect.Value / 100f;
            if (amount <= 0f || target == null || !target.TryGetBounds(out Bounds bounds))
                return;
            int count = Mathf.RoundToInt(PullInBurstCount * amount);
            Color color = Palette.Pocket(new Color(1f, 0.8f, 0.55f, 1f));
            for (int i = 0; i < count; i++)
            {
                Sparks.Toward(RandomPointIn(bounds), pocket.Center, AbsorbTime * (0.6f + 0.6f * Sparks.RandomValue()),
                    color);
            }
        }

        private static Vector3 RandomPointIn(Bounds bounds) =>
            bounds.center + Vector3.Scale(bounds.extents, new Vector3(
                Sparks.RandomValue() * 2f - 1f, Sparks.RandomValue() * 2f - 1f, Sparks.RandomValue() * 2f - 1f));

        // While reaching into a full pocket the hand hovers nothing else, so the grab there takes the object out
        // and not whatever's nearby. In v0.22 a hand reaching into the pocket on the other wrist sometimes grabbed
        // that arm instead (the player rig's sphere grip), and once the arm was put in the pocket, taking the
        // whole player with it (William). Grab assist uses the same lock.
        private static void LockHover(HandState state, Hand hand, bool locked)
        {
            if (locked == state.HoverLocked)
                return;
            state.HoverLocked = locked;
            if (hand == null)
                return;
            try
            {
                if (locked)
                {
                    HandReciever hovered = hand.HoveringReceiver;
                    if (hovered != null)
                    {
                        hovered.OnHandHoverEnd(hand);
                        hand._mHoveringReceiver = null;
                    }
                    hand.HoverLock();
                }
                else
                {
                    hand.HoverUnlock();
                }
            }
            catch (Exception e)
            {
                Mod.Log.Error($"Couldn't {(locked ? "lock" : "unlock")} a hand's hover for a wrist pocket: {e.Message}");
            }
        }

        // Remembers what the hand holds and how, and whether it's at the pocket and can go in. Something the
        // pocket's own hand is holding too, like a pistol held in both hands, doesn't count.
        private static void NoteHeld(HandState state, Hand hand, Hand pocketHand, Pocket pocket, float reach)
        {
            state.AtPocket = false;
            // The object is the one the grip is on. Going by the hand's attached object instead could give a
            // different entity than the grip's, and then nothing went in, without a word in the log.
            HandReciever receiver = hand.AttachedReceiver;
            Grip grip = receiver != null ? receiver.TryCast<Grip>() : null;
            Rigidbody host = grip != null ? grip.GetComponentInParent<Rigidbody>() : null;
            Target target = host != null ? TargetCache.Get(host) : HeldTarget(hand);
            state.Holding = target;
            if (target == null || target.IsOnPlayer)
                return;

            // Right after a grab the hand can hold an object for a moment before it's on one of its grips, and
            // nothing is decided until it is. In v0.24 and v0.25 that moment's "held by nothing" was kept for the
            // whole hold, so something just caught or taken out of a pocket wouldn't go in, and a hand that came off
            // its grip for a moment logged an empty reason (William's "grabbed wrong, so I can't put it in").
            if (host == null)
            {
                float now = Time.unscaledTime;
                if (state.NoGripSince < 0f)
                    state.NoGripSince = now;
                if (!state.LoggedNoGrip && now - state.NoGripSince >= NoGripLogDelay && IsNear(target, pocket, reach))
                {
                    state.LoggedNoGrip = true;
                    Mod.Trace($"{target.Name} won't go in a wrist pocket: it's held by " +
                                $"{(receiver != null ? receiver.name : "nothing")}, which isn't a grip on a body");
                }
                return;
            }
            state.NoGripSince = -1f;
            if (state.Checked != target || state.CheckedHost == null || state.CheckedHost.Pointer != host.Pointer)
            {
                state.Checked = target;
                state.CheckedHost = host;
                state.Unstorable = null;
                state.LoggedUnstorable = false;
                state.Measured = false;
            }
            state.Grip = grip;
            state.Host = host;
            state.HostInHand = Frame.Of(hand.transform).Inverse().Then(Frame.Of(host.transform));
            if (!pocket.HasFrame || HeldObjects.KeyOf(pocketHand) == target.Key)
                return;

            // It goes in from the face of the pocket or the sides, not from under the arm (William, v0.26): its
            // nearest point has to be no further below the pocket's face than UndersideMargin.
            Vector3 nearest = target.SurfacePointToward(pocket.Center);
            if (Vector3.Distance(nearest, pocket.Center) > ReachToward(pocket, nearest, reach) ||
                Vector3.Dot(nearest - pocket.Center, pocket.Outward) < -UndersideMargin * BodyScale.Avatar)
            {
                return;
            }

            if (!state.Measured)
            {
                state.Measured = true;
                state.Unstorable = WhyNotStorable(target, host);
            }
            if (state.Unstorable != null)
            {
                LogUnstorable(state, target, pocket, reach);
                return;
            }
            state.AtPocket = true;
            state.Hold = GripHold.Capture(grip, hand);
        }

        // Says once per hold why something at a pocket won't go in, and the same thing about the same object only
        // once in a while: in v0.25 grabbing a windmill again and again logged it 26 times in 40 seconds.
        private static void LogUnstorable(HandState state, Target target, Pocket pocket, float reach)
        {
            if (state.LoggedUnstorable || !IsNear(target, pocket, reach))
                return;
            state.LoggedUnstorable = true;
            string line = $"{target.Name} won't go in a wrist pocket: {state.Unstorable}";
            float now = Time.unscaledTime;
            if (line == _lastRefusal && now - _lastRefusalTime < RefusalRepeat)
                return;
            _lastRefusal = line;
            _lastRefusalTime = now;
            Mod.Trace(line);
        }

        private static bool IsNear(Target target, Pocket pocket, float reach)
        {
            if (!pocket.HasFrame)
                return false;
            Vector3 nearest = target.SurfacePointToward(pocket.Center);
            return Vector3.Distance(nearest, pocket.Center) <= ReachToward(pocket, nearest, reach);
        }

        // The reach toward a point: all of it straight out of the pocket's face, the side reach share of it from
        // the side, and less again from below, so the pocket reaches out from the wrist like a cone rather than a
        // ball (William, v0.27). The face is the wrist's own outward side, not the way the ring turns to face you.
        private static float ReachToward(Pocket pocket, Vector3 point, float reach)
        {
            Vector3 offset = point - pocket.Center;
            float distance = offset.magnitude;
            if (distance < 1e-4f)
                return reach;
            float cos = Vector3.Dot(offset / distance, pocket.Outward);
            float side = Settings.PocketSideReach.Value / 100f;
            float share = cos >= 0f ? side + (1f - side) * cos : side * (1f + cos);
            return reach * share;
        }

        // Whether the hand is hovering something it could grab, other than the player's own arm, whose sphere grips
        // the other hand is always near at a wrist.
        private static bool HoversSomethingElse(Hand hand)
        {
            HandReciever hovered = hand.HoveringReceiver;
            if (hovered == null)
                return false;
            Grip grip = hovered.TryCast<Grip>();
            bool armGrip = grip != null && grip.TryCast<SphereGrip>() != null && grip.GetComponentInParent<PhysicsRig>() != null;
            return !armGrip;
        }

        private static Target HeldTarget(Hand hand)
        {
            GameObject held = Player.GetObjectInHand(hand);
            Rigidbody rb = held != null ? held.GetComponentInParent<Rigidbody>() : null;
            return rb != null ? TargetCache.Get(rb) : null;
        }

        // Why an object can't go in a pocket, or null if it can. Only the object's own plugs count: a magazine
        // inside a gun sits among the gun's parts, and in v0.21 its plug made the gun count as plugged in, so a gun
        // that had been given a magazine wouldn't go back in (William).
        //
        // Weapons (guns, melee weapons and anything that fits the game's holsters) always fit, whatever their size
        // (William, v0.31). The max size decides what else goes in.
        private static string WhyNotStorable(Target target, Rigidbody host)
        {
            MarrowEntity entity = target.Entity;
            if (target.IsOnPlayer)
                return "it's part of a player";
            if (entity == null)
                return "it isn't a game entity, so it can't be hidden";
            if (target.IsKinematic)
                return "it's fixed in place";
            float maxSize = Settings.PocketMaxSize.Value / 100f * BodyScale.Value;
            string largest = null;
            float size = target.IsWeapon ? 0f : ObjectModel.MeasureSize(target, host.transform, out largest);
            if (size > maxSize)
                return $"it's {size * 100f:0} cm long, more than the pocket max size of {maxSize * 100f:0} cm (largest part {largest})";
            foreach (AlignPlug plug in target.RootObject.GetComponentsInChildren<AlignPlug>())
            {
                if (plug == null || !plug._isLocked)
                    continue;
                MarrowEntity owner = plug.GetComponentInParent<MarrowEntity>();
                if (owner != null && owner.Pointer == entity.Pointer)
                    return "it's plugged into something, like a magazine in a gun";
            }
            return null;
        }

        private static void Store(Pocket pocket, int wrist, BaseController controller, HandState state)
        {
            Target target = state.Holding;
            Stored item = PutAway(pocket, wrist, target, state.Grip, state.Host, state.HostInHand,
                Frame.Of(state.Host.transform), false);
            if (item == null)
                return;
            item.Hold = state.Hold;
            Hands[1 - wrist].WaitForLeave = true;
            Feedback.Play(FeedbackKind.Lock, controller, pocket.Center);
            PullInBurst(pocket, target);
        }

        // Hides the object and everything plugged into it in the pocket, with a see-through copy of how it looks.
        // The copy starts where it was let go and shrinks into the pocket, or, for something brought back from the
        // last level, is in the pocket at once. Null if it couldn't be hidden.
        private static Stored PutAway(Pocket pocket, int wrist, Target target, Grip grip, Rigidbody host,
            Frame hostInHand, Frame released, bool fromLastLevel)
        {
            var item = new Stored
            {
                Target = target,
                Grip = grip,
                Host = host,
                HostInHand = hostInHand,
                Barcode = BarcodeOf(target.Entity),
                GripPath = grip != null ? PathFrom(target.Entity.transform, grip.transform) : null,
                HostPath = PathFrom(target.Entity.transform, host.transform),
                Placement = Placement.Pocket,
                MoveStart = Time.unscaledTime,
                MoveDuration = fromLastLevel ? 0f : AbsorbTime,
                FromPosition = released.Position,
                FromRotation = released.Rotation,
                FromScale = 1f,
                FromAlpha = HandAlpha,
                GhostPosition = released.Position,
                GhostRotation = released.Rotation,
                GhostScale = 1f,
                GhostAlpha = HandAlpha,
            };
            item.Entities.Add(target.Entity);
            AddPluggedIn(item.Entities);
            foreach (MarrowEntity entity in item.Entities)
                EverPocketed.Add(entity.Pointer);
            NoteBodies(item);
            var roots = new List<GameObject>();
            Frame toHost = released.Inverse();
            foreach (MarrowEntity entity in item.Entities)
            {
                roots.Add(entity.gameObject);
                item.EntitiesInHost.Add(toHost.Then(Frame.Of(entity.transform)));
            }

            // Its look is copied before it's hidden.
            try
            {
                item.Model = ObjectModel.Capture(target, roots, host.transform);
                item.LyingFlat = LyingFlat(item.Model.Bounds);
                item.GhostMaterial = GlowMaterials.Create(Settings.GlowStyle.Value);
                if (item.GhostMaterial != null)
                    item.Ghost = item.Model.Build(PocketName + " Copy", item.GhostMaterial);
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"Couldn't copy how {target.Name} looks for its pocket: {e.Message}");
            }

            var hidden = new List<string>();
            try
            {
                foreach (MarrowEntity entity in item.Entities)
                {
                    StopMoving(entity);
                    item.WasCullPrevented.Add(entity._isDisableOnCullPrevented);
                    entity.PreventDisableOnCull(true);
                    hidden.Add(HideEntity(entity) ? $"{entity.name} (switched off directly)" : entity.name);
                }
            }
            catch (Exception e)
            {
                // Whatever got hidden comes back, so nothing is lost.
                Mod.Log.Error($"Couldn't hide {target.Name} in a wrist pocket, so it stays out: {e}");
                ShowEntities(item, released);
                DestroyVisuals(item);
                return null;
            }

            MoveOutOfLevel(item);
            pocket.Item = item;
            pocket.Flash = 1f;
            if (fromLastLevel)
                return item;
            string parts = item.Model != null ? $"{item.Model.PartCount} meshes, {item.Model.Size * 100f:0} cm" : "no copy";
            string with = item.Entities.Count > 1 ? $", with {string.Join(", ", hidden.GetRange(1, hidden.Count - 1))}" : "";
            int kept = 0;
            foreach (bool moved in item.MovedOut)
                kept += moved ? 1 : 0;
            Mod.Trace($"{Side(1 - wrist)} hand put {target.Name} in the {SideOf(wrist)} pocket, held by " +
                        $"{(item.Grip != null ? item.Grip.name : "?")} ({parts}){with}. Hidden: {hidden[0]}; " +
                        $"{kept} of {item.Entities.Count} kept out of the level, crate {item.Barcode ?? "none"}");
            return item;
        }

        // Swapping: the object in the pocket comes out where its copy waited beside the pocket, and drops.
        private static void Evict(Pocket pocket, int wrist, Target newcomer)
        {
            Stored item = pocket.Item;
            if (!IsIntact(item, out string gone))
            {
                Forget(pocket, wrist, gone);
                return;
            }
            try
            {
                // It comes out where its copy was shown, if that's clear of the body, so it doesn't jump. Otherwise
                // where it's clear.
                Frame pose = SwapPose(pocket, item, out float clearing);
                if (item.Placement == Placement.Beside)
                {
                    Frame shown = SwapPoseAt(pocket, item, pocket.ShownClearing, out bool clear);
                    if (clear)
                    {
                        pose = shown;
                        clearing = pocket.ShownClearing;
                    }
                }
                if (item.CrossedLevel)
                    SpawnGunScreen.Ensure(item.Target.RootObject);
                ShowEntities(item, pose);
                Outings.Add(new Outing { Item = item, Start = pose.Position, CheckTime = Time.unscaledTime + OutingCheckDelay });
                Watch(item);
                string moved = clearing > 0f ? $", moved {clearing * 100f:0} cm further out to clear your body" : "";
                Mod.Trace($"{item.Target.Name} dropped out of the {SideOf(wrist)} pocket to make room for {newcomer.Name}{moved}");
            }
            catch (Exception e)
            {
                Mod.Log.Error($"Couldn't swap {item.Target.Name} out of the {SideOf(wrist)} pocket: {e}");
            }
            DestroyVisuals(item);
            pocket.Item = null;
        }

        private static bool TryTake(Pocket pocket, int wrist, Hand hand)
        {
            Stored item = pocket.Item;
            if (hand.HasAttachedObject())
                return false;
            string where = $"the {SideOf(wrist)} pocket";
            if (!IsIntact(item, out string gone))
            {
                Forget(pocket, wrist, gone);
                return false;
            }

            Frame hostPose = Frame.Of(hand.transform).Then(item.HostInHand);
            string note = "";
            try
            {
                if (item.CrossedLevel)
                    SpawnGunScreen.Ensure(item.Target.RootObject);
                ShowEntities(item, hostPose);
                float off = Vector3.Distance(item.Host.transform.position, hostPose.Position);
                if (off > MaxTeleportError)
                {
                    PlaceEntities(item, hostPose);
                    note = $"; the teleport put it {off * 100f:0} cm off, so it was placed directly";
                }
                if (!item.Target.IsActive)
                {
                    Mod.TraceWarning($"{item.Target.Name} didn't come back out of {where}, so it stays in");
                    HideEntities(item);
                    return false;
                }
                Rigidbody handBody = hand.rb;
                item.Target.SetVelocity(handBody != null ? handBody.velocity : Vector3.zero, true);
                if (!GrabAssist.StartInPlace(item.Target, hand, item.Grip, item.Host, item.HostInHand, item.Hold, out string grab))
                {
                    Mod.Trace($"Couldn't grab {item.Target.Name} out of {where}, so it stays in");
                    HideEntities(item);
                    return false;
                }
                Mod.Trace($"{Side(1 - wrist)} hand took {item.Target.Name} out of {where} {grab}{note}");
                if (Settings.PocketPassThrough.Value)
                    PassThrough.Begin(item.Target, item.Entities);
                Outings.Add(new Outing
                {
                    Item = item,
                    TakenBy = hand,
                    Start = hostPose.Position,
                    CheckTime = Time.unscaledTime + OutingCheckDelay,
                });
                Watch(item);
            }
            catch (Exception e)
            {
                // Better out in the world than lost in the pocket.
                Mod.Log.Error($"Taking {item.Target.Name} out of {where} failed, so it's left where it came out: {e}");
            }
            pocket.Flash = 1f;
            DestroyVisuals(item);
            pocket.Item = null;
            return true;
        }

        // Puts back everything in the pockets, beside them, for when the pockets are turned off.
        private static void EmptyAll()
        {
            for (int i = 0; i < 2; i++)
                Empty(i);
        }

        // Drops what's in a pocket out beside it, for when the pocket is turned off.
        private static void Empty(int wrist)
        {
            Pocket pocket = Pockets[wrist];
            Stored item = pocket.Item;
            if (item == null)
                return;
            if (IsIntact(item, out _))
            {
                Frame at = pocket.HasFrame ? SwapPose(pocket, item, out _) : new Frame(item.GhostPosition, item.GhostRotation);
                try
                {
                    ShowEntities(item, at);
                    Mod.Trace($"{item.Target.Name} dropped out of the {SideOf(wrist)} pocket, since it was turned off");
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"Couldn't drop {item.Target.Name} out of the {SideOf(wrist)} pocket: {e}");
                }
            }
            DestroyVisuals(item);
            pocket.Item = null;
        }

        // An object can vanish from a pocket without the pocket letting it go, if the game despawns it.
        private static void CheckStillThere(Pocket pocket, int wrist)
        {
            if (pocket.Item != null && !IsIntact(pocket.Item, out string gone))
                Forget(pocket, wrist, gone);
        }

        private static bool IsIntact(Stored item, out string gone)
        {
            MarrowEntity entity = item.Entities.Count > 0 ? item.Entities[0] : null;
            if (!item.Target.IsAlive || entity == null || entity.IsDestroyed)
                gone = "it was destroyed";
            else if (entity.IsDespawned)
                gone = "the game despawned it";
            else if (item.Target.IsActive)
                gone = "something brought it back out";
            else
                gone = null;
            return gone == null;
        }

        private static void Forget(Pocket pocket, int wrist, string why)
        {
            Mod.Trace($"The {SideOf(wrist)} pocket lost {pocket.Item.Target.Name}: {why}");
            DestroyVisuals(pocket.Item);
            pocket.Item = null;
        }

        // Entities plugged into the object, like a gun's magazine, and anything plugged into those, plus any
        // packed into it. The list grows as it goes.
        private static void AddPluggedIn(List<MarrowEntity> entities)
        {
            for (int i = 0; i < entities.Count; i++)
            {
                MarrowEntity entity = entities[i];
                foreach (Socket socket in entity.GetComponentsInChildren<Socket>())
                {
                    Plug plug = socket != null ? socket.LockedPlug : null;
                    AddOnce(entities, plug != null ? plug.GetComponentInParent<MarrowEntity>() : null);
                }
                var parasites = entity._parasites;
                if (parasites == null)
                    continue;
                for (int p = 0; p < parasites.Count; p++)
                    AddOnce(entities, parasites[p]);
            }
        }

        private static void AddOnce(List<MarrowEntity> entities, MarrowEntity entity)
        {
            if (entity == null)
                return;
            foreach (MarrowEntity known in entities)
            {
                if (known.Pointer == entity.Pointer)
                    return;
            }
            entities.Add(entity);
        }

        private static void StopMoving(MarrowEntity entity)
        {
            var bodies = entity.Bodies;
            if (bodies == null)
                return;
            foreach (MarrowBody body in bodies)
            {
                if (body == null || !body.HasRigidbody)
                    continue;
                Rigidbody rb = body._rigidbody;
                if (rb == null || rb.isKinematic)
                    continue;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        // Hides an entity the game's way. If that leaves it switched on, it's switched off directly. Returns
        // whether it had to be.
        private static bool HideEntity(MarrowEntity entity)
        {
            entity.Hide(true, true);
            if (!entity.gameObject.activeSelf)
                return false;
            entity.gameObject.SetActive(false);
            return true;
        }

        private static void HideEntities(Stored item)
        {
            foreach (MarrowEntity entity in item.Entities)
            {
                if (entity == null)
                    continue;
                StopMoving(entity);
                entity.PreventDisableOnCull(true);
                HideEntity(entity);
            }
            MoveOutOfLevel(item);
        }

        // Brings the object and everything plugged into it back, with the body it's held by at the given pose. One
        // that fails doesn't stop the rest. Whether the game may switch them off when their area is culled goes
        // back to how it was a little later, once the game has seen them where they are now.
        private static void ShowEntities(Stored item, Frame hostPose)
        {
            if (item.CrossedLevel)
            {
                int dead = DropDeadReferences(item);
                if (dead > 0)
                    Mod.Trace($"{item.Target.Name} dropped {dead} more references to things of the last level as it came out");
            }
            BackIntoLevel(item);
            for (int i = 0; i < item.Entities.Count; i++)
            {
                MarrowEntity entity = item.Entities[i];
                try
                {
                    if (entity == null || entity.IsDestroyed)
                        continue;
                    entity.Hide(false, true);
                    if (!entity.gameObject.activeSelf)
                        entity.gameObject.SetActive(true);
                    Frame pose = hostPose.Then(item.EntitiesInHost[i]);
                    entity.Teleport(pose.Position, pose.Rotation, false);
                    if (i < item.WasCullPrevented.Count)
                        CullRestores.Add((entity, item.WasCullPrevented[i], Time.unscaledTime + CullRestoreDelay));
                }
                catch (Exception e)
                {
                    Mod.Log.Error($"Couldn't bring {(entity != null ? entity.name : "a part")} back out of a wrist pocket: {e.Message}");
                }
            }
            Unfreeze(item, out _);
        }

        // Remembers how each body of the stored entities could move when it went in.
        private static void NoteBodies(Stored item)
        {
            foreach (MarrowEntity entity in item.Entities)
            {
                var bodies = entity != null ? entity.Bodies : null;
                if (bodies == null)
                    continue;
                foreach (MarrowBody body in bodies)
                {
                    Rigidbody rb = body != null && body.HasRigidbody ? body._rigidbody : null;
                    if (rb != null)
                        item.Bodies.Add((rb, rb.isKinematic, rb.constraints));
                }
            }
        }

        // Makes sure what came out can move: the game can leave an entity hibernating, which freezes its bodies
        // where they are, and an object that came out of a pocket got stuck in the air in the v0.25 test (William).
        // Most likely that was the game's culling, which hibernates instead of switching off an entity kept from
        // being switched off, as pocketed ones are. Every body gets back how it could move when it went in, and is
        // woken up. Says what had to be undone, or null if nothing.
        private static string Unfreeze(Stored item, out bool changed)
        {
            changed = false;
            var undone = new List<string>();
            foreach (MarrowEntity entity in item.Entities)
            {
                if (entity == null || entity.IsDestroyed)
                    continue;
                try
                {
                    if (entity.IsHibernating)
                    {
                        entity.ClearHibernation();
                        undone.Add($"{entity.name} was hibernating");
                    }
                }
                catch (Exception e)
                {
                    Mod.Log.Warning($"Couldn't wake {entity.name} after a wrist pocket: {e.Message}");
                }
            }
            int kinematic = 0;
            int constrained = 0;
            foreach ((Rigidbody rb, bool wasKinematic, RigidbodyConstraints constraints) in item.Bodies)
            {
                if (rb == null)
                    continue;
                if (rb.isKinematic != wasKinematic)
                {
                    rb.isKinematic = wasKinematic;
                    kinematic++;
                }
                if (rb.constraints != constraints)
                {
                    rb.constraints = constraints;
                    constrained++;
                }
                if (!rb.isKinematic)
                    rb.WakeUp();
            }
            if (kinematic > 0)
                undone.Add($"{kinematic} bodies were kinematic");
            if (constrained > 0)
                undone.Add($"{constrained} bodies were frozen");
            changed = undone.Count > 0;
            return changed ? string.Join(", ", undone) : null;
        }

        // Checks on what came out of a pocket a moment ago. Taken out by a hand: how the hand holds it compared
        // with how it was put away, since William found take-outs sometimes held weirdly. Otherwise, whether it's
        // falling, or stuck where it came out, in which case it's freed and checked again.
        private static void CheckOutings()
        {
            float now = Time.unscaledTime;
            HeldObjects held = default;
            bool read = false;
            for (int i = Outings.Count - 1; i >= 0; i--)
            {
                Outing outing = Outings[i];
                if (now < outing.CheckTime)
                    continue;
                Outings.RemoveAt(i);
                Stored item = outing.Item;
                Target target = item.Target;
                if (!target.IsAlive || item.Host == null)
                    continue;
                if (!read)
                {
                    held = HeldObjects.Read();
                    read = true;
                }
                try
                {
                    if (outing.TakenBy != null && HeldObjects.KeyOf(outing.TakenBy) == target.Key)
                    {
                        LogTakeOutHold(outing, item);
                        continue;
                    }
                    if (held.Contains(target))
                        continue;
                    float moved = Vector3.Distance(item.Host.position, outing.Start);
                    string how = outing.TakenBy != null ? "taken out of a pocket but not held" : "swapped out of a pocket";
                    if (moved > StuckDistance)
                    {
                        if (outing.Freed)
                            Mod.Trace($"{target.Name} is moving again, {moved * 100f:0} cm since it was freed");
                        continue;
                    }
                    if (outing.Freed)
                    {
                        Mod.TraceWarning($"{target.Name} is still stuck after being freed: {Describe(item)}");
                        continue;
                    }
                    string undone = Unfreeze(item, out _);
                    target.WakeUp();
                    Mod.Trace($"{target.Name}, {how}, hadn't moved {OutingCheckDelay:0.0} s later ({Describe(item)})" +
                                (undone != null ? $". Freed it: {undone}" : ". Nothing was holding it still, so it may be resting on something"));
                    if (undone != null)
                    {
                        Outings.Add(new Outing
                        {
                            Item = item,
                            Start = item.Host.position,
                            CheckTime = now + OutingCheckDelay,
                            Freed = true,
                        });
                    }
                }
                catch (Exception e)
                {
                    Mod.Log.Warning($"Couldn't check on {target.Name} after a wrist pocket: {e.Message}");
                }
            }
        }

        // Watches what came out of a pocket for a while: every part of it, the gun and its magazine and cartridges,
        // should be active again. In the v0.27 test weapons sometimes stopped working or froze in the air after
        // coming out (William). That's what a culled entity kept from being switched off looks like: its scripts
        // stop and its bodies are frozen, but it stays visible. A part found culled, hibernating or still hidden is
        // logged and woken up.
        private static void WatchOutings()
        {
            float now = Time.unscaledTime;
            for (int i = Watches.Count - 1; i >= 0; i--)
            {
                (Stored item, int stage, float due) = Watches[i];
                if (now < due)
                    continue;
                Watches.RemoveAt(i);
                if (!item.Target.IsAlive || IsInPocket(item.Entities.Count > 0 ? item.Entities[0] : null))
                    continue;
                try
                {
                    var problems = new List<string>();
                    foreach (MarrowEntity entity in item.Entities)
                    {
                        if (entity == null || entity.IsDestroyed || entity.IsDespawned)
                            continue;
                        var flags = new List<string>();
                        if (entity.IsHidden)
                            flags.Add("hidden");
                        if (entity.IsCulled)
                            flags.Add("culled");
                        if (entity.IsHibernating)
                            flags.Add("hibernating");
                        if (!entity.gameObject.activeInHierarchy)
                            flags.Add("switched off");
                        if (flags.Count == 0)
                            continue;
                        problems.Add($"{entity.name} {string.Join(", ", flags)}");
                        Revive(entity);
                    }
                    if (problems.Count > 0)
                    {
                        string undone = Unfreeze(item, out _);
                        Mod.TraceWarning($"{item.Target.Name}, {WatchTimes[stage]:0.0} s out of its pocket: " +
                                        $"{string.Join("; ", problems)}. Woke it up{(undone != null ? $" ({undone})" : "")}");
                    }
                }
                catch (Exception e)
                {
                    Mod.Log.Warning($"Couldn't check on {item.Target.Name} after a wrist pocket: {e.Message}");
                }
                if (stage + 1 < WatchTimes.Length)
                    Watches.Add((item, stage + 1, now + WatchTimes[stage + 1] - WatchTimes[stage]));
            }
        }

        private static void Watch(Stored item) => Watches.Add((item, 0, Time.unscaledTime + WatchTimes[0]));

        // Takes the references an entity carried to a new level still holds to things that were destroyed with the
        // last one out of its lists: its own parts, and what registered with it to be culled, hidden, despawned or
        // posed, like parts of the last level's zones. The game goes through these lists when it culls or hides the
        // entity. In the v0.32 test, after a traffic cone came along from the menu to Street Puncher, the game's
        // culling logged a NullReferenceException in MarrowEntity.OnCullResolve about every 2 seconds for the whole
        // level (386 of them, against 4 in the session before), and William found the things he'd carried weren't
        // there. Returns how many it took out.
        private static int DropDeadReferences(Stored item)
        {
            int dropped = 0;
            foreach (MarrowEntity entity in item.Entities)
            {
                try
                {
                    if (entity == null || entity.IsDestroyed)
                        continue;
                    Il2CppReferenceArray<Il2CppSLZ.Marrow.Utilities.MarrowBehaviour> behaviours = entity._behaviours;
                    if (behaviours != null)
                    {
                        var alive = new List<Il2CppSLZ.Marrow.Utilities.MarrowBehaviour>();
                        for (int i = 0; i < behaviours.Length; i++)
                        {
                            if (behaviours[i] != null)
                                alive.Add(behaviours[i]);
                        }
                        if (alive.Count < behaviours.Length)
                        {
                            dropped += behaviours.Length - alive.Count;
                            entity._behaviours = new Il2CppReferenceArray<Il2CppSLZ.Marrow.Utilities.MarrowBehaviour>(alive.ToArray());
                        }
                    }
                    dropped += DropDead(entity._cullables) + DropDead(entity._hideables) +
                               DropDead(entity._despawnables) + DropDead(entity._poseables) + DropDead(entity._parasites);
                }
                catch (Exception e)
                {
                    Mod.Log.Warning($"Couldn't check {item.Target.Name}'s references to the last level: {e.Message}");
                }
            }
            return dropped;
        }

        private static int DropDead<T>(Il2CppSystem.Collections.Generic.List<T> list) where T : Il2CppObjectBase
        {
            if (list == null)
                return 0;
            int dropped = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                T entry = list[i];
                // A destroyed Unity object still has its wrapper, so it's asked whether it's still there.
                Object unity = ReferenceEquals(entry, null) ? null : entry.TryCast<Object>();
                bool dead = ReferenceEquals(entry, null) || (!ReferenceEquals(unity, null) && unity == null);
                if (!dead)
                    continue;
                list.RemoveAt(i);
                dropped++;
            }
            return dropped;
        }

        // Makes an entity active again: shown, counted in the zones it's in, not hibernating, and not switched off
        // if the game still counts it as culled.
        private static void Revive(MarrowEntity entity)
        {
            try
            {
                if (entity.IsHidden)
                    entity.Hide(false, true);
                if (entity.IsCulled)
                {
                    entity.PreventDisableOnCull(true);
                    RefreshZones(entity);
                }
                if (entity.IsHibernating)
                    entity.ClearHibernation();
                if (!entity.gameObject.activeSelf)
                    entity.gameObject.SetActive(true);
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"Couldn't wake {entity.name}: {e.Message}");
            }
        }

        // How the hand holds what it took out, against how it held it when it went in.
        private static void LogTakeOutHold(Outing outing, Stored item)
        {
            Frame now = Frame.Of(outing.TakenBy.transform).Inverse().Then(Frame.Of(item.Host.transform));
            float offset = Vector3.Distance(now.Position, item.HostInHand.Position);
            float angle = Quaternion.Angle(now.Rotation, item.HostInHand.Rotation);
            HandReciever receiver = outing.TakenBy.AttachedReceiver;
            string grip = receiver != null ? receiver.name : "nothing";
            string sameGrip = item.Grip != null && receiver != null && receiver.Pointer == item.Grip.Pointer
                ? "by the same grip"
                : $"by {grip}, not {(item.Grip != null ? item.Grip.name : "?")} as when it went in";
            Mod.Trace($"Take-out check for {item.Target.Name}: held {offset * 100f:0} cm and {angle:0} deg from how it " +
                        $"was put away, {sameGrip}");
        }

        // The state of a stored object's entities and bodies, for the log.
        private static string Describe(Stored item)
        {
            var parts = new List<string>();
            MarrowEntity entity = item.Entities.Count > 0 ? item.Entities[0] : null;
            if (entity != null && !entity.IsDestroyed)
            {
                var flags = new List<string>();
                if (!entity.gameObject.activeInHierarchy)
                    flags.Add("switched off");
                if (entity.IsHidden)
                    flags.Add("hidden");
                if (entity.IsCulled)
                    flags.Add("culled");
                if (entity.IsHibernating)
                    flags.Add("hibernating");
                if (entity.IsGhosting)
                    flags.Add("ghosting");
                if (!entity.IsCollidersEnabled)
                    flags.Add("colliders off");
                parts.Add(flags.Count > 0 ? $"entity {string.Join(", ", flags)}" : "entity active");
            }
            int kinematic = 0;
            int frozen = 0;
            int sleeping = 0;
            int total = 0;
            foreach ((Rigidbody rb, _, _) in item.Bodies)
            {
                if (rb == null)
                    continue;
                total++;
                if (rb.isKinematic)
                    kinematic++;
                if (rb.constraints != RigidbodyConstraints.None)
                    frozen++;
                if (rb.IsSleeping())
                    sleeping++;
            }
            parts.Add($"{total} bodies: {kinematic} kinematic, {frozen} constrained, {sleeping} asleep");
            return string.Join("; ", parts);
        }

        // Whether the game may switch an object off when its area is culled goes back to how it was, unless the game
        // counts it as culled right then: switching that back would switch off an object lying in front of the
        // player. Its zone tracking is woken up again instead.
        private static void RestoreCulling()
        {
            float now = Time.unscaledTime;
            for (int i = CullRestores.Count - 1; i >= 0; i--)
            {
                (MarrowEntity entity, bool prevented, float due) = CullRestores[i];
                if (now < due)
                    continue;
                CullRestores.RemoveAt(i);
                if (entity == null || entity.IsDestroyed || IsInPocket(entity))
                    continue;
                if (entity.IsCulled)
                {
                    Mod.Trace($"{entity.name} counts as culled {CullRestoreDelay:0} s after coming out of a pocket, so " +
                                "it's kept from being switched off and its zone tracking is woken up");
                    RefreshZones(entity);
                    continue;
                }
                entity.PreventDisableOnCull(prevented);
            }
        }

        // Switches an entity's zone trackers off and on again, so the zones it's in notice it afresh. While it was
        // hidden its trackers were off, and the game could count it as having left every zone.
        private static void RefreshZones(MarrowEntity entity)
        {
            try
            {
                entity.ForceEnableTrackers(false);
                entity.ForceEnableTrackers(true);
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"Couldn't wake the zone tracking of {entity.name}: {e.Message}");
            }
        }

        // While something's in a pocket it's kept out of the level, in the scene that survives level loads, along
        // with anything plugged into it that isn't already part of it. So a level change doesn't take it, whatever
        // it is: something found in a level, a gun with its magazine and ammo. Until v0.28 things were spawned again
        // from their crates in the next level, which lost what was plugged in, couldn't bring things that were part
        // of a level, and in the v0.27 test never worked at all (William: "make objects consistently stay").
        private static void MoveOutOfLevel(Stored item)
        {
            item.Parents.Clear();
            item.Scenes.Clear();
            item.MovedOut.Clear();
            if (!Settings.PocketKeep.Value)
                return;
            foreach (MarrowEntity entity in item.Entities)
            {
                Transform parent = null;
                UnityEngine.SceneManagement.Scene scene = default;
                bool moved = false;
                try
                {
                    if (entity != null && !IsUnderAnother(entity, item.Entities))
                    {
                        parent = entity.transform.parent;
                        scene = entity.gameObject.scene;
                        entity.transform.SetParent(null, true);
                        Object.DontDestroyOnLoad(entity.gameObject);
                        moved = true;
                    }
                }
                catch (Exception e)
                {
                    Mod.Log.Warning($"Couldn't keep {entity.name} out of the level in its pocket: {e.Message}");
                }
                item.Parents.Add(parent);
                item.Scenes.Add(scene);
                item.MovedOut.Add(moved);
            }
        }

        // Back into the level: under its old parent if that's still there, like the pool it was spawned from, or
        // otherwise into the level the player is in now.
        private static void BackIntoLevel(Stored item)
        {
            for (int i = 0; i < item.Entities.Count && i < item.MovedOut.Count; i++)
            {
                MarrowEntity entity = item.Entities[i];
                if (!item.MovedOut[i] || entity == null || entity.IsDestroyed)
                    continue;
                try
                {
                    Transform parent = item.Parents[i];
                    UnityEngine.SceneManagement.Scene scene = item.Scenes[i];
                    if (parent != null)
                        entity.transform.SetParent(parent, true);
                    else
                        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(entity.gameObject,
                            scene.IsValid() && scene.isLoaded && scene.name != "DontDestroyOnLoad" ? scene : LevelScene());
                    item.MovedOut[i] = false;
                }
                catch (Exception e)
                {
                    Mod.Log.Warning($"Couldn't put {entity.name} back into the level: {e.Message}");
                }
            }
        }

        // The scene the player is in, which is the level's, or the active scene if the rig isn't in a level scene.
        private static UnityEngine.SceneManagement.Scene LevelScene()
        {
            RigManager rig = Player.RigManager;
            UnityEngine.SceneManagement.Scene scene = rig != null ? rig.gameObject.scene : default;
            if (!scene.IsValid() || !scene.isLoaded || scene.name == "DontDestroyOnLoad")
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!_loggedLevelScene)
            {
                _loggedLevelScene = true;
                Mod.Trace($"Things out of a pocket after a level change go into the scene {scene.name}");
            }
            return scene;
        }

        private static bool IsUnderAnother(MarrowEntity entity, List<MarrowEntity> entities)
        {
            foreach (MarrowEntity other in entities)
            {
                if (other != null && other.Pointer != entity.Pointer && entity.transform.IsChildOf(other.transform))
                    return true;
            }
            return false;
        }

        private static bool IsInPocket(MarrowEntity entity)
        {
            foreach (Pocket pocket in Pockets)
            {
                if (pocket.Item == null)
                    continue;
                foreach (MarrowEntity stored in pocket.Item.Entities)
                {
                    if (stored != null && stored.Pointer == entity.Pointer)
                        return true;
                }
            }
            return false;
        }

        // Places the object's entities by their transforms, for when the game's teleport didn't put them there.
        private static void PlaceEntities(Stored item, Frame hostPose)
        {
            for (int i = 0; i < item.Entities.Count; i++)
            {
                MarrowEntity entity = item.Entities[i];
                if (entity == null || entity.IsDestroyed)
                    continue;
                Frame pose = hostPose.Then(item.EntitiesInHost[i]);
                entity.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
            }
        }

        // Where the pocket is this frame. It's set off from the wrist along the hand (or the forearm, like a
        // watch), out of the back of the wrist and across it toward the thumb. The back of the hand is the hand's
        // own outward side (right on the right hand, left on the left), so the pocket stays on the hand when a grip
        // turns it away from the controller. For the watch it's the forearm's own outward side, the same way round.
        //
        // v0.22 to v0.25 learned the back of the hand in the forearm's frame over the first 3 seconds of a level
        // instead. That picked up however the wrist happened to be twisted then: in the v0.25 test one level's left
        // arm got (-1.00, 0.03, 0.09) and its right arm (0.96, -0.28, 0.07), 17 degrees apart, and William found
        // the two pockets didn't match. Other levels had learned (-1.00, -0.02, 0.08) and (1.00, -0.02, 0.02),
        // so the forearm's own axis is the back of the hand when the wrist is straight.
        private static bool TryPlace(Pocket pocket, int wrist, Hand hand, BaseController controller)
        {
            Transform handTransform = hand.transform;
            Vector3 palm = GloveHand.PalmPosition(hand);
            PhysicsRig rig = Player.PhysicsRig;
            Transform elbow = rig != null ? (wrist == 1 ? rig.m_elbowRt : rig.m_elbowLf) : null;

            Vector3 handOut = wrist == 1 ? handTransform.right : -handTransform.right;
            Vector3 handForward = handTransform.forward;

            bool fromJoint = TryWristJoint(pocket, hand, elbow, palm, out Vector3 wristPoint);
            if (!fromJoint)
                wristPoint = palm - handForward * FallbackWristToPalm;

            bool watch = Settings.PocketFollows.Value == PocketMount.Forearm && elbow != null;
            Vector3 along = watch ? wristPoint - elbow.position : handForward;
            Vector3 outward = watch ? (wrist == 1 ? elbow.right : -elbow.right) : handOut;
            if (along.sqrMagnitude < 1e-6f)
                along = handForward;
            along.Normalize();
            outward = Vector3.ProjectOnPlane(outward, along);
            if (outward.sqrMagnitude < 1e-6f)
                return false;
            outward.Normalize();
            Vector3 side = Vector3.Cross(outward, along);
            if (wrist == 1)
                side = -side;

            // Turned around the wrist toward the thumb, which moves the pocket around it too.
            float roll = Settings.PocketRoll.Value * Mathf.Deg2Rad;
            Vector3 rolledOut = outward * Mathf.Cos(roll) + side * Mathf.Sin(roll);
            side = side * Mathf.Cos(roll) - outward * Mathf.Sin(roll);
            outward = rolledOut;

            float scale = BodyScale.Avatar;
            pocket.Center = wristPoint - along * (Settings.PocketAlong.Value / 100f * scale) +
                            outward * (Settings.PocketHeight.Value / 100f * scale) +
                            side * (Settings.PocketSide.Value / 100f * scale);

            // Tilted toward the fingers, which only turns its face.
            float tilt = Settings.PocketTilt.Value * Mathf.Deg2Rad;
            pocket.Outward = outward * Mathf.Cos(tilt) + along * Mathf.Sin(tilt);
            pocket.Along = along * Mathf.Cos(tilt) - outward * Mathf.Sin(tilt);
            pocket.Side = side;

            pocket.FromJoint = fromJoint;
            pocket.HasElbow = elbow != null;
            pocket.WristToPalm = Vector3.Distance(wristPoint, palm);
            pocket.ForearmLength = elbow != null ? Vector3.Distance(wristPoint, elbow.position) : 0f;
            return true;
        }

        // The wrist, as the anchor of the joint between the hand and the forearm. v0.21 looked for it in the hand's
        // _forearmJoint, which was empty, so the wrist was always estimated from the palm.
        private static bool TryWristJoint(Pocket pocket, Hand hand, Transform elbow, Vector3 palm, out Vector3 wristPoint)
        {
            wristPoint = palm;
            Joint joint = pocket.WristJoint;
            if (joint == null && Time.unscaledTime >= pocket.NextJointSearch)
            {
                pocket.NextJointSearch = Time.unscaledTime + JointSearchInterval;
                joint = FindWristJoint(hand, elbow);
                pocket.WristJoint = joint;
            }
            if (joint == null)
                return false;
            wristPoint = joint.transform.TransformPoint(joint.anchor);
            float toPalm = Vector3.Distance(wristPoint, palm);
            return toPalm >= MinWristToPalm && toPalm <= MaxWristToPalm;
        }

        // The joint holding the hand to its forearm, which can be on either of them.
        private static Joint FindWristJoint(Hand hand, Transform elbow)
        {
            Rigidbody handBody = hand.rb;
            Rigidbody forearm = elbow != null ? elbow.GetComponentInParent<Rigidbody>() : null;
            if (handBody == null || forearm == null)
                return null;
            foreach (Joint joint in handBody.GetComponents<Joint>())
            {
                if (joint != null && joint.connectedBody != null && joint.connectedBody.Pointer == forearm.Pointer)
                    return joint;
            }
            foreach (Joint joint in forearm.GetComponents<Joint>())
            {
                if (joint != null && joint.connectedBody != null && joint.connectedBody.Pointer == handBody.Pointer)
                    return joint;
            }
            return null;
        }

        private static void Draw(Pocket pocket, int wrist)
        {
            Stored item = pocket.Item;
            if (!pocket.HasFrame)
            {
                if (pocket.Core != null)
                    pocket.Core.enabled = false;
                if (pocket.Glow != null)
                    pocket.Glow.enabled = false;
                if (item != null && item.Ghost != null)
                    item.Ghost.SetActive(false);
                return;
            }
            if (!pocket.LoggedWrist && (pocket.Open || item != null || Hands[1 - wrist].Reaching))
                LogWrist(pocket, wrist);

            float scale = BodyScale.Avatar;
            bool reaching = Hands[1 - wrist].Reaching;
            float dt = Time.unscaledDeltaTime;
            float follow = 1f - Mathf.Exp(-dt * RingResponse);
            float radius = (pocket.Open ? OpenRadius : item != null ? FullRadius : EmptyRadius) * scale *
                           Settings.RingSize.Value / 100f;
            float brightness = pocket.Open || reaching ? 1f : item != null ? 0.6f : 0.3f;
            pocket.Radius += (radius - pocket.Radius) * follow;
            pocket.Brightness += (brightness - pocket.Brightness) * follow;
            pocket.Flash = Mathf.Max(0f, pocket.Flash - dt * FlashDecay);
            float strength = Mathf.Clamp01(pocket.Brightness + pocket.Flash * 0.5f);

            UpdateFace(pocket, wrist);
            EnsureRings(pocket, wrist);
            if (pocket.Core != null && pocket.Glow != null)
            {
                _ringPoints ??= new Il2CppStructArray<Vector3>(RingPoints);
                for (int i = 0; i < RingPoints; i++)
                {
                    float angle = i * Mathf.PI * 2f / RingPoints;
                    _ringPoints[i] = pocket.Center +
                                     (pocket.FaceAlong * Mathf.Cos(angle) + pocket.FaceSide * Mathf.Sin(angle)) * pocket.Radius;
                }
                // The look settings: thickness widens both, opacity is the bright line's, and glow the soft one's,
                // which also spreads wider above 100%, since its strength tops out there.
                float thickness = Settings.RingThickness.Value / 100f * scale;
                float opacity = Settings.RingOpacity.Value / 100f;
                float glow = Settings.RingGlow.Value / 100f;
                float spread = glow > 1f ? 1f + (glow - 1f) * 0.6f : 1f;
                SetRing(pocket.Core, CoreWidth * thickness,
                    Palette.Pocket(new Color(1f, 0.78f, 0.5f, Mathf.Clamp01(0.9f * strength * opacity))), opacity > 0f);
                SetRing(pocket.Glow, GlowWidth * thickness * spread,
                    Palette.Pocket(new Color(1f, 0.5f, 0.1f, Mathf.Clamp01(0.45f * strength * glow))), glow > 0f);
            }

            if (item != null && item.Ghost != null)
                PlaceGhost(pocket, wrist, item);
        }

        private static void LogWrist(Pocket pocket, int wrist)
        {
            pocket.LoggedWrist = true;
            string elbow = pocket.HasElbow ? $", forearm {pocket.ForearmLength * 100f:0} cm" : ", no elbow found, so Forearm follows the hand";
            Mod.Trace($"{Side(wrist)} wrist pocket: wrist {(pocket.FromJoint ? "at the joint between the hand and the forearm" : "estimated from the palm")}, " +
                        $"{pocket.WristToPalm * 100f:0.0} cm from the palm{elbow}");
        }

        private static void SetRing(LineRenderer ring, float width, Color color, bool shown)
        {
            ring.enabled = shown;
            if (!shown)
                return;
            ring.SetPositions(_ringPoints);
            ring.widthMultiplier = width;
            ring.startColor = color;
            ring.endColor = color;
        }

        private static void EnsureRings(Pocket pocket, int wrist)
        {
            if (pocket.Core != null && pocket.Glow != null)
                return;
            pocket.Glow ??= CreateRing($"{PocketName} {Side(wrist)} Glow");
            pocket.Core ??= CreateRing($"{PocketName} {Side(wrist)}");
        }

        // The rings add light like the see-through copies do: the plain line looked dim next to them when the pocket
        // opened (William, v0.26). With "Pocket faces you" they're drawn over everything, so a ring turned toward
        // the eyes never disappears into the arm.
        private static LineRenderer CreateRing(string name)
        {
            if (_ringMaterial == null && !_ringMaterialTried)
            {
                _ringMaterialTried = true;
                _ringMaterial = GlowMaterials.CreateAdditive();
            }
            LineRenderer ring = LineFactory.Create(name, RingPoints, _ringMaterial);
            if (ring == null)
                return null;
            ring.loop = true;
            ring.numCapVertices = 0;
            ring.widthMultiplier = CoreWidth;
            return ring;
        }

        // The way the ring faces this frame. With "Pocket faces you" it turns toward the eyes, or toward the other
        // hand while that reaches in or brings something to it, as in Half-Life: Alyx. Off, it's flat on the wrist.
        private static void UpdateFace(Pocket pocket, int wrist)
        {
            bool facesYou = Settings.PocketFacesYou.Value;
            if (_ringMaterial != null && facesYou != _ringOnTop)
            {
                _ringOnTop = facesYou;
                GlowMaterials.DrawOnTop(_ringMaterial, facesYou);
            }

            Vector3 want = pocket.Outward;
            if (facesYou)
            {
                Hand other = HandAt(1 - wrist);
                Transform head = Player.Head;
                Vector3? toward = (Hands[1 - wrist].Reaching || pocket.Open) && other != null
                    ? GloveHand.PalmPosition(other)
                    : head != null ? head.position : (Vector3?)null;
                Vector3 direction = toward.HasValue ? toward.Value - pocket.Center : Vector3.zero;
                if (direction.sqrMagnitude > 1e-6f)
                    want = direction.normalized;
            }
            // Off, it follows the wrist exactly. On, it turns smoothly between the eyes and a hand.
            pocket.Face = !facesYou || pocket.Face.sqrMagnitude < 0.5f
                ? want
                : Vector3.Slerp(pocket.Face, want, 1f - Mathf.Exp(-Time.unscaledDeltaTime * FaceResponse)).normalized;

            Vector3 along = Vector3.ProjectOnPlane(pocket.Along, pocket.Face);
            if (along.sqrMagnitude < 1e-6f)
                along = Vector3.ProjectOnPlane(pocket.Side, pocket.Face);
            pocket.FaceAlong = along.normalized;
            pocket.FaceSide = Vector3.Cross(pocket.Face, pocket.FaceAlong);
        }

        // The see-through copy goes where the object is: in a reaching hand, beside the pocket when it's about to
        // be swapped out, or else in the pocket as a miniature lying along the forearm, turning slowly. When that
        // changes it moves over from where it was shown, and once there it stays exactly in place, attached to the
        // hand or the pocket.
        private static void PlaceGhost(Pocket pocket, int wrist, Stored item)
        {
            Hand hand = HandAt(1 - wrist);
            Placement placement = Hands[1 - wrist].Reaching && hand != null ? Placement.Hand
                : pocket.Swapping ? Placement.Beside
                : Placement.Pocket;
            Vector3 position;
            Quaternion rotation;
            float scale;
            float alpha;
            if (placement == Placement.Hand)
            {
                Frame held = Frame.Of(hand.transform).Then(item.HostInHand);
                position = held.Position;
                rotation = held.Rotation;
                scale = 1f;
                alpha = HandAlpha;
            }
            else if (placement == Placement.Beside)
            {
                // Shown easing toward where it's clear of the body, so it glides instead of jumping in 2 cm steps
                // as the arm moves (William, v0.26).
                SwapPose(pocket, item, out float clearing);
                if (item.Placement != Placement.Beside)
                    pocket.ShownClearing = clearing;
                else
                    pocket.ShownClearing += (clearing - pocket.ShownClearing) *
                                            (1f - Mathf.Exp(-Time.unscaledDeltaTime / ClearingSmoothing));
                Frame beside = SwapPoseAt(pocket, item, pocket.ShownClearing, out _);
                position = beside.Position;
                rotation = beside.Rotation;
                scale = 1f;
                alpha = SwapAlpha;
            }
            else
            {
                // A miniature lying in the ring's plane, facing the same way as the ring.
                float size = Mathf.Max(item.Model.Size, 0.01f);
                scale = Settings.PocketMiniature.Value / 100f * BodyScale.Avatar / size;
                rotation = Quaternion.LookRotation(pocket.Face, pocket.FaceAlong) *
                           Quaternion.AngleAxis(Time.unscaledTime * MiniatureSpin, Vector3.forward) * item.LyingFlat;
                position = pocket.Center + pocket.Face * (MiniatureLift * BodyScale.Avatar) -
                           rotation * (item.Model.Bounds.center * scale);
                alpha = PocketAlpha;
            }

            // In a pocket that faces the player it's drawn over the arm too, like the ring.
            bool onTop = placement == Placement.Pocket && Settings.PocketFacesYou.Value;
            if (onTop != item.OnTop)
            {
                item.OnTop = onTop;
                GlowMaterials.DrawOnTop(item.GhostMaterial, onTop);
            }

            float now = Time.unscaledTime;
            if (placement != item.Placement)
            {
                item.Placement = placement;
                item.MoveStart = now;
                item.MoveDuration = MoveTime;
                item.FromPosition = item.GhostPosition;
                item.FromRotation = item.GhostRotation;
                item.FromScale = item.GhostScale;
                item.FromAlpha = item.GhostAlpha;
            }
            float t = Mathf.Clamp01((now - item.MoveStart) / Mathf.Max(item.MoveDuration, 0.01f));
            if (t < 1f)
            {
                float eased = t * t * (3f - 2f * t);
                position = Vector3.Lerp(item.FromPosition, position, eased);
                rotation = Quaternion.Slerp(item.FromRotation, rotation, eased);
                scale = Mathf.Lerp(item.FromScale, scale, eased);
                alpha = Mathf.Lerp(item.FromAlpha, alpha, eased);
            }
            item.GhostPosition = position;
            item.GhostRotation = rotation;
            item.GhostScale = scale;
            item.GhostAlpha = alpha;

            Transform transform = item.Ghost.transform;
            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = Vector3.one * scale;
            item.Ghost.SetActive(true);
            float shown = Mathf.Clamp01(alpha * Settings.PocketPreview.Value / 100f);
            GlowMaterials.SetTint(item.GhostMaterial, Palette.Pocket(new Color(1f, 0.5f, 0.1f, shown)));
        }

        // Beside the pocket at full size, lying along the forearm, always on the little finger's side of the wrist
        // and a little out from it. The copy waits there while a swap is coming, and the old object drops from
        // there. v0.22 picked the side away from the head, which flipped as the arm crossed the body (William).
        //
        // From there it's moved further out, away from the wrist, only as far as it takes to be clear of the
        // player's body, checked with its bounding box against the player's colliders. Clearing says how far that
        // was. v0.24 moved bigger objects out by a share of their size instead, which put long ones like swords far
        // away even when nothing was in the way (William).
        private static Frame SwapPose(Pocket pocket, Stored item, out float clearing)
        {
            SwapGeometry(pocket, item, out Bounds bounds, out Quaternion rotation, out Vector3 start, out Vector3 away,
                out Vector3 halfSize);
            float scale = BodyScale.Avatar;
            Vector3 center = start;
            clearing = 0f;
            for (int step = 0; step <= MaxSwapSteps; step++)
            {
                clearing = step * SwapStep * scale;
                center = start + away * clearing;
                if (!PlayerBody.Overlaps(center, halfSize, rotation))
                    break;
            }
            return new Frame(center - rotation * bounds.center, rotation);
        }

        // The same, at a given distance further out, and whether it's clear of the body there.
        private static Frame SwapPoseAt(Pocket pocket, Stored item, float clearing, out bool clear)
        {
            SwapGeometry(pocket, item, out Bounds bounds, out Quaternion rotation, out Vector3 start, out Vector3 away,
                out Vector3 halfSize);
            Vector3 center = start + away * clearing;
            clear = !PlayerBody.Overlaps(center, halfSize, rotation);
            return new Frame(center - rotation * bounds.center, rotation);
        }

        // Where a swapped-out object starts beside the pocket, which way it's moved out to clear the body, and the
        // box checked against the body.
        private static void SwapGeometry(Pocket pocket, Stored item, out Bounds bounds, out Quaternion rotation,
            out Vector3 start, out Vector3 away, out Vector3 halfSize)
        {
            bounds = item.Model != null ? item.Model.Bounds : new Bounds(Vector3.zero, Vector3.one * 0.1f);
            rotation = Quaternion.LookRotation(pocket.Outward, pocket.Along) * item.LyingFlat;
            Vector3 size = bounds.size;
            float shortest = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
            float middle = Mathf.Max(Mathf.Min(size.x, size.y), Mathf.Min(Mathf.Max(size.x, size.y), size.z));
            float scale = BodyScale.Avatar;
            start = pocket.Center - pocket.Side * (middle * 0.5f + SwapGap * scale) + pocket.Outward * (shortest * 0.5f);
            away = (pocket.Outward * 0.5f - pocket.Side).normalized;
            halfSize = bounds.extents + Vector3.one * (SwapClearance * scale);
        }

        // Turns the model so its shortest side faces out of the wrist and its longest runs along the forearm.
        private static Quaternion LyingFlat(Bounds bounds)
        {
            Vector3 size = bounds.size;
            Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
            float[] sizes = { size.x, size.y, size.z };
            int longest = 0;
            int shortest = 0;
            for (int i = 1; i < 3; i++)
            {
                if (sizes[i] > sizes[longest])
                    longest = i;
                if (sizes[i] < sizes[shortest])
                    shortest = i;
            }
            if (longest == shortest)
                return Quaternion.identity;
            return Quaternion.Inverse(Quaternion.LookRotation(axes[shortest], axes[longest]));
        }

        private static void DestroyVisuals(Stored item)
        {
            if (item.Ghost != null)
                Object.Destroy(item.Ghost);
            if (item.GhostMaterial != null)
                Object.Destroy(item.GhostMaterial);
            item.Model?.Destroy();
            item.Ghost = null;
            item.GhostMaterial = null;
            item.Model = null;
        }

        private static Hand HandAt(int index) => index == 1 ? Player.RightHand : Player.LeftHand;
        private static string SideOf(int index) => index == 1 ? "right" : "left";
        private static string Side(int index) => index == 1 ? "Right" : "Left";
    }
}
