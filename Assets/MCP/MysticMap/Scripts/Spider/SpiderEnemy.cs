using System.Collections.Generic;
using UnityEngine;
using MysticMap.World;

namespace MysticMap
{
    /// <summary>
    /// A hostile fantasy spider that hunts the slime.
    ///
    /// It is built from the "Assets/fantasySpider" pack (the animated spider FBX), but nothing in
    /// here depends on that model: drop the component on any creature with a collider and it will
    /// walk, chase, bite and cast.
    ///
    /// Behaviour:
    ///   * it lives around a home spot and wanders there while nothing is in sight,
    ///   * the moment the slime comes within sight it turns hostile and runs at it,
    ///   * close up it bites (a short wind-up, then damage through <see cref="Damage"/>),
    ///   * from a distance it casts a long-range magic slash: a <see cref="MagicProjectile"/> fired
    ///     at the player, exactly the way the slime's own blade spells work, so it flies over the
    ///     ground, plays the Hovl effect and hurts whatever has an <see cref="IDamageable"/>
    ///     (the player, see <see cref="PlayerVitals"/>),
    ///   * it animates through a legacy Animation component (the pack's clips are legacy clips, so
    ///     it plays idle / walk / run / attack1 / attack2 / death2 by name) and simply keeps
    ///     moving if there is no animation at all.
    ///
    /// Movement uses a CharacterController and follows the streamed terrain through
    /// <see cref="ChunkManager.SampleHeight"/>, so it works anywhere in the procedural world.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MysticMap/Spider Enemy (bite + ranged magic)")]
    public class SpiderEnemy : MonoBehaviour
    {
        /// <summary>What the spider is doing right now.</summary>
        public enum SpiderState
        {
            /// <summary>Standing around, slowly looking about.</summary>
            Idle,
            /// <summary>Walking to a random spot near home.</summary>
            Wander,
            /// <summary>Running at the player.</summary>
            Chase,
            /// <summary>Standing still during a bite wind-up.</summary>
            Bite,
            /// <summary>Standing still during a spell wind-up.</summary>
            Cast,
            /// <summary>Dead (the Health component removes the object).</summary>
            Dead
        }

        [Header("Who it hunts")]
        [Tooltip("The victim (auto-filled with the slime when empty).")]
        public Transform target;
        [Tooltip("Look for the SlimePlayer automatically.")]
        public bool autoFindPlayer = true;
        [Tooltip("How far (m) the spider notices the player from.")]
        public float sightRange = 34f;
        [Tooltip("Once angry, how far (m) the player must get away before the spider calms down.")]
        public float loseRange = 62f;
        [Tooltip("What the bites and spells may hit.")]
        public LayerMask hitMask = ~0;

        [Header("Movement")]
        [Tooltip("Speed (m/s) while wandering (nothing to chase).")]
        public float walkSpeed = 1.9f;
        [Tooltip("Speed (m/s) while chasing the player.")]
        public float runSpeed = 4.6f;
        [Tooltip("How fast (degrees/s) the spider turns to face where it goes.")]
        public float turnSpeed = 280f;
        [Tooltip("Downward acceleration (m/s^2).")]
        public float gravity = 22f;
        [Tooltip("Do not come closer than this to the victim while chasing.")]
        public float stopDistance = 2.2f;

        [Header("Wander (idle life)")]
        [Tooltip("Radius (m) around its home spot the spider walks in when nothing is in sight.")]
        public float wanderRadius = 12f;
        [Tooltip("Seconds it rests at each wander point before picking the next one.")]
        public float wanderPause = 2.6f;
        [Tooltip("Degrees/s the spider slowly turns while resting (looking about).")]
        public float idleTurnSpeed = 25f;

        [Header("Bite (close range)")]
        [Tooltip("How close (m) the player has to be for a bite.")]
        public float biteRange = 3.2f;
        [Tooltip("Damage of one bite.")]
        public float biteDamage = 9f;
        [Tooltip("Seconds of wind-up before the bite lands (matches the attack animation).")]
        public float biteWindup = 0.4f;
        [Tooltip("Seconds between two bites.")]
        public float biteCooldown = 1.9f;
        [Tooltip("Knockback of the bite (m, 0 = none).")]
        public float biteKnockback = 2.5f;
        [Tooltip("Effect played on the victim of a bite (any impact effect).")]
        public GameObject biteImpactEffect;

        [Header("Ranged magic (the flying slash)")]
        [Tooltip("Let the spider cast its long-range spell.")]
        public bool useMagic = true;
        [Tooltip("The spell: its castEffect is the projectile prefab, the rest is damage / speed / " +
                 "radius / impact effect. Filled from the Hovl pack by the prefab builder.")]
        public MagicSpellLevel spell = new MagicSpellLevel();
        [Tooltip("Closest (m) the player can be while the spider prefers magic.")]
        public float castMinRange = 7f;
        [Tooltip("Farthest (m) the spider may cast at.")]
        public float castMaxRange = 33f;
        [Tooltip("Seconds of wind-up before the spell leaves the spider.")]
        public float castWindup = 0.65f;
        [Tooltip("Seconds between two casts.")]
        public float castCooldown = 5.5f;
        [Tooltip("Height (m) above the spider's feet the spell comes out of.")]
        public float muzzleHeight = 0.85f;
        [Tooltip("Height (m) above the player's feet the spell is aimed at.")]
        public float aimHeight = 0.7f;
        [Tooltip("Only cast when the player can actually be seen (no magic through hills).")]
        public bool requireLineOfSight = true;
        [Tooltip("Effect played around the spider while the spell charges (optional).")]
        public GameObject castChargeEffect;

