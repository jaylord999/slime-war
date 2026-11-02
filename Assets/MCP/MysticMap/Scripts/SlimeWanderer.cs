using MysticMap.World;
using UnityEngine;

namespace MysticMap
{
    /// <summary>
    /// A wild slime that just lives in the world: it idles for a while, hops to a random spot
    /// near its home and idles again. Nothing chases, nothing is chased - it is scenery that
    /// moves.
    ///
    /// The slime model has no animation clips (mesh + blend shapes only), so the hop is the same
    /// procedurally generated squash / stretch <see cref="SlimePlayer"/> uses: it squashes when it
    /// lands and stretches while it rises, and it keeps its feet on the ground while it does.
    ///
    /// The ground comes from <see cref="ChunkManager.SampleHeight"/>, so a slime walks the
    /// streamed procedural terrain correctly wherever it is put.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MysticMap/Slime Wanderer (walks + idles)")]
    public class SlimeWanderer : MonoBehaviour
    {
        [Header("Walking")]
        [Tooltip("Hopping speed (m/s).")]
        public float moveSpeed = 1.3f;
        [Tooltip("How fast (degrees/s) the slime turns towards where it is going.")]
        public float turnSpeed = 220f;
        [Tooltip("Downward acceleration (m/s^2).")]
        public float gravity = 22f;
        [Tooltip("How close (m) to the target counts as 'arrived'.")]
        public float arriveDistance = 0.35f;
        [Tooltip("Give up on a walk after this many seconds (a slime stuck on a rock picks a new spot).")]
        public float maxWalkTime = 14f;
        [Tooltip("How far (m) from its home a slime will wander. 0 = anywhere inside the area it was put in.")]
        public float wanderRadius = 6f;

        [Header("Idling")]
        [Tooltip("Shortest rest between two walks (s).")]
        public float idleMin = 1.5f;
        [Tooltip("Longest rest between two walks (s).")]
        public float idleMax = 5f;

        [Header("Bounce (procedural - the model has no animation)")]
        [Tooltip("Upward speed of every hop.")]
        public float hopSpeed = 2.6f;
        [Tooltip("Minimum time on the ground between two hops (keeps a landing from double-firing).")]
        public float hopRest = 0.06f;
        [Tooltip("How flat the slime is when it lands (0 = rigid, 0.5 = very squishy).")]
        [Range(0f, 0.6f)] public float squash = 0.3f;
        [Tooltip("How tall the slime is at the top of a hop.")]
        [Range(0f, 0.6f)] public float stretch = 0.28f;
        [Tooltip("Vertical speed -> stretch factor.")]
        public float bounceStretch = 0.06f;
        [Tooltip("How quickly the squash / stretch follows the bounce. Higher = snappier.")]
        public float squashBlend = 18f;
        [Tooltip("Gentle breathing squash while it rests.")]
        public bool idleBreathe = true;

        [Header("Health")]
        [Tooltip("Add a Health component so the slimes can be hurt by the magic (and hop away).")]
        public bool addHealth = true;
        [Tooltip("Hit points at full health.")]
        public float maxHealth = 60f;

        [Header("Model")]
        [Tooltip("The slime mesh (a child of this object). Auto-filled from the first renderer.")]
        public Transform model;
        [Tooltip("Yaw correction if the model does not look down its own +Z axis.")]
        public float modelYawOffset = 0f;
        [Tooltip("Puts the model into a uniform 1 m box (the player slime does this). Off = each " +
                 "slime model keeps the size its own FBX was exported with.")]
        public bool normalizeModelHeight = false;
        [Tooltip("World height used when 'normalizeModelHeight' is on.")]
        public float slimeHeight = 1f;

        // What the slime is doing right now.
        bool _walking;
        Vector3 _home;
        Vector3 _target;
        float _idleTimer;
        float _walkTimer;

        float _verticalVelocity;
        float _hopTimer;

        // Model rest pose, measured once so squash / stretch stays anchored to the ground.
        // (An FBX root may carry an axis conversion, so the model is never forced upright.)
        Vector3 _restScale = Vector3.one;
        float _restLocalY;
        float _bottomY;
        float _scaleY = 1f;
        Quaternion _restRotation = Quaternion.identity;
        int _upAxis = 1;              // the model's own axis that points up: 0 = X, 1 = Y, 2 = Z
        bool _restPoseCaptured;

        // Hit reaction: a wounded slime hops away from whoever hit it for a moment.
        Health _health;
        float _fleeFor;
        Vector3 _fleeFrom;

        /// <summary>This slime's hit points (null when <see cref="addHealth"/> is off).</summary>
        public Health Health => _health;

        ChunkManager _chunks;

