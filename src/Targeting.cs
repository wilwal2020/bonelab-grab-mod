using System;
using System.Collections.Generic;
using BoneLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSLZ.Marrow;
using UnityEngine;

namespace GravityGloves
{
    // Finds the best object inside one hand's aim cone and keeps the selection steady.
    internal sealed class Targeting
    {
        private const float ScanInterval = 1f / 30f;

        // Between two objects that are about equally well aimed at, prefer the closer one (degrees per metre).
        private const float DistanceWeight = 0.3f;

        // And the one aimed at more centrally. Angles go to the nearest edge of each object, so pointing at
        // either end of a bat counts, but that makes neighbouring weapons nearly tie.
        private const float CenterWeight = 0.2f;

        // Grabbable things that aren't items get this share of the item priority.
        private const float GrabbablePriority = 0.25f;

        // Line-of-sight raycasts are the expensive part, so only the best few candidates are checked.
        private const int MaxSightChecks = 3;

        // Props live on the Dynamic layer. Interactable covers grabbable things that aren't on it.
        private const int GatherMask = (int)(GameLayers.DYNAMIC | GameLayers.INTERACTABLE);

        // Things that block the view of a target. Player layers are left out so your own body doesn't count.
        private const int SightMask = (int)(GameLayers.DEFAULT | GameLayers.FIXTURE | GameLayers.DYNAMIC |
                                            GameLayers.ENEMY_COLLIDERS | GameLayers.INTERACTABLE);

        private struct Candidate
        {
            public Target Target;
            public Vector3 Point;
            public float Score;
        }

        private readonly Il2CppReferenceArray<Collider> _overlaps = new Il2CppReferenceArray<Collider>(256);
        private readonly Il2CppStructArray<RaycastHit> _hits = new Il2CppStructArray<RaycastHit>(16);
        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly Dictionary<Target, int> _candidateOf = new Dictionary<Target, int>();

        private Target _current;
        private float _outdoneSince = -1f;
        private float _nextScan;

        // The second best object at the last scan and how much worse it scored, for the lock log.
        public Target RunnerUp { get; private set; }
        public float RunnerUpGap { get; private set; }

        public void Reset()
        {
            _current = null;
            _outdoneSince = -1f;
            _nextScan = 0f;
            RunnerUp = null;
        }

        // Called every frame while nothing is locked. Returns the current selection, which may be null. The head
        // is where the player looks from: only objects within the view angle of where it faces can be selected.
        public Target Select(Vector3 origin, Vector3 aim, Transform head, float now)
        {
            if (now < _nextScan)
                return _current;
            _nextScan = now + ScanInterval;
            TargetCache.CleanupIfDue(now);

            HeldObjects held = HeldObjects.Read();
            Target best = FindBest(origin, aim, head, held);

            if (_current != null && !IsUsable(_current, origin, Settings.MaxRange.Value * BodyScale.Value, held))
                _current = null;

            if (best == _current)
            {
                _outdoneSince = -1f;
            }
            else if (_current == null || (best != null && best.Rank > _current.Rank))
            {
                // A more important kind of object takes over at once, so reaching past debris to the
                // ammo behind it doesn't stay stuck on the debris.
                _current = best;
                _outdoneSince = -1f;
            }
            else
            {
                // Keep the current target until it hasn't been the best pick for a moment. That counts from when
                // it stopped being best, whatever beat it, so sweeping across a row of weapons doesn't keep
                // restarting the wait and stay stuck on the first one.
                if (_outdoneSince < 0f)
                    _outdoneSince = now;
                if (now - _outdoneSince >= Settings.TargetStickiness.Value)
                {
                    _current = best;
                    _outdoneSince = -1f;
                }
            }
            return _current;
        }

        // A locked target stays locked wherever the hand points, as long as it's still there and free.
        public bool CanStayLocked(Target target, Vector3 origin) =>
            IsUsable(target, origin, Settings.MaxRange.Value * BodyScale.Value * 1.25f, HeldObjects.Read());