        [Header("Animation (the pack's clips)")]
        [Tooltip("The Animation component on the spider model (auto-filled from the children). " +
                 "The fantasySpider pack is a LEGACY animation set, so its clips are played through " +
                 "this component - an Animator cannot use them.")]
        public Animation spiderAnimation;
        [Tooltip("Clip played while standing still.")]
        public string idleClip = "idle";
        [Tooltip("Clip played while wandering.")]
        public string walkClip = "walk";
        [Tooltip("Clip played while chasing the player.")]
        public string runClip = "run";
        [Tooltip("Clip played for a bite.")]
        public string biteClip = "attack1";
        [Tooltip("Clip played while casting the spell.")]
        public string spellClip = "attack2";
        [Tooltip("Clip played once when the spider dies.")]
        public string deathClip = "death2";
        [Tooltip("Seconds a change of clip takes to blend.")]
        [Range(0.02f, 0.6f)] public float clipFade = 0.15f;

        // =====================================================================
        //  Model fit - moving / scaling the MESH, never the collider
        // =====================================================================
        /// <summary>How the mesh finds the ground under it.</summary>
        public enum FootAlign
        {
            /// <summary>Never touch the mesh height (the prefab's own offset is used as-is).</summary>
            Off,
            /// <summary>Sample the ground straight under the body (best on gentle ground).</summary>
            Center,
            /// <summary>Also probe around the body and stand on the lowest ground (hills, dips).</summary>
            Lowest,
            /// <summary>Also probe around the body and stand on the highest ground (never sink in).</summary>
            Highest
        }

        /// <summary>Which part of the mesh has to touch the ground.</summary>
        public enum FootPart
        {
            /// <summary>
            /// Everything except the head and fangs (the pack's animated fangs / palps hang lower than the
            /// feet, so measuring them too would keep the legs in the air). This is what a player reads as
            /// the feet, and it can never leave a claw out of the measurement.
            /// </summary>
            Claws,
            /// <summary>Everything the renderers draw (the lowest vertex of the whole spider).</summary>
            WholeMesh
        }

        [Header("Mesh (size and offset)")]
        [Tooltip("The spider mesh inside the prefab. Auto-filled: the object that carries the Animation. " +
                 "Everything in this section moves / scales THIS - never the collider - so the look and " +
                 "the walking stay independent.")]
        public Transform model;
        [Tooltip("Multiplies the mesh size. 1 = the size the prefab was built with, 0.5 = half as big, " +
                 "2 = twice as big.")]
        [Range(0.1f, 3f)] public float modelScale = 1f;
        [Tooltip("Metres to raise (+) or sink (-) the mesh. Use it when the legs float above the ground " +
                 "or dig into it.")]
        [Range(-2f, 2f)] public float modelHeight;
        [Tooltip("Metres to push the mesh forward (+) so the body sits over the front legs.")]
        [Range(-2f, 2f)] public float modelForward;
        [Tooltip("Metres to push the mesh sideways (+ = to its right).")]
        [Range(-2f, 2f)] public float modelSide;

        [Header("Feet on the ground")]
        [Tooltip("Keep the feet on the real ground, so the pack's animated pose (its claws hang far below " +
                 "the body) or a slope never leaves the spider floating. Center = sample under the body; " +
                 "Lowest / Highest also probe around it (better on hills). Off = never touch the mesh height.")]
        public FootAlign feetAlign = FootAlign.Center;
        [Tooltip("What counts as the feet: the lowest point of the body and legs (the claws). The renderer " +
                 "bounds also cover the animated fangs, which hang lower, so they are measured only when " +
                 "'Foot part' is 'Whole mesh'.")]
        public FootPart footPart = FootPart.Claws;
        [Tooltip("How far (m) around the body the extra ground probes are taken.")]
        [Range(0f, 2f)] public float probeRadius = 0.6f;
        [Tooltip("Extra gap (m) kept between the lowest leg and the ground. Small positive = just touching.")]
        [Range(-0.5f, 0.5f)] public float footGap;
        [Tooltip("Seconds between two ground probes (0.08 = about 12 checks a second).")]
        [Range(0.02f, 0.5f)] public float alignInterval = 0.08f;
        [Tooltip("How fast the mesh glides to the ground (higher = snappier). 0 = snapped every frame.")]
        [Range(0f, 40f)] public float alignSpeed = 14f;

        [Header("Hit box (the CharacterController)")]
        [Tooltip("Override the collider the prefab was built with. Off = keep the prefab's own numbers.")]
        public bool tuneCollider;
        [Tooltip("Radius (m) of the collider capsule. The legs stick out much further than the body, so a " +
                 "small radius that follows the body usually reads best.")]
        public float colliderRadius = 0.5f;
        [Tooltip("Total height (m) of the collider capsule.")]
        public float colliderHeight = 1.7f;
        [Tooltip("Height (m) of the capsule's centre above the feet (half the height puts its bottom on the ground).")]
        public float colliderCenterY = 0.85f;
        [Tooltip("How high a step (m) the spider can walk up. Keep it low so two spiders cannot climb on top " +
                 "of each other.")]
        [Range(0f, 1f)] public float colliderStepOffset = 0.3f;

        [Header("Personal space")]
        [Tooltip("Try to keep this far (m) from other spiders so they do not pile up.")]
        [Range(0f, 10f)] public float personalSpace = 2.5f;
        [Tooltip("How hard they push apart when they are too close (0 = never).")]
        [Range(0f, 4f)] public float separationPush = 2.5f;

        [Header("Death")]
        [Tooltip("Safety net: the spider is removed this long after dying even if Health does not do it.")]
        public float deathRemoveDelay = 4f;