        /// <summary>
        /// Where this slime lives. The spawner sets it when it drops the slime in, so every slime
        /// keeps to its own little patch instead of drifting off across the whole world.
        /// </summary>
        public Vector3 Home
        {
            get => _home;
            set => _home = value;
        }

        /// <summary>What the slime is doing right now (handy for debugging / other scripts).</summary>
        public bool IsWalking => _walking;

        /// <summary>
        /// Wires the slime mesh into this wanderer (the spawner calls it right after creating the
        /// object) and strips the Animator the FBX importer may have added.
        /// </summary>
        public void SetModel(GameObject instance, Transform modelTransform)
        {
            if (instance == null) return;

            foreach (Animator animator in instance.GetComponentsInChildren<Animator>(true))
            {
                if (animator != null) Destroy(animator);
            }

            model = modelTransform != null ? modelTransform : instance.transform;
            FitToModel();
        }

        /// <summary>
        /// Measures the model and remembers its rest pose. Each slime keeps its own scale unless
        /// <see cref="normalizeModelHeight"/> is on, so replaced models that are bigger or smaller
        /// look exactly as they were exported.
        /// </summary>
        public void FitToModel()
        {
            if (model == null) model = FindModel();
            if (model == null) return;

            CaptureRestPose();

            _restScale = model.localScale;
            _restLocalY = model.localPosition.y;

            if (normalizeModelHeight && TryGetModelBounds(out Bounds bounds) && bounds.size.y > 0.0001f)
            {
                float s = Mathf.Max(0.01f, slimeHeight) / bounds.size.y;
                model.localScale = _restScale * s;
                _restScale = model.localScale;
            }

            if (TryGetModelBounds(out Bounds fitted)) _bottomY = fitted.min.y;
            else _bottomY = 0f;

            _scaleY = 1f;
        }

        /// <summary>Drops the slime straight onto the ground at a spot and makes that spot home.</summary>
        public void PlaceAt(Vector3 position)
        {
            _home = position;
            transform.position = position;
            _verticalVelocity = 0f;
            _hopTimer = 0f;
            _walking = false;
            _target = position;
            _idleTimer = Random.Range(0f, Mathf.Max(0.1f, idleMax));
        }

        void Awake()
        {
            if (model == null) model = FindModel();

            SetupHealth();

            // Keep whatever orientation the object was given, then fit the model to it.
            _home = transform.position;
            FitToModel();

            if (model != null)
                model.rotation = Quaternion.Euler(0f, transform.eulerAngles.y + modelYawOffset, 0f) * _restRotation;

            _target = transform.position;
            _idleTimer = Random.Range(0f, Mathf.Max(0.1f, idleMax));
        }

