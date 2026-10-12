using System;
using System.Collections.Generic;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Interaction;
using UnityEngine;

namespace GravityGloves
{
    // Something the glove can pull: a Marrow entity with all of its rigidbodies, or a single loose rigidbody.
    internal sealed class Target
    {
        // For catching, a grip that can be held anywhere counts as this much further away than it is, so a handle
        // or fixed grip about as near wins.
        private const float SurfaceGripPenalty = 0.06f;

        private readonly MarrowEntity _entity;
        private readonly List<Rigidbody> _bodies = new List<Rigidbody>();
        private readonly HashSet<IntPtr> _bodyPointers = new HashSet<IntPtr>();
        private List<Collider> _surfaces;
        private bool _surfacesStale;
        private bool? _isWeapon;
        private string _weaponKind;
        private bool? _isItem;
        private bool? _isGrabbable;
        private Il2CppSLZ.Bonelab.AmmoPickup _ammoPickup;
        private bool _searchedAmmoPickup;

        public Target(Rigidbody mainBody, MarrowEntity entity)
        {
            _entity = entity;
            MainBody = mainBody;
            Key = entity != null ? entity.Pointer : mainBody.Pointer;
            Name = entity != null ? entity.name : mainBody.name;

            var marrowBodies = entity != null ? entity.Bodies : null;
            if (marrowBodies != null)
            {
                for (int i = 0; i < marrowBodies.Length; i++)
                {
                    MarrowBody body = marrowBodies[i];
                    if (body != null && body.HasRigidbody)
                        Add(body._rigidbody);
                }
            }
            Add(mainBody);

            float mass = 0f;
            foreach (Rigidbody rb in _bodies)
                mass += rb.mass;
            Mass = mass;
        }

        public Rigidbody MainBody { get; }

        // The Marrow entity, or null for a loose rigidbody.
        public MarrowEntity Entity => _entity;

        // Part of a player's body, which BONELAB lets the other hand grab.
        public bool IsOnPlayer => MainBody.GetComponentInParent<RigManager>() != null;

        // The entity pointer, or the rigidbody pointer for objects without an entity.
        public IntPtr Key { get; }

        public string Name { get; }
        public float Mass { get; }

        public bool IsAlive => MainBody != null;

        // Pooled objects are switched off rather than destroyed, for example when ammo is collected.
        public bool IsActive => MainBody.gameObject.activeInHierarchy;
        public bool IsKinematic => MainBody.isKinematic;
        public bool UsesGravity => MainBody.useGravity;
        public float Speed => MainBody.velocity.magnitude;

        // Guns, melee weapons and anything that fits in a holster.
        public bool IsWeapon => _isWeapon ??= HasWeaponComponent();

        // Things meant to be picked up and used: weapons, magazines and ammo. Some ammo boxes only give
        // themselves away by name.
        public bool IsItem => _isItem ??= IsWeapon || HasAmmoComponent() ||
                                          Name.IndexOf("ammo", StringComparison.OrdinalIgnoreCase) >= 0;

        // Has a grip the hand can take, unlike shell casings and most debris.
        public bool IsGrabbable => _isGrabbable ??= FindGrip() != null;

        // For selection: items beat other grabbable things, which beat everything else.
        public int Rank => IsItem ? 2 : IsGrabbable ? 1 : 0;

        // The ammo box part of the object, if it's an ammo pickup.
        public Il2CppSLZ.Bonelab.AmmoPickup AmmoPickup
        {
            get
            {
                if (_searchedAmmoPickup)
                    return _ammoPickup;
                _searchedAmmoPickup = true;
                GameObject root = RootObject;
                _ammoPickup = root.GetComponentInChildren<Il2CppSLZ.Bonelab.AmmoPickup>(true);
                if (_ammoPickup == null)
                {
                    var proxy = root.GetComponentInChildren<Il2CppSLZ.Bonelab.AmmoPickupProxy>(true);
                    if (proxy != null)
                        _ammoPickup = proxy.ammoPickup;
                }
                return _ammoPickup;
            }
        }

        // The entity's root, or the rigidbody's object for things without an entity.
        public GameObject RootObject => _entity != null ? _entity.gameObject : MainBody.gameObject;

        public Vector3 CenterOfMass => WeightedAverage(rb => rb.worldCenterOfMass, MainBody.worldCenterOfMass);

        public Vector3 Velocity => WeightedAverage(rb => rb.velocity, MainBody.velocity);

        public bool Contains(Rigidbody rb) => rb != null && _bodyPointers.Contains(rb.Pointer);

        // The point on the object's surface nearest to the given point, for the beam to start from.
        public Vector3 SurfacePointToward(Vector3 from)
        {
            Vector3 nearest = CenterOfMass;
            float nearestDistance = float.MaxValue;
            foreach (Collider collider in Surfaces)
            {
                if (!IsSurface(collider))
                    continue;
                Vector3 point = collider.ClosestPoint(from);
                Bounds bounds = collider.bounds;
                bounds.Expand(0.02f);
                if (!bounds.Contains(point))
                    continue;
                float distance = (point - from).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearest = point;
                    nearestDistance = distance;
                }
            }
            return nearest;
        }