        [Tooltip("Filled by the prefab builder: where the prefab itself put the mesh. The offsets above are " +
                 "always applied on top of this, so tweaking them never drifts.")]
        [HideInInspector] public Vector3 modelBasePosition;
        [HideInInspector] public Vector3 modelBaseScale = Vector3.one;
        [HideInInspector] public bool modelBaseSet;

        [Tooltip("Which version of the prefab builder produced this spider. The tool rebuilds a prefab " +
                 "that was made by an older version, so new fit settings arrive by themselves.")]
        [HideInInspector] public int builtBy;

        // ---- state ----------------------------------------------------------
        SpiderState _state = SpiderState.Idle;
        Health _health;
        CharacterController _controller;
        ChunkManager _chunks;
        Collider _targetCollider;

        Vector3 _home;
        Vector3 _wanderPoint;

        float _wanderTimer;
        float _biteTimer;
        float _castTimer;
        float _windup;
        float _groundTimer;
        SpiderState _pending;
        bool _windingUp;
        bool _aggro;
        float _verticalVelocity;

        // ---- animation ------------------------------------------------------
        string _playingClip;

        // ---- mesh fitting ---------------------------------------------------
        Renderer[] _modelRenderers;
        float _lift;                  // extra height the feet-on-the-ground solve is adding right now
        float _alignTimer;
        bool _solved;                 // the feet have found the ground at least once (start from 0 = no pop)

        // The mesh is measured from its live skinned vertices (see LowestModelY), so the last solve can
        // never leave more than a hair of air under the claws however quickly the animation moves them.
        Mesh _cachedMesh;
        Vector3[] _meshPositions;
        BoneWeight[] _meshBoneWeights;

        Transform[] _bones;           // the bones that deform the mesh (the rig's skeleton)
        bool[] _weightsFollowBones;   // which of them carry the head (null = none found, measure all)
        BoneWeight[] _weightCache;    // _meshBoneWeights with the head's vertices taken out

        /// <summary>How far (m) the feet solver may move the mesh before it gives up for this pose.</summary>
        float _maxLift => Mathf.Max(1.5f, (model != null ? model.localScale.y : 1f) * 2.5f);

        /// <summary>Every spider alive, so they can keep out of each other's way.</summary>
        static readonly List<SpiderEnemy> All = new List<SpiderEnemy>(16);

        /// <summary>What the spider is doing (handy for debugging / tests).</summary>
        public SpiderState State => _state;
        /// <summary>True while the spider is hunting the player.</summary>
        public bool IsAggro => _aggro;
        /// <summary>The hit points component (null when the prefab has none).</summary>
        public Health Health => _health;

        // =====================================================================
        //  Lifecycle
        // =====================================================================
        void Awake()
        {
            _health = GetComponent<Health>();
            _controller = GetComponent<CharacterController>();

            if (spiderAnimation == null) spiderAnimation = GetComponentInChildren<Animation>();
            if (spiderAnimation != null) spiderAnimation.playAutomatically = false;

            EnsureModel();
            if (tuneCollider) ApplyCollider();
            ApplyModelFit();

            _home = transform.position;
            _wanderPoint = _home;
        }

        void OnEnable()
        {
            if (!All.Contains(this)) All.Add(this);

            if (_health == null) return;
            _health.Died += OnDied;
            _health.Revived += OnRevived;
        }

        void OnDisable()
        {
            All.Remove(this);

            if (_health == null) return;
            _health.Died -= OnDied;
            _health.Revived -= OnRevived;
        }

        void Start()
        {
            ResolveTarget();
            SnapToGround();
            PickWanderPoint();
        }

        // =====================================================================
        //  Animation (legacy clips - that is what the spider pack ships)
        // =====================================================================
        /// <summary>Keeps a looping clip (idle / walk / run) on screen, blending into it.</summary>
        void PlayLoop(string clip)
        {
            if (spiderAnimation == null || string.IsNullOrEmpty(clip)) return;
            if (spiderAnimation.GetClip(clip) == null) return;                 // the pack renamed it
            if (_playingClip == clip && spiderAnimation.IsPlaying(clip)) return;

            _playingClip = clip;
            spiderAnimation.CrossFade(clip, Mathf.Max(0.02f, clipFade));
        }

        /// <summary>Plays a one-shot clip (a bite, a spell, the death) from its start.</summary>
        void PlayOnce(string clip, bool clamp)
        {
            if (spiderAnimation == null || string.IsNullOrEmpty(clip)) return;

            AnimationState state = spiderAnimation.GetClip(clip) != null ? spiderAnimation[clip] : null;
            if (state == null) return;

            state.wrapMode = clamp ? WrapMode.ClampForever : WrapMode.Once;
            state.time = 0f;
            state.speed = 1f;

            spiderAnimation.Play(clip, PlayMode.StopAll);
            _playingClip = clip;
        }

        /// <summary>Picks the clip that matches what the spider is doing right now.</summary>
        void AnimateLocomotion()
        {
            switch (_state)
            {
                case SpiderState.Chase: PlayLoop(runClip); break;
                case SpiderState.Wander: PlayLoop(walkClip); break;
                case SpiderState.Bite:
                case SpiderState.Cast:
                case SpiderState.Dead: break;                              // a one-shot has the stage
                default: PlayLoop(idleClip); break;
            }
        }

        // =====================================================================
        //  Main loop
        // =====================================================================
        void Update()
        {
            float dt = Time.deltaTime;

            Tick(dt);
            AlignFeet(dt);
        }