        /// <summary>
        /// Gives the slime hit points: a <see cref="Health"/> (the project's IDamageable, so every
        /// spell already hurts it) wired so a hit makes it hop away and a death removes it.
        ///
        /// The collider covers the whole slime so the spells can actually hit it. It is a trigger
        /// on a kinematic rigidbody, so it is only there to be hit - it never blocks the player or
        /// fights the ground-following movement.
        /// </summary>
        void SetupHealth()
        {
            if (!addHealth) return;

            if (TryGetComponent(out _health) && _health != null)
            {
                _health.maxHealth = Mathf.Max(1f, maxHealth);
                _health.Damaged += OnDamaged;
                _health.Died += OnDied;
                return;
            }

            // One collider sized to the slime (reused when the prefab already has one).
            if (!TryGetComponent(out Collider collider) || collider == null)
            {
                var capsule = gameObject.AddComponent<CapsuleCollider>();
                capsule.center = new Vector3(0f, 0.5f, 0f);
                capsule.height = 1f;
                capsule.radius = 0.42f;
            }

            foreach (Collider c in GetComponents<Collider>())
                if (c != null) c.isTrigger = true;

            if (TryGetComponent(out Rigidbody body))
            {
                body.isKinematic = true;
                body.useGravity = false;
            }
            else
            {
                var rb = gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            _health = gameObject.AddComponent<Health>();
            _health.maxHealth = Mathf.Max(1f, maxHealth);
            _health.destroyOnDeath = true;
            _health.destroyDelay = 0.15f;
            _health.stirEnvironment = true;
            _health.environmentRadius = 1.2f;
            _health.flashColor = new Color(1f, 0.55f, 0.55f, 1f);
            _health.flashTime = 0.12f;

            _health.Damaged += OnDamaged;
            _health.Died += OnDied;
        }

        void OnDestroy()
        {
            if (_health == null) return;
            _health.Damaged -= OnDamaged;
            _health.Died -= OnDied;
        }

        /// <summary>A hit makes the slime hop away from the attacker for a moment.</summary>
        void OnDamaged(DamageInfo info)
        {
            Vector3 away = transform.position - info.point;
            away.y = 0f;

            if (away.sqrMagnitude < 0.0001f) away = -FlatDirection(info.direction);
            if (away.sqrMagnitude < 0.0001f) away = Random.insideUnitSphere;

            _fleeFrom = info.point;
            _fleeFor = 1.2f;
            _target = transform.position + FlatDirection(away) * Mathf.Max(2f, wanderRadius);
            _walkTimer = 0f;
            _walking = true;
            _hopTimer = 0f;                                     // leave the ground at once
        }

        /// <summary>Stops the behaviour for good once it is dead (Health removes the object).</summary>
        void OnDied()
        {
            _walking = false;
            _fleeFor = 0f;
            enabled = false;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // A wounded slime runs away from where the hit came from before it settles down again.
            if (_fleeFor > 0f)
            {
                _fleeFor -= dt;
                if (_fleeFor <= 0f)
                {
                    _walking = false;
                    StopWalking();
                }
            }

            Think(dt);

            Vector3 dir = _walking ? FlatDirection(_target - transform.position) : Vector3.zero;
            bool moving = dir.sqrMagnitude > 0.0001f;

            // ---- horizontal travel ------------------------------------------------
            if (moving) transform.position += dir * (Mathf.Max(0f, moveSpeed) * dt);

            StickToGround(dt, moving);
            FaceTravelDirection(dir, dt);
            AnimateModel(moving, dt);
        }

        // =====================================================================
        //  Behaviour: idle -> walk to a random spot -> idle
        // =====================================================================
        void Think(float dt)
        {
            if (_walking)
            {
                _walkTimer += dt;

                // While fleeing it keeps going even past the normal max walk time.
                if (_fleeFor > 0f) return;

                if (FlatDistance(transform.position, _target) <= Mathf.Max(0.05f, arriveDistance) ||
                    _walkTimer >= maxWalkTime)
                    StopWalking();
                return;
            }

            if (_fleeFor > 0f) return;      // standing still only between the hop-away and the rest

            _idleTimer -= dt;
            if (_idleTimer > 0f) return;

            // Pick somewhere new near home and start hopping towards it.
            float radius = wanderRadius > 0f ? wanderRadius : 5f;
            Vector2 offset = Random.insideUnitCircle * radius;

            _target = new Vector3(_home.x + offset.x, _home.y, _home.z + offset.y);
            _walkTimer = 0f;
            _walking = true;
        }

        void StopWalking()
        {
            _walking = false;
            _target = transform.position;

            float lo = Mathf.Min(idleMin, idleMax);
            float hi = Mathf.Max(idleMin, idleMax);
            _idleTimer = Random.Range(lo, hi);
        }

        // =====================================================================
        //  Ground + bounce
        // =====================================================================
        /// <summary>
        /// Follows the terrain: the hop arc is layered on top of the sampled ground, so a slime
        /// never sinks into a hill or hangs over a dip. Every landing launches the next hop.
        /// </summary>
        void StickToGround(float dt, bool moving)
        {
            float floor = GroundHeight(transform.position.x, transform.position.z);

            if (_verticalVelocity <= 0f && transform.position.y <= floor + 0.02f)
            {
                // On the ground: rest, then hop again if it is still on its way somewhere.
                transform.position = new Vector3(transform.position.x, floor, transform.position.z);
                _verticalVelocity = 0f;
                _hopTimer -= dt;

                if (moving && _hopTimer <= 0f)
                {
                    _verticalVelocity = Mathf.Max(0.1f, hopSpeed);
                    _hopTimer = Mathf.Max(0f, hopRest);
                }
                else
                {
                    return;
                }
            }

            _verticalVelocity -= Mathf.Max(0.01f, gravity) * dt;
            transform.position += Vector3.up * (_verticalVelocity * dt);

            // Never end a hop below the ground.
            floor = GroundHeight(transform.position.x, transform.position.z);
            if (transform.position.y < floor)
            {
                transform.position = new Vector3(transform.position.x, floor, transform.position.z);
                _verticalVelocity = 0f;
            }
        }

        /// <summary>Ground height at a spot (procedural world, town included), with a safe fallback.</summary>
        float GroundHeight(float x, float z)
        {
            if (_chunks == null) _chunks = FindChunkManager();
            if (_chunks == null) return transform.position.y;

            try { return _chunks.SampleHeight(x, z); }
            catch { _chunks = null; return transform.position.y; }
        }

        static ChunkManager FindChunkManager()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<ChunkManager>(FindObjectsInactive.Include);
#else
            return Object.FindObjectOfType<ChunkManager>(true);
#endif
        }