        // Where the object touches the palm, for catching it there: the nearest point of its surface. If the palm
        // is already inside the object, it's the point on the side the object was moving toward the palm, so
        // the object can be backed out of the hand until that side touches it.
        public Vector3 ContactPoint(Vector3 palm)
        {
            Vector3 nearest = SurfacePointToward(palm);
            if ((nearest - palm).sqrMagnitude > 1e-6f || !TryGetBounds(out Bounds bounds))
                return nearest;

            Vector3 heading = Velocity;
            if (heading.sqrMagnitude < 0.01f)
                heading = palm - CenterOfMass;
            if (heading.sqrMagnitude < 1e-6f)
                return nearest;
            heading.Normalize();

            // From beyond the object, back along its motion to its leading side.
            float reach = bounds.size.magnitude + 0.1f;
            var ray = new Ray(palm + heading * reach, -heading);
            float best = float.MaxValue;
            foreach (Collider collider in Surfaces)
            {
                if (!IsSurface(collider))
                    continue;
                if (collider.Raycast(ray, out RaycastHit hit, reach) && hit.distance < best)
                {
                    best = hit.distance;
                    nearest = hit.point;
                }
            }
            return nearest;
        }

        // The world-space box around the object's colliders.
        public bool TryGetBounds(out Bounds bounds)
        {
            bounds = default;
            bool found = false;
            foreach (Collider collider in Surfaces)
            {
                if (!IsSurface(collider))
                    continue;
                if (found)
                {
                    bounds.Encapsulate(collider.bounds);
                }
                else
                {
                    bounds = collider.bounds;
                    found = true;
                }
            }
            return found;
        }

        // The solid colliders on all of the object's bodies. The list is made again once one of them is switched off
        // or has ended up on another object, and while it's empty, so a pooled object's parts are up to date.
        private List<Collider> Surfaces
        {
            get
            {
                if (_surfaces != null && !_surfacesStale && _surfaces.Count > 0)
                    return _surfaces;
                _surfacesStale = false;
                _surfaces = new List<Collider>();
                foreach (Collider collider in RootObject.GetComponentsInChildren<Collider>())
                {
                    if (collider != null && !collider.isTrigger && Contains(collider.attachedRigidbody))
                        _surfaces.Add(collider);
                }
                return _surfaces;
            }
        }

        // A collider that's still part of the object and in the world, and switched on. Unity gives the closest point
        // of a switched-off collider as the point asked about, so one left in the list read as touching from anywhere:
        // in the v0.29 test magazines from the ammo pouch opened a wrist pocket from across the body, drew its sparks
        // and went in when let go (William: "attracted to the wrist pocket no matter how far away it was"). Pooled
        // magazines and their cartridges are switched off and passed between objects.
        private bool IsSurface(Collider collider)
        {
            if (collider == null || !collider.gameObject.activeInHierarchy || !Contains(collider.attachedRigidbody))
            {
                _surfacesStale = true;
                return false;
            }
            return collider.enabled;
        }

        // Every body gets the same velocity so the joints holding the entity together don't fight it.
        public void SetVelocity(Vector3 velocity, bool dampSpin)
        {
            foreach (Rigidbody rb in _bodies)
            {
                if (rb == null || rb.isKinematic)
                    continue;
                rb.velocity = velocity;
                if (dampSpin)
                    rb.angularVelocity = Vector3.zero;
                rb.WakeUp();
            }
        }

        // Moves the whole entity as one rigid piece. With a turn rate (radians per second), each body also
        // gets the extra velocity from swinging round the shared centre of mass.
        public void SetMotion(Vector3 velocity, Vector3? angularVelocity)
        {
            Vector3 center = angularVelocity.HasValue ? CenterOfMass : Vector3.zero;
            foreach (Rigidbody rb in _bodies)
            {
                if (rb == null || rb.isKinematic)
                    continue;
                if (angularVelocity.HasValue)
                {
                    Vector3 spin = angularVelocity.Value;
                    rb.angularVelocity = spin;
                    rb.velocity = velocity + Vector3.Cross(spin, rb.worldCenterOfMass - center);
                }
                else
                {
                    rb.velocity = velocity;
                }
            }
        }

        public void AddAcceleration(Vector3 acceleration)
        {
            foreach (Rigidbody rb in _bodies)
            {
                if (rb != null && !rb.isKinematic)
                    rb.AddForce(acceleration, ForceMode.Acceleration);
            }
        }