        void Tick(float dt)
        {
            if (_state == SpiderState.Dead) return;

            ResolveTarget();
            TickTimers(dt);

            if (_windingUp)
            {
                _windup -= dt;

                if (_state == SpiderState.Cast) FaceTarget(dt);
                Move(Vector3.zero, 0f, dt);
                AnimateLocomotion();

                if (_windup <= 0f) LandAttack();
                return;
            }

            float distance = TargetDistance();

            if (distance <= sightRange) _aggro = true;
            if (_aggro && (target == null || distance > loseRange)) _aggro = false;

            if (_aggro && target != null) Hunt(distance, dt);
            else Wander(dt);

            AnimateLocomotion();
        }

        void TickTimers(float dt)
        {
            if (_biteTimer > 0f) _biteTimer -= dt;
            if (_castTimer > 0f) _castTimer -= dt;
            if (_wanderTimer > 0f) _wanderTimer -= dt;
            if (_groundTimer > 0f) _groundTimer -= dt;
        }

        void ResolveTarget()
        {
            if (target != null || !autoFindPlayer) return;

            var players = FindObjectsByType<SlimePlayer>();
            if (players != null && players.Length > 0) target = players[0].transform;
        }

        // =====================================================================
        //  Hunting
        // =====================================================================
        void Hunt(float distance, float dt)
        {
            if (CanCast(distance)) { BeginCast(); return; }

            if (distance <= biteRange)
            {
                if (_biteTimer <= 0f) { BeginBite(); return; }

                Move(Vector3.zero, 0f, dt);      // recovering from the last bite
                FaceTarget(dt);
                return;
            }

            _state = SpiderState.Chase;
            FaceTarget(dt);

            Vector3 toTarget = Flat(target.position - transform.position);
            float travel = Mathf.Max(0f, distance - stopDistance);

            if (toTarget.sqrMagnitude > 0.0001f && travel > 0.05f)
            {
                // Steer around the other spiders instead of walking over them.
                Vector3 steer = toTarget.normalized + Separation() * separationPush;
                Move(steer, Mathf.Min(runSpeed, travel * 2f), dt);
            }
            else
            {
                Move(Vector3.zero, 0f, dt);
            }
        }

        /// <summary>How strongly the other spiders push this one away (0 = nobody nearby).</summary>
        Vector3 Separation()
        {
            if (separationPush <= 0f || personalSpace <= 0f) return Vector3.zero;

            Vector3 push = Vector3.zero;
            float space = Mathf.Max(0.5f, personalSpace);

            for (int i = 0; i < All.Count; i++)
            {
                SpiderEnemy other = All[i];
                if (other == null || other == this) continue;

                Vector3 away = Flat(transform.position - other.transform.position);
                float distance = away.magnitude;
                if (distance > space) continue;

                if (distance < 0.01f)
                    away = new Vector3(Random.value - 0.5f, 0f, Random.value - 0.5f);

                push += away.normalized * (1f - distance / space);
            }

            return push;
        }

        bool CanCast(float distance)
        {
            if (!useMagic || spell == null || spell.castEffect == null) return false;
            if (_castTimer > 0f) return false;
            if (target == null) return false;
            if (distance < castMinRange || distance > castMaxRange) return false;
            if (requireLineOfSight && !HasLineOfSight()) return false;

            return true;
        }

        void BeginBite()
        {
            _state = SpiderState.Bite;
            _pending = SpiderState.Bite;
            _windingUp = true;
            _windup = Mathf.Max(0.05f, biteWindup);
            _biteTimer = Mathf.Max(biteWindup, biteCooldown);

            PlayOnce(biteClip, false);
        }

        void BeginCast()
        {
            _state = SpiderState.Cast;
            _pending = SpiderState.Cast;
            _windingUp = true;
            _windup = Mathf.Max(0.05f, castWindup);
            _castTimer = Mathf.Max(castWindup, castCooldown);

            PlayOnce(spellClip, false);

            if (castChargeEffect != null)
            {
                GameObject fx = MagicEffects.Spawn(castChargeEffect, Muzzle(), transform.rotation, transform,
                                                   Mathf.Max(0.05f, spell.castScale));
                MagicEffects.DestroyLater(fx, _windup + 0.25f);
            }
        }

        /// <summary>Runs the bite / spell the wind-up was for.</summary>
        void LandAttack()
        {
            bool bite = _pending == SpiderState.Bite;
            _windingUp = false;

            if (bite) DoBite();
            else LaunchSpell();

            _state = SpiderState.Idle;
        }

        void DoBite()
        {
            Collider victim = TargetCollider();
            if (victim == null) return;

            Vector3 point = victim.bounds.center;
            Vector3 direction = point - (transform.position + Vector3.up * muzzleHeight);
            if (direction.sqrMagnitude < 0.0001f) direction = transform.forward;

            var info = new DamageInfo(biteDamage * Random.Range(0.9f, 1.1f), point, direction,
                                      biteKnockback, gameObject);

            Damage.Apply(victim, info, transform, biteImpactEffect);
            MagicEnvironment.Impact(point, 2.6f, 0.35f);
        }

        /// <summary>Fires the ranged slash at the player through the shared projectile code.</summary>
        void LaunchSpell()
        {
            if (!useMagic || spell == null || spell.castEffect == null) return;
            if (target == null) return;

            Vector3 origin = Muzzle();
            Vector3 aim = target.position + Vector3.up * aimHeight;
            Vector3 direction = Flat(aim - origin);
            if (direction.sqrMagnitude < 0.0001f) direction = Flat(transform.forward);

            var spec = new MagicCastSpec
            {
                damage = spell.damage,
                power = 0.45f,
                environmentScale = Mathf.Max(0f, spell.environmentScale),
                direction = direction.normalized,
                debrisPerHit = 0
            };

            MagicProjectile.Launch(spell.castEffect, origin, direction, spell, spec, gameObject, hitMask);
        }