        void FaceTravelDirection(Vector3 dir, float dt)
        {
            if (model == null || dir.sqrMagnitude < 0.0001f) return;

            Quaternion want = Quaternion.LookRotation(dir, Vector3.up)
                              * Quaternion.Euler(0f, modelYawOffset, 0f) * _restRotation;

            model.rotation = Quaternion.RotateTowards(model.rotation, want,
                                                      Mathf.Max(0f, turnSpeed) * dt);
        }

        /// <summary>
        /// The squash / stretch: squashed on landing, stretched while rising, breathing while it
        /// rests. Applied along the model's own up axis, with its feet stuck to the ground.
        /// </summary>
        void AnimateModel(bool moving, float dt)
        {
            if (model == null) return;

            float target;
            if (IsGrounded())
            {
                if (moving) target = 1f - squash;                                         // squashed on impact
                else if (idleBreathe) target = 1f + Mathf.Sin(Time.time * 2.2f) * 0.04f;  // gentle breathing
                else target = 1f;
            }
            else
            {
                target = 1f + Mathf.Clamp(_verticalVelocity * Mathf.Max(0f, bounceStretch), -squash, stretch);
            }

            float t = 1f - Mathf.Exp(-Mathf.Max(0.01f, squashBlend) * dt);
            _scaleY = Mathf.Lerp(_scaleY, Mathf.Clamp(target, 0.4f, 1.8f), t);

            float sxz = 1f / Mathf.Sqrt(_scaleY);   // keep the slime's volume roughly constant

            Vector3 scale = _restScale;
            scale.x = _restScale.x * sxz;
            scale.z = _restScale.z * sxz;
            if (_upAxis == 1) scale.y = _restScale.y * _scaleY;
            else if (_upAxis == 0) scale.x = _restScale.x * _scaleY;
            else scale.z = _restScale.z * _scaleY;

            model.localScale = scale;

            // Keep the bottom of the slime planted while it squashes (the model's pivot may not
            // be at its feet, so the offset is measured instead of assumed).
            float y = _restLocalY + (_bottomY - _restLocalY) * (1f - _scaleY);
            model.localPosition = new Vector3(model.localPosition.x, y, model.localPosition.z);
        }

        bool IsGrounded()
            => transform.position.y <= GroundHeight(transform.position.x, transform.position.z) + 0.04f;

        // =====================================================================
        //  Tools
        // =====================================================================
        /// <summary>
        /// Remembers how the model came out of the FBX importer, once: the rest rotation (a Z-up
        /// file arrives with a -90 degree X on its root) and which of the model's own axes points
        /// up. Scaling happens before that rotation, so the vertical half of the squash has to
        /// travel along <see cref="_upAxis"/> instead of always Y.
        /// </summary>
        void CaptureRestPose()
        {
            if (_restPoseCaptured || model == null) return;
            _restPoseCaptured = true;

            _restRotation = model.localRotation;

            Vector3 up = Quaternion.Inverse(_restRotation) * Vector3.up;
            float ax = Mathf.Abs(up.x), ay = Mathf.Abs(up.y), az = Mathf.Abs(up.z);
            _upAxis = (ax > ay && ax > az) ? 0 : (az > ay ? 2 : 1);
        }

        /// <summary>The first child that actually has geometry (skips cameras, lights and audio).</summary>
        Transform FindModel()
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child == null) continue;
                if (child.GetComponent<Camera>() != null) continue;
                if (child.GetComponentInChildren<Renderer>(true) != null) return child;
            }
            return null;
        }

        /// <summary>Bounds of everything the model draws, expressed in this object's local space.</summary>
        bool TryGetModelBounds(out Bounds localBounds)
        {
            localBounds = new Bounds();
            if (model == null) return false;

            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;

                Bounds b = r.bounds;   // world space
                Vector3 c = b.center, e = b.extents;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        c.x + ((i & 1) == 0 ? -e.x : e.x),
                        c.y + ((i & 2) == 0 ? -e.y : e.y),
                        c.z + ((i & 4) == 0 ? -e.z : e.z));
                    Vector3 local = transform.InverseTransformPoint(corner);
                    if (!any) { localBounds = new Bounds(local, Vector3.zero); any = true; }
                    else localBounds.Encapsulate(local);
                }
            }
            return any;
        }

        static Vector3 FlatDirection(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.zero;
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        void OnDrawGizmosSelected()
        {
            Vector3 home = Application.isPlaying ? _home : transform.position;

            Gizmos.color = new Color(0.5f, 1f, 0.6f, 0.7f);
            Gizmos.DrawWireSphere(home, Mathf.Max(0.1f, wanderRadius));

            if (!Application.isPlaying) return;

            Gizmos.color = new Color(1f, 0.85f, 0.3f, 0.9f);
            Gizmos.DrawLine(transform.position, _target);
            Gizmos.DrawWireSphere(_target, 0.2f);
        }
    }
}