        // While flying, bodies use continuous collision, so a fast object can't pass through a thin wall or floor
        // between physics steps. Each goes back to how it was afterwards.
        public void SetFastCollision(bool fast)
        {
            if (fast)
            {
                if (_savedCollision != null)
                    return;
                _savedCollision = new List<(Rigidbody, CollisionDetectionMode)>();
                foreach (Rigidbody rb in _bodies)
                {
                    if (rb == null || rb.isKinematic || rb.collisionDetectionMode != CollisionDetectionMode.Discrete)
                        continue;
                    _savedCollision.Add((rb, rb.collisionDetectionMode));
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                }
                return;
            }
            if (_savedCollision == null)
                return;
            foreach ((Rigidbody rb, CollisionDetectionMode mode) in _savedCollision)
            {
                if (rb != null)
                    rb.collisionDetectionMode = mode;
            }
            _savedCollision = null;
        }

        private List<(Rigidbody, CollisionDetectionMode)> _savedCollision;

        // A body held still for a while can fall asleep, and a sleeping body ignores gravity until it's touched.
        public void WakeUp()
        {
            foreach (Rigidbody rb in _bodies)
            {
                if (rb != null && !rb.isKinematic)
                    rb.WakeUp();
            }
        }

        // The grip nearest a point, for catching: the hand takes whatever part of the object reaches it, the way
        // a real catch works. Distance is to the grip's own colliders, since a big grip's target can be far from
        // where it's touched. A grip that can be held anywhere, like a box grip around a whole crate, often has
        // the point inside it, so a real handle within SurfaceGripPenalty of the point wins over it. Falls back
        // to FindGrip.
        public Grip FindGripNearest(Vector3 point)
        {
            Grip nearest = null;
            float nearestDistance = float.MaxValue;
            foreach (Rigidbody rb in _bodies)
            {
                if (rb == null)
                    continue;
                InteractableHost host = rb.GetComponent<InteractableHost>();
                var grips = host != null ? host._grips : null;
                if (grips == null)
                    continue;
                for (int i = 0; i < grips.Count; i++)
                {
                    Grip grip = grips[i];
                    if (grip == null || !grip.isActiveAndEnabled || grip.IsInteractionDisabled)
                        continue;
                    float distance = Mathf.Sqrt(DistanceToGrip(grip, point));
                    if (GripKinds.Of(grip) == GripKind.Surface)
                        distance += SurfaceGripPenalty;
                    if (distance < nearestDistance)
                    {
                        nearest = grip;
                        nearestDistance = distance;
                    }
                }
            }
            return nearest ?? FindGrip();
        }

        private static float DistanceToGrip(Grip grip, Vector3 point)
        {
            float nearest = float.MaxValue;
            var colliders = grip.gripColliders;
            if (colliders != null)
            {
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider collider = colliders[i];
                    if (collider == null || !collider.enabled)
                        continue;
                    // ClosestPoint only works on convex shapes. Anything else gets its bounding box.
                    MeshCollider mesh = collider.TryCast<MeshCollider>();
                    Vector3 closest = mesh != null && !mesh.convex
                        ? collider.ClosestPointOnBounds(point)
                        : collider.ClosestPoint(point);
                    nearest = Mathf.Min(nearest, (closest - point).sqrMagnitude);
                }
            }
            if (nearest < float.MaxValue)
                return nearest;
            Transform gripTransform = grip.targetTransform != null ? grip.targetTransform : grip.transform;
            return (gripTransform.position - point).sqrMagnitude;
        }

        // The grip to fit to the hand: the one BONELAB's force pull would use, otherwise any grip.
        public Grip FindGrip()
        {
            Grip fallback = null;
            foreach (Rigidbody rb in _bodies)
            {
                if (rb == null)
                    continue;
                InteractableHost host = rb.GetComponent<InteractableHost>();
                if (host == null)
                    continue;
                ForcePullGrip forcePull = host.GetForcePullGrip();
                if (forcePull != null && forcePull._grip != null)
                    return forcePull._grip;
                if (fallback == null)
                    fallback = host.GetGrip();
            }
            return fallback;
        }

        // What makes it a weapon, for the log, or null if it isn't one.
        public string WeaponKind => IsWeapon ? _weaponKind : null;

        private bool HasWeaponComponent()
        {
            GameObject root = RootObject;
            if (root.GetComponentInChildren<Gun>(true) != null)
                _weaponKind = "gun";
            else if (root.GetComponentInChildren<StabSlash>(true) != null)
                _weaponKind = "melee";
            else if (root.GetComponentInChildren<WeaponSlot>(true) != null)
                _weaponKind = "fits a holster";
            return _weaponKind != null;
        }

        private bool HasAmmoComponent() =>
            AmmoPickup != null || RootObject.GetComponentInChildren<Magazine>(true) != null;

        private Vector3 WeightedAverage(Func<Rigidbody, Vector3> value, Vector3 fallback)
        {
            Vector3 sum = Vector3.zero;
            float mass = 0f;
            foreach (Rigidbody rb in _bodies)
            {
                if (rb == null)
                    continue;
                sum += value(rb) * rb.mass;
                mass += rb.mass;
            }
            return mass > 0f ? sum / mass : fallback;
        }

        private void Add(Rigidbody rb)
        {
            if (rb != null && _bodyPointers.Add(rb.Pointer))
                _bodies.Add(rb);
        }
    }
}