        /// <summary>Where its magic comes out: in front of the body, at chest height.</summary>
        Vector3 Muzzle() => transform.position + Vector3.up * muzzleHeight + Flat(transform.forward) * 0.7f;

        /// <summary>True when nothing solid stands between the spider's muzzle and the player.</summary>
        bool HasLineOfSight()
        {
            if (target == null) return false;

            Vector3 from = Muzzle() + Vector3.up * 0.15f;
            Vector3 to = target.position + Vector3.up * aimHeight;
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance < 0.05f) return true;

            // Only a *blocked* ray matters: whatever we hit, if it belongs to the player (or to
            // another creature) the shot is clear. Anything else is a hill or a wall in the way.
            if (Physics.Raycast(from, delta / distance, out RaycastHit hit, distance, hitMask,
                                QueryTriggerInteraction.Ignore))
            {
                if (hit.transform == target || hit.transform.IsChildOf(target)) return true;
                return Damage.Find(hit.collider) != null;
            }

            return true;
        }

        // =====================================================================
        //  Wandering (nothing in sight)
        // =====================================================================
        void Wander(float dt)
        {
            Vector3 toPoint = Flat(_wanderPoint - transform.position);

            if (_wanderTimer > 0f && toPoint.sqrMagnitude <= 0.25f)
            {
                _state = SpiderState.Idle;
                transform.Rotate(0f, idleTurnSpeed * dt, 0f, Space.World);
                Move(Vector3.zero, 0f, dt);
                return;
            }

            if (toPoint.sqrMagnitude <= 0.25f) { PickWanderPoint(); Move(Vector3.zero, 0f, dt); return; }

            _state = SpiderState.Wander;
            Move(toPoint.normalized, walkSpeed, dt);
        }

        void PickWanderPoint()
        {
            _wanderTimer = Mathf.Max(0.5f, wanderPause);

            Vector2 offset = Random.insideUnitCircle * Mathf.Max(1f, wanderRadius);
            var spot = new Vector3(_home.x + offset.x, 0f, _home.z + offset.y);
            spot.y = GroundHeight(spot.x, spot.z, transform.position.y);
            _wanderPoint = spot;
        }

        // =====================================================================
        //  Movement / grounding
        // =====================================================================
        /// <summary>Walks a flat direction, turns to face it and applies gravity.</summary>
        void Move(Vector3 direction, float speed, float dt)
        {
            direction = Flat(direction);

            if (direction.sqrMagnitude > 0.0001f)
            {
                direction.Normalize();
                Face(direction, dt * (_state == SpiderState.Chase ? 1.6f : 1f));
            }

            if (_controller != null && _controller.enabled)
            {
                if (_controller.isGrounded && _verticalVelocity < 0f) _verticalVelocity = -2f;
                _verticalVelocity -= gravity * dt;

                Vector3 motion = direction * speed;
                motion.y = _verticalVelocity;
                _controller.Move(motion * dt);
                return;
            }

            // No controller (a plain creature object): move the transform and follow the ground.
            if (speed > 0.01f) transform.position += direction * speed * dt;

            if (_groundTimer <= 0f)
            {
                _groundTimer = 0.2f;

                Vector3 p = transform.position;
                p.y = GroundHeight(p.x, p.z, p.y);
                transform.position = p;
            }
        }

        /// <summary>Turns towards a flat direction at the configured turn speed.</summary>
        void Face(Vector3 direction, float dt)
        {
            direction = Flat(direction);
            if (direction.sqrMagnitude < 0.0001f) return;

            Quaternion want = Quaternion.LookRotation(direction.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, want,
                                                          Mathf.Max(30f, turnSpeed) * dt);
        }

        void FaceTarget(float dt)
        {
            if (target == null) return;

            Vector3 to = Flat(target.position - transform.position);
            if (to.sqrMagnitude > 0.0001f) Face(to, dt);
        }

        void SnapToGround()
        {
            Vector3 p = transform.position;
            Vector3 want = new Vector3(p.x, GroundHeight(p.x, p.z, p.y), p.z);

            if (_controller != null && _controller.enabled)
            {
                Vector3 delta = want - p;
                if (delta.sqrMagnitude > 0.0001f) _controller.Move(delta);
            }
            else transform.position = want;

            _verticalVelocity = 0f;
        }