        private Target FindBest(Vector3 origin, Vector3 aim, Transform head, HeldObjects held)
        {
            // Distances count in avatar-sized metres.
            float scale = BodyScale.Value;
            float maxRange = Settings.MaxRange.Value * scale;
            float minRange = Settings.MinRange * scale;
            float coneAngle = Settings.ConeAngle.Value;
            float minMass = Settings.MinMass.Value;
            float maxMass = Settings.MaxMass.Value;

            // Things behind the player, or off to the side out of view, aren't picked (William, v0.25). The part
            // aimed at has to be within half the view angle of where the head faces. 360 is all around.
            float viewHalfAngle = Settings.ViewAngle.Value / 2f;
            bool checkView = viewHalfAngle < 180f && head != null;
            Vector3 eye = checkView ? head.position : Vector3.zero;
            Vector3 facing = checkView ? head.forward : Vector3.forward;

            // Items win over other things near the aim by up to this many degrees.
            float itemPriority = coneAngle * Settings.ItemPriority.Value / 100f;

            // A capsule along the aim that's as wide as the cone at full range. The angle test trims it to the cone.
            float radius = maxRange * Mathf.Tan(coneAngle * Mathf.Deg2Rad);
            Vector3 start = origin + aim * minRange;
            Vector3 end = origin + aim * maxRange;
            int count = Physics.OverlapCapsuleNonAlloc(start, end, radius, _overlaps, GatherMask,
                QueryTriggerInteraction.Ignore);

            _candidates.Clear();
            _candidateOf.Clear();
            for (int i = 0; i < count; i++)
            {
                Collider collider = _overlaps[i];
                if (collider == null)
                    continue;
                Rigidbody rb = collider.attachedRigidbody;
                if (rb == null || rb.isKinematic)
                    continue;

                Target target = TargetCache.Get(rb);
                if (target.Mass > maxMass || !Flights.CanPull(target) || GrabAssist.IsGrabbing(target))
                    continue;
                // The minimum keeps out shell casings and debris, but anything with a grip, like ammo, still counts.
                if (target.Mass < minMass && !target.IsGrabbable)
                    continue;

                // Aim at the part of the object nearest the aim line rather than its middle, so pointing at
                // either end of something long, like a bat, counts.
                Vector3 alongAim = origin + aim * Mathf.Max(Vector3.Dot(collider.bounds.center - origin, aim), 0f);
                Vector3 point = collider.ClosestPoint(alongAim);
                Vector3 toPoint = point - origin;
                float distance = toPoint.magnitude;
                if (distance < minRange || distance > maxRange)
                    continue;
                float angle = Vector3.Angle(aim, toPoint);
                if (angle > coneAngle)
                    continue;
                if (checkView && Vector3.Angle(facing, point - eye) > viewHalfAngle)
                    continue;

                float priority = target.IsItem ? itemPriority : target.IsGrabbable ? itemPriority * GrabbablePriority : 0f;
                var candidate = new Candidate
                {
                    Target = target,
                    Point = point,
                    Score = angle + DistanceWeight * distance / scale - priority,
                };

                // An object with several colliders counts once, with its best one.
                if (_candidateOf.TryGetValue(target, out int existing))
                {
                    if (candidate.Score < _candidates[existing].Score)
                        _candidates[existing] = candidate;
                }
                else
                {
                    _candidateOf[target] = _candidates.Count;
                    _candidates.Add(candidate);
                }
            }

            // How centrally each object is aimed at, once per object.
            for (int i = 0; i < _candidates.Count; i++)
            {
                Candidate candidate = _candidates[i];
                candidate.Score += CenterWeight * Vector3.Angle(aim, candidate.Target.CenterOfMass - origin);
                _candidates[i] = candidate;
            }

            _candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
            RunnerUp = null;
            int sightChecks = 0;
            for (int i = 0; i < _candidates.Count; i++)
            {
                Candidate candidate = _candidates[i];
                if (held.Contains(candidate.Target) || candidate.Target.IsOnPlayer)
                    continue;
                if (sightChecks++ >= MaxSightChecks)
                    break;
                if (!HasLineOfSight(origin, candidate.Point, candidate.Target))
                    continue;
                if (i + 1 < _candidates.Count)
                {
                    RunnerUp = _candidates[i + 1].Target;
                    RunnerUpGap = _candidates[i + 1].Score - candidate.Score;
                }
                return candidate.Target;
            }
            return null;
        }

        private static bool IsUsable(Target target, Vector3 origin, float maxDistance, HeldObjects held)
        {
            if (!target.IsAlive || target.IsKinematic || held.Contains(target) || !Flights.CanPull(target) ||
                GrabAssist.IsGrabbing(target))
            {
                return false;
            }
            return (target.MainBody.worldCenterOfMass - origin).sqrMagnitude <= maxDistance * maxDistance;
        }

        // Items in the player's holsters and inventory slots are parented under the player rig.

        private bool HasLineOfSight(Vector3 origin, Vector3 point, Target target)
        {
            Vector3 toPoint = point - origin;
            float distance = toPoint.magnitude;
            if (distance < 0.01f)
                return true;

            int count = Physics.RaycastNonAlloc(origin, toPoint / distance, _hits, distance, SightMask,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider collider = _hits[i].collider;
                if (collider != null && !target.Contains(collider.attachedRigidbody))
                    return false;
            }
            return true;
        }
    }
}