        /// <summary>Ground height anywhere: the streamed world when it is there, a ray otherwise.</summary>
        float GroundHeight(float x, float z, float fallbackY)
        {
            if (_chunks == null) _chunks = FindFirstObjectByType<ChunkManager>();

            if (_chunks != null && _chunks.Generator != null) return _chunks.SampleHeight(x, z);

            Vector3 from = new Vector3(x, fallbackY + 8f, z);
            if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, 90f, hitMask,
                                QueryTriggerInteraction.Ignore))
                return hit.point.y;

            return fallbackY;
        }

        // =====================================================================
        //  Mesh fitting (size, offset, feet on the ground, hit box)
        // =====================================================================
        /// <summary>Finds the mesh object and remembers where the prefab put it.</summary>
        void EnsureModel()
        {
            if (model == null) model = ModelTransform();
            if (model == null) return;

            if (!modelBaseSet)
            {
                modelBasePosition = model.localPosition;
                modelBaseScale = model.localScale;
                modelBaseSet = true;
            }

            if (_modelRenderers == null || _modelRenderers.Length == 0)
                _modelRenderers = model.GetComponentsInChildren<Renderer>(true);
        }

        /// <summary>
        /// The object the pack's Animation lives on - that is the mesh root. Falls back to the first
        /// child that draws anything.
        /// </summary>
        Transform ModelTransform()
        {
            if (spiderAnimation != null) return spiderAnimation.transform;

            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child != null && child.GetComponentInChildren<Renderer>() != null) return child;
            }

            return null;
        }

        /// <summary>
        /// Puts the mesh at the prefab's own position / scale, then adds the size and the offsets set
        /// in the Inspector (and the feet-on-the-ground lift). The collider is never touched here, so
        /// making the spider look bigger never changes how it walks or what the spells hit.
        /// </summary>
        void ApplyModelFit()
        {
            if (model == null) return;

            model.localScale = modelBaseScale * Mathf.Max(0.05f, modelScale);

            Vector3 offset = Vector3.forward * modelForward +
                             Vector3.right * modelSide +
                             Vector3.up * (modelHeight + _lift);

            model.localPosition = modelBasePosition + offset;
        }

        /// <summary>
        /// Keeps the lowest leg on the real ground. The collider only knows one contact point, so on a
        /// slope - or while the walk / run clip bends the legs - the mesh would float. This measures the
        /// ground under the spider AND the animated mesh itself, then slides the mesh down (or up) onto it.
        ///
        /// The measurement uses the live skinned vertices: a SkinnedMeshRenderer's bounds are only updated
        /// per frame and try to wrap the whole animation, so on the pack's spider (whose claws hang a long
        /// way below the body in the animated pose) they are far too tall and the legs end up in the air.
        /// </summary>
        void AlignFeet(float dt)
        {
            if (model == null) return;

            if (feetAlign == FootAlign.Off)
            {
                if (_lift != 0f)
                {
                    _lift = 0f;
                    ApplyModelFit();
                }

                return;
            }

            // Gliding towards the last solution (alignSpeed 0 = snapped every frame).
            if (_alignTimer > 0f && alignSpeed > 0f)
            {
                _alignTimer -= dt;
                ApplyModelFit();
                return;
            }

            _alignTimer = Mathf.Max(0.02f, alignInterval);

            float ground = ProbeGround();
            if (float.IsNaN(ground)) return;

            float lowest = LowestModelY();
            if (float.IsNaN(lowest)) return;

            // How much higher the mesh still has to move for its lowest point to touch the ground.
            float want = Mathf.Clamp(ground + footGap - lowest, -_maxLift, _maxLift);

            // The very first solve snaps, so a freshly spawned spider never visibly settles; after that it
            // glides, which keeps the walk smooth on bumpy ground.
            if (!_solved || alignSpeed <= 0f) _lift = want;
            else _lift += want * Mathf.Clamp01(alignSpeed * Mathf.Max(0.02f, alignInterval));

            _lift = Mathf.Clamp(_lift, -_maxLift, _maxLift);
            _solved = true;

            // A float must never outlive one frame: if the mesh is still too high (an odd pose, a steep
            // slope), snap the rest of the gap away instead of leaving the spider hovering.
            float after = LowestModelY();
            if (!float.IsNaN(after) && after - (ground + footGap) > 0.01f)
            {
                _lift = Mathf.Clamp(_lift + (ground + footGap - after), -_maxLift, _maxLift);
            }

            ApplyModelFit();
        }

        /// <summary>Ground height the feet should rest on (see <see cref="feetAlign"/>).</summary>
        float ProbeGround()
        {
            Vector3 p = transform.position;

            switch (feetAlign)
            {
                case FootAlign.Lowest: return GroundAround(p, true);
                case FootAlign.Highest: return GroundAround(p, false);
                default: return GroundHeight(p.x, p.z, p.y);
            }
        }

        /// <summary>The lowest (or highest) ground within <see cref="probeRadius"/> of the body.</summary>
        float GroundAround(Vector3 centre, bool takeLowest)
        {
            float best = GroundHeight(centre.x, centre.z, centre.y);
            float radius = Mathf.Max(0f, probeRadius);

            for (int i = 0; i < 4; i++)
            {
                float angle = i * Mathf.PI * 0.5f;
                float x = centre.x + Mathf.Cos(angle) * radius;
                float z = centre.z + Mathf.Sin(angle) * radius;
                float height = GroundHeight(x, z, centre.y);

                best = takeLowest ? Mathf.Min(best, height) : Mathf.Max(best, height);
            }

            return best;
        }

        /// <summary>
        /// World height of the lowest point the mesh really draws right now (the animated pose, not the
        /// static bounds). Skinned vertices are read back from their bones, so this is exactly where the
        /// claws are. Renderers that cannot be read (no CPU copy of the mesh) fall back to their bounds.
        /// </summary>
        float LowestModelY()
        {
            if (_modelRenderers == null || _modelRenderers.Length == 0) return transform.position.y;

            bool any = false;
            float lowest = float.MaxValue;

            for (int i = 0; i < _modelRenderers.Length; i++)
            {
                Renderer r = _modelRenderers[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;

                float min;

                if (r is SkinnedMeshRenderer skinned && LowestSkinnedY(skinned, out min)) { /* measured */ }
                else min = r.bounds.min.y;

                if (!any || min < lowest) { lowest = min; any = true; }
            }

            return any ? lowest : transform.position.y;
        }

        /// <summary>
        /// The lowest animated vertex of a skinned mesh: the mesh's own vertices deformed by their current
        /// bones (the same maths the GPU does). With <see cref="FootPart.Claws"/> the head is left out, so
        /// a lowered fang never lifts the feet off the ground.
        /// </summary>
        bool LowestSkinnedY(SkinnedMeshRenderer skinned, out float lowest)
        {
            lowest = 0f;

            Mesh mesh = skinned.sharedMesh;
            if (mesh == null || !mesh.isReadable) return false;

            if (_meshPositions == null || _cachedMesh != mesh)
            {
                _cachedMesh = mesh;
                _meshPositions = mesh.vertices;
                _meshBoneWeights = mesh.boneWeights;
                _weightCache = null;

                if (_meshPositions == null || _meshBoneWeights == null ||
                    _meshPositions.Length != _meshBoneWeights.Length)
                {
                    _meshPositions = null;
                    _meshBoneWeights = null;
                    return false;
                }
            }

            if (_meshPositions.Length == 0) return false;

            Transform[] bones = skinned.bones;
            if (bones == null || bones.Length == 0) return false;

            if (_bones != bones)
            {
                _bones = bones;
                CacheBoneMap();
            }

            Matrix4x4[] bindposes = mesh.bindposes;
            if (bindposes == null || bindposes.Length < bones.Length) return false;

            var localToWorld = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                localToWorld[i] = bones[i] != null ? bones[i].localToWorldMatrix : skinned.transform.localToWorldMatrix;

            Vector3[] positions = _meshPositions;
            BoneWeight[] weights = footPart == FootPart.Claws ? FootWeights(_meshBoneWeights) : _meshBoneWeights;

            lowest = float.MaxValue;
            bool any = false;

            for (int v = 0; v < positions.Length; v++)
            {
                BoneWeight w = weights[v];
                if (w.weight0 + w.weight1 + w.weight2 + w.weight3 <= 0f) continue;   // not moved by the feet

                Vector4 p = positions[v];

                Vector4 skinned4 = Blend(localToWorld, bindposes, w.weight0, w.boneIndex0, p)
                                 + Blend(localToWorld, bindposes, w.weight1, w.boneIndex1, p)
                                 + Blend(localToWorld, bindposes, w.weight2, w.boneIndex2, p)
                                 + Blend(localToWorld, bindposes, w.weight3, w.boneIndex3, p);

                if (!any || skinned4.y < lowest) { lowest = skinned4.y; any = true; }
            }

            return any;
        }

        /// <summary>One bone's contribution to a vertex, exactly like a skin shader does it.</summary>
        static Vector4 Blend(Matrix4x4[] localToWorld, Matrix4x4[] bindposes, float weight, int bone, Vector4 p)
        {
            if (weight <= 0f || bone < 0 || bone >= localToWorld.Length || bone >= bindposes.Length)
                return Vector4.zero;

            return localToWorld[bone].MultiplyPoint3x4(bindposes[bone].MultiplyPoint3x4(p)) * weight;
        }

        /// <summary>
        /// The vertex weights to measure with <see cref="FootPart.Claws"/>: everything except the head and
        /// fangs. Every vertex the body / legs move keeps its full weight (so no claw can be missed); only
        /// the ones the head bones carry are dropped. Without a recognisable head every vertex counts,
        /// which is exactly the same as measuring the whole mesh.
        /// </summary>
        BoneWeight[] FootWeights(BoneWeight[] weights)
        {
            if (_weightsFollowBones == null)
                return weights;                                   // no head bones found: measure everything

            if (_weightCache != null && _weightCache.Length == weights.Length) return _weightCache;

            var mapped = new BoneWeight[weights.Length];
            bool[] head = _weightsFollowBones;

            for (int v = 0; v < weights.Length; v++)
            {
                BoneWeight src = weights[v];

                // How much of this vertex is carried by the head (the fangs) - the rest is body / legs.
                float byHead = (head[SafeBone(src.boneIndex0, head.Length)] ? src.weight0 : 0f) +
                               (head[SafeBone(src.boneIndex1, head.Length)] ? src.weight1 : 0f) +
                               (head[SafeBone(src.boneIndex2, head.Length)] ? src.weight2 : 0f) +
                               (head[SafeBone(src.boneIndex3, head.Length)] ? src.weight3 : 0f);

                float total = src.weight0 + src.weight1 + src.weight2 + src.weight3;
                if (total <= 0f) { mapped[v] = src; continue; }    // rigid: no bone at all moves it

                // A fang (mostly carried by the head) must not count as a foot; anything the body or a leg
                // moves counts at its full weight, so the claws are always measured at their lowest point.
                if (1f - byHead / total <= 0.5f) { mapped[v] = default; continue; }

                mapped[v] = src;
            }

            _weightCache = mapped;
            return mapped;
        }

        static int SafeBone(int bone, int length) => bone >= 0 && bone < length ? bone : 0;

        /// <summary>
        /// Finds the bones that carry the head (its fangs hang below the feet when the pack's spider
        /// animates). A bone whose name says so wins; on a rig without such names (the pack's bones are
        /// "IK Chain16", "Dummy17", "Box30") the head is the group of bones that sits high and close to the
        /// centre of the body - the legs all reach out much further. Nothing found simply means every
        /// vertex is measured.
        /// </summary>
        void CacheBoneMap()
        {
            _weightsFollowBones = null;
            _weightCache = null;

            Transform[] bones = _bones;
            if (bones == null || bones.Length == 0) return;

            Transform root = model != null ? model : transform;

            var high = new float[bones.Length];
            var reach = new float[bones.Length];
            var named = new bool[bones.Length];
            int namedCount = 0;

            float lowest = float.MaxValue, highest = float.MinValue, widest = 0f;

            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = bones[i];
                if (bone == null) continue;

                string boneName = bone.name ?? string.Empty;
                if (boneName.IndexOf("head", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    boneName.IndexOf("jaw", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    boneName.IndexOf("fang", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    boneName.IndexOf("mandible", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    boneName.IndexOf("chelicer", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    named[i] = true;
                    namedCount++;
                }

                Vector3 flat = bone.position - root.position;
                flat.y = 0f;

                high[i] = bone.position.y;
                reach[i] = flat.magnitude;

                if (high[i] < lowest) lowest = high[i];
                if (high[i] > highest) highest = high[i];
                if (reach[i] > widest) widest = reach[i];
            }

            // A rig that names its head is trusted as it is.
            if (namedCount > 0)
            {
                _weightsFollowBones = named;
                return;
            }

            if (lowest > highest) return;                       // nothing usable

            float highLimit = lowest + Mathf.Max(0.01f, highest - lowest) * 0.55f;
            float nearLimit = Mathf.Max(0.05f, widest * 0.4f);

            var head = new bool[bones.Length];
            int found = 0;

            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;

                // The head sits high, near the centre of the body: the legs reach out much further.
                if (high[i] < highLimit || reach[i] > nearLimit) continue;

                head[i] = true;
                found++;
            }

            _weightsFollowBones = found > 0 ? head : null;
        }

        /// <summary>Overrides the collider with the numbers set in the Inspector (see <see cref="tuneCollider"/>).</summary>
        public void ApplyCollider()
        {
            if (_controller == null) _controller = GetComponent<CharacterController>();
            if (_controller == null) return;

            _controller.radius = Mathf.Max(0.05f, colliderRadius);
            _controller.height = Mathf.Max(_controller.radius * 2.01f, colliderHeight);
            _controller.center = new Vector3(0f, colliderCenterY, 0f);
            _controller.stepOffset = Mathf.Clamp(colliderStepOffset, 0f, _controller.height);
        }

        /// <summary>
        /// Re-applies every fit setting now. The tuning window calls this after changing them so the
        /// change is visible in the Scene view without entering Play mode.
        /// </summary>
        public void RefreshFit()
        {
            _controller = GetComponent<CharacterController>();

            if (model == null) model = ModelTransform();
            if (model != null && (_modelRenderers == null || _modelRenderers.Length == 0))
                _modelRenderers = model.GetComponentsInChildren<Renderer>(true);

            if (tuneCollider) ApplyCollider();

            // The leg bones move with the mesh scale, so find them again and start the solve afresh.
            CacheBoneMap();
            _solved = false;

            ApplyModelFit();
        }

        // =====================================================================
        //  Death / revival
        // =====================================================================
        void OnDied()
        {
            _state = SpiderState.Dead;
            _aggro = false;
            _windingUp = false;

            if (_controller != null) _controller.enabled = false;
            PlayOnce(deathClip, true);        // held on its last frame while the body is removed

            // Health normally removes the spider; this is only the safety net (a delayed Destroy on
            // an object that is already gone simply never runs).
            Destroy(gameObject, Mathf.Max(0.5f, deathRemoveDelay));
        }

        void OnRevived()
        {
            _state = SpiderState.Idle;
            _aggro = false;
            _windingUp = false;
            _verticalVelocity = 0f;
            _playingClip = null;
            _lift = 0f;
            _solved = false;

            if (_controller != null) _controller.enabled = true;
        }

        // =====================================================================
        //  Helpers / gizmos
        // =====================================================================
        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        float TargetDistance()
        {
            if (target == null) return float.MaxValue;
            return Flat(target.position - transform.position).magnitude;
        }

        Collider TargetCollider()
        {
            if (_targetCollider != null) return _targetCollider;
            if (target == null) return null;

            if (target.TryGetComponent(out Collider own)) _targetCollider = own;
            else _targetCollider = target.GetComponentInChildren<Collider>();

            return _targetCollider;
        }

        /// <summary>Places the spider at its new home and points it at a hunter (used by the spawner).</summary>
        public void Activate(Vector3 home, Transform hunter, bool aggro)
        {
            _home = home;
            _wanderPoint = home;
            _wanderTimer = 0f;
            _biteTimer = 0f;
            _castTimer = 0f;
            _windingUp = false;
            _state = SpiderState.Idle;
            _playingClip = null;

            if (spiderAnimation == null) spiderAnimation = GetComponentInChildren<Animation>();
            if (spiderAnimation != null) spiderAnimation.playAutomatically = false;

            // Start from the prefab's own fit, then let the feet solver find the ground again.
            _lift = 0f;
            _alignTimer = 0f;
            _solved = false;
            EnsureModel();
            if (tuneCollider) ApplyCollider();
            ApplyModelFit();

            target = hunter;
            _aggro = aggro;
        }

        void OnDrawGizmosSelected()
        {
            Vector3 p = transform.position;

            Gizmos.color = new Color(1f, 0.35f, 0.2f, 0.85f);
            Gizmos.DrawWireSphere(p, sightRange);

            Gizmos.color = new Color(0.9f, 0.6f, 0.1f, 0.5f);
            Gizmos.DrawWireSphere(p, biteRange);

            if (useMagic)
            {
                Gizmos.color = new Color(0.55f, 0.4f, 1f, 0.6f);
                Gizmos.DrawWireSphere(p, castMinRange);
                Gizmos.color = new Color(0.35f, 0.7f, 1f, 0.35f);
                Gizmos.DrawWireSphere(p, castMaxRange);
            }

            Vector3 home = Application.isPlaying ? _home : transform.position;
            Gizmos.color = new Color(0.2f, 1f, 0.5f, 0.35f);
            Gizmos.DrawWireSphere(home, wanderRadius);
        }
    }
}
