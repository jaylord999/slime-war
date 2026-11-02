using System.Collections.Generic;
using MysticMap.World;
using UnityEngine;

namespace MysticMap
{
    /// <summary>
    /// The Spider Matriarch: one very large, stationary spider that owns a stretch of the world.
    ///
    /// It is the spider answer to the <see cref="SlimeBoss"/> - the same idea, learnt from the
    /// fantasy spider instead of the slime:
    ///   * it never chases. It patrols a small patch around its home spot (up to
    ///     <see cref="patrolRadius"/>), and only turns on the spot to keep its face - and its fangs -
    ///     pointing at the player,
    ///   * it only fights inside its <see cref="range"/>: walk away and it loses interest and idles,
    ///   * it casts a small kit of spells - a venom bolt that flies at the player, a burst that
    ///     erupts under them, a web nova for when they hug it and a shockwave that tears up the
    ///     ground. Every spell goes through exactly the code the player's magic uses
    ///     (<see cref="MagicProjectile"/>, <see cref="Damage"/>, <see cref="MagicEffects"/>), and the
    ///     boss is the caster, so it never hurts itself or its own brood,
    ///   * it summons: magic circles bloom on the ground around it, charge for a moment, and then
    ///     ordinary (much smaller) <see cref="SpiderEnemy"/> spiders crawl out of them and hunt the
    ///     player, exactly like the ones the <see cref="SpiderDirector"/> streams in.
    ///
    /// Unlike the spider minions, the boss never chases: it holds its position, and only its
    /// rotation follows the player.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MysticMap/Spider Boss (stationary caster + summoner)")]
    public class SpiderBoss : MonoBehaviour
    {
        /// <summary>How a spell reaches its victim.</summary>
        public enum BossSpellKind
        {
            /// <summary>A burst on the ground under the player (the area is hurt on impact).</summary>
            GroundCast,
            /// <summary>A travelling venom slash / bolt fired at the player.</summary>
            Projectile,
            /// <summary>Everything around the boss is hit at once (only worth it up close).</summary>
            Nova,
            /// <summary>A ring of the world around the boss is torn up, with a small hit.</summary>
            Shockwave
        }

        /// <summary>One spell in the boss's kit.</summary>
        [System.Serializable]
        public class BossSpell
        {
            public string name = "Spell";
            public BossSpellKind kind = BossSpellKind.GroundCast;

            [Tooltip("Effect that is actually played (a Hovl pack prefab, e.g. 'AoE effects/AoE slash green').")]
            public GameObject effect;

            [Header("When it is used")]
            [Tooltip("Shortest distance (m) the player must be at for this spell to be picked.")]
            public float minRange;
            [Tooltip("Longest distance (m) the player may be at for this spell to be picked.")]
            public float maxRange = 26f;
            [Tooltip("Relative chance of being picked when several spells fit the distance.")]
            [Range(0f, 5f)] public float weight = 1f;

            [Header("Damage")]
            [Tooltip("Damage dealt by the spell (0 = a pure visual).")]
            public float damage = 22f;
            [Tooltip("Radius (m) of the area that is hurt.")]
            public float radius = 3.5f;
            [Tooltip("How much the spell disturbs the world around it (grass, rocks).")]
            [Range(0f, 2f)] public float environmentScale = 1f;
            [Tooltip("Knockback (m) applied to whatever is hit.")]
            public float knockback;

            [Header("Projectile only")]
            [Tooltip("Travelling speed (m/s) of the bolt.")]
            public float speed = 19f;
            [Tooltip("Make the bolt skim along the ground instead of flying straight.")]
            public bool hugGround = true;
            [Tooltip("Height (m) above the ground a skimming bolt flies at.")]
            public float hugHeight = 0.9f;
            [Tooltip("Fly on through several victims instead of stopping at the first.")]
            public bool pierce;

            [Header("Visuals")]
            [Tooltip("Scale of the played effect (the pack prefabs are built at very different sizes).")]
            public float effectScale = 1f;

            /// <summary>True when the player is close enough for this spell.</summary>
            public bool Fits(float distance) => distance >= minRange && distance <= maxRange;
        }

        [Header("Identity")]
        [Tooltip("Name shown in the boss bar.")]
        public string bossName = "Spider Matriarch";
        [Tooltip("How much bigger than a normal spider it is (the model is scaled by this).")]
        public float sizeMultiplier = 4f;
        [Tooltip("The spider mesh (auto-filled from the first renderer when empty).")]
        public Transform model;

        [Header("Who it fights")]
        [Tooltip("The player (auto-filled with the slime when empty).")]
        public Transform target;
        public bool autoFindPlayer = true;
        [Tooltip("How far (m) the boss notices and keeps hunting the player. Outside it, it ignores everyone.")]
        public float range = 45f;
        [Tooltip("What its spells may hit.")]
        public LayerMask hitMask = ~0;

        [Header("Casting")]
        [Tooltip("Seconds between two spells. Short - it is a boss.")]
        public float cooldown = 2f;
        [Tooltip("Random extra wait added to the cooldown, in seconds.")]
        public float cooldownJitter = 1f;
        [Tooltip("The spells it can use, with the distance each one is used from.")]
        public BossSpell[] spells = new BossSpell[0];

        [Header("Walking (it patrols, it never chases)")]
        [Tooltip("Let it walk around its home spot. Off = it stands perfectly still.")]
        public bool patrol = true;
        [Tooltip("How far (m) from its home spot it is allowed to wander.")]
        public float patrolRadius = 7f;
        [Tooltip("Speed (m/s) while it walks to the next spot.")]
        public float walkSpeed = 1.4f;
        [Tooltip("Seconds it rests at each spot before picking the next one.")]
        public float patrolPause = 4f;

        [Header("Idle motion (never chases, never walks away)")]
        [Tooltip("How fast (degrees/s) it turns on the spot to face the player.")]
        public float turnSpeed = 90f;
        [Tooltip("How much (m) it bobs up and down while it breathes (0 = dead still).")]
        public float bobAmount = 0.06f;
        [Tooltip("Breathing speed (cycles per second).")]
        public float bobSpeed = 0.55f;
        [Tooltip("Degrees the body sways while it waits (a little life, no walking).")]
        public float legTwitch = 1.5f;

        [Header("Feet on the ground")]
        [Tooltip("Stand the spider's lowest leg on the real ground every frame, so it never floats " +
                 "(the pack's animated pose hangs a long way below the body).")]
        public bool alignFeet = true;
        [Tooltip("Extra gap (m) kept between the lowest leg and the ground. Small positive = just touching.")]
        [Range(-0.5f, 0.5f)] public float footGap;

        [Header("Summoning")]
        [Tooltip("Spider prefabs it can call (empty = the spider director's / the generated prefab).")]
        public GameObject[] minionPrefabs = new GameObject[0];
        [Tooltip("Start the summoning as soon as the fight begins.")]
        public bool summonOnStart = true;
        [Tooltip("Seconds between two summoning rituals.")]
        public float summonCooldown = 14f;
        [Tooltip("Seconds between two rituals while its health is low (it gets desperate).")]
        public float desperateCooldown = 8f;
        [Tooltip("Below this fraction of health the desperate cooldown is used.")]
        [Range(0f, 1f)] public float desperateBelow = 0.35f;
        [Tooltip("How many spiders one ritual calls.")]
        public int summonCount = 3;
        [Tooltip("Farthest circle (m) from the boss where a spider may appear.")]
        public float summonRadius = 10f;
        [Tooltip("Closest circle (m) from the boss.")]
        public float summonMinRadius = 4.5f;
        [Tooltip("Seconds the magic circles charge before the spiders appear.")]
        public float summonDelay = 1.6f;
        [Tooltip("Circle drawn under every summon (empty = a ring of light is built at runtime).")]
        public GameObject circleEffect;
        [Tooltip("One-shot played where a spider pops out.")]
        public GameObject summonBurst;
        [Tooltip("Largest number of its own summoned spiders alive at once.")]
        public int maxMinions = 8;
        [Tooltip("Summoned spiders may not appear closer than this (m) to the player.")]
        public float minionPlayerDistance = 3f;
        [Tooltip("Size of the summoned spiders compared to a normal spider (1 = exactly the same).")]
        [Range(0.15f, 1.5f)] public float minionScale = 0.55f;

        [Header("Health")]
        [Tooltip("Hit points of the boss.")]
        public float maxHealth = 900f;
        [Tooltip("Show a boss bar at the top of the screen.")]
        public bool showHealthBar = true;
        [Tooltip("Seconds the fight stays quiet after the boss dies.")]
        public float deathDelay = 2f;

        [Header("Effects")]
        [Tooltip("Played where a spell lands.")]
        public GameObject impactEffect;
        [Tooltip("Played when the boss appears.")]
        public GameObject spawnEffect;

        Health _health;
        readonly List<SpiderEnemy> _minions = new List<SpiderEnemy>();
        readonly List<SummonCircle> _circles = new List<SummonCircle>();

        Vector3 _home;
        Vector3 _patrolPoint;
        float _patrolTimer;
        Transform _rig;
        float _yaw;
        float _cooldown;
        float _summonTimer;
        float _stun;
        float _time;
        bool _engaged;
        bool _dying;

        Vector3 _restScale = Vector3.one;
        Vector3 _restPosition = Vector3.zero;
        Quaternion _restRotation = Quaternion.identity;
        float _bob;

        Renderer[] _renderers;
        Mesh _cachedMesh;
        Vector3[] _meshPositions;
        BoneWeight[] _meshBoneWeights;

        ChunkManager _chunks;
        Texture2D _pixel;

        /// <summary>A magic circle waiting for the spider it is about to bring in.</summary>
        class SummonCircle
        {
            public GameObject circle;
            public GameObject ring;
            public Vector3 position;
            public float timer;
            public float diameter;
        }

        /// <summary>True while the player is in range and the boss is alive.</summary>
        public bool IsEngaged => _engaged;

        /// <summary>The boss's hit points (null before Awake).</summary>
        public Health Health => _health;

        // =====================================================================
        //  Lifecycle
        // =====================================================================
        void Awake()
        {
            if (model == null) model = FindModel();
            if (target == null) ResolveTarget();

            _home = transform.position;
            _yaw = transform.eulerAngles.y;
            _patrolPoint = _home;
            _patrolTimer = Mathf.Max(0f, patrolPause) * 0.5f;

            FitModel();
            SetupHealth();

            if (spells == null || spells.Length == 0) spells = DefaultKit();

            // The summoning circles need a visual; if none was set up, use the magic effect that
            // ships in Resources (the same one the player's charging circles come from).
            if (circleEffect == null) circleEffect = Resources.Load<GameObject>("ThirdPersonMagicFX/MagicCircle");

            _cooldown = 1.5f;
            _summonTimer = summonOnStart ? 2f : summonCooldown;
        }

        void Start()
        {
            if (spawnEffect != null)
                MagicEffects.DestroyLater(MagicEffects.Spawn(spawnEffect, transform.position, Quaternion.identity));
        }

        void OnDestroy()
        {
            if (_health == null) return;
            _health.Damaged -= OnDamaged;
            _health.Died -= OnDied;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            _time += dt;

            if (_dying) { AnimateModel(); return; }

            if (target == null) { ResolveTarget(); if (target == null) return; }

            float distance = FlatDistance(transform.position, target.position);
            _engaged = distance <= Mathf.Max(1f, range);

            FacePlayer(dt);
            Patrol(dt);

            if (_stun > 0f) { _stun -= dt; AnimateModel(); return; }

            if (_engaged) Cast(dt, distance);
            Summon(dt);

            AnimateModel();
        }

        // =====================================================================
        //  Facing + patrolling (it walks its own ground, never after the player)
        // =====================================================================
        /// <summary>Slowly turns on the spot so the head keeps pointing at the player.</summary>
        void FacePlayer(float dt)
        {
            if (target == null) return;

            Vector3 delta = Flat(target.position - transform.position);
            if (delta.sqrMagnitude < 0.0001f) return;

            float want = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
            _yaw = Mathf.MoveTowardsAngle(_yaw, want, Mathf.Max(0f, turnSpeed) * dt);
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
        }

        /// <summary>
        /// Walks a slow circuit around its home spot: it picks a point inside
        /// <see cref="patrolRadius"/>, walks there at <see cref="walkSpeed"/>, rests, then picks the
        /// next one. It never follows the player - only its turning does that - so it stays the
        /// anchor of the fight while its brood does the chasing.
        /// </summary>
        void Patrol(float dt)
        {
            if (!patrol || patrolRadius < 0.5f) return;

            Vector3 here = transform.position;

            float toPoint = FlatDistance(here, _patrolPoint);
            Vector3 toHome = _home - here;
            toHome.y = 0f;

            // Never let it stroll out of its own range.
            if (toHome.magnitude > Mathf.Max(1f, patrolRadius))
            {
                _patrolPoint = _home;
                toPoint = FlatDistance(here, _patrolPoint);
            }

            if (toPoint <= 0.35f)
            {
                _patrolTimer -= dt;
                if (_patrolTimer > 0f) return;

                Vector2 dir = Random.insideUnitCircle;
                if (dir.sqrMagnitude < 0.0001f) dir = Vector2.right;
                dir.Normalize();

                float r = Random.Range(patrolRadius * 0.35f, Mathf.Max(0.5f, patrolRadius));
                _patrolPoint = new Vector3(_home.x + dir.x * r, 0f, _home.z + dir.y * r);
                _patrolTimer = Mathf.Max(0f, patrolPause);
                return;
            }

            Vector3 step = _patrolPoint - here;
            step.y = 0f;

            Vector3 move = step.normalized * (Mathf.Max(0.05f, walkSpeed) * dt);
            if (move.magnitude > step.magnitude) move = step;

            Vector3 next = here + move;
            next.y = GroundHeight(next.x, next.z);   // follow the terrain while it walks

            transform.position = Vector3.Lerp(here, next, 1f - Mathf.Exp(-12f * dt));
        }

        /// <summary>
        /// A slow breath on the model (it is alive, not a statue) plus a little sway. The real model
        /// sits inside a rig that the foot solver moves, so this only ever nudges that rig.
        /// </summary>
        void AnimateModel()
        {
            if (model == null || _rig == null) return;

            float breath = Mathf.Sin(_time * Mathf.Max(0.05f, bobSpeed) * Mathf.PI * 2f);

            Vector3 scale = _restScale;
            scale.x = _restScale.x * (1f + breath * 0.012f);
            scale.z = _restScale.z * (1f + breath * 0.012f);
            scale.y = _restScale.y * (1f - breath * 0.018f);
            model.localScale = scale;

            float bob = breath * Mathf.Max(0f, bobAmount);
            float sway = Mathf.Sin(_time * 0.7f) * Mathf.Max(0f, legTwitch);

            _rig.localRotation = _restRotation * Quaternion.Euler(0f, sway, 0f);
            _rig.localPosition += Vector3.up * (bob - _bob);   // only the change in breath, never an absolute height
            _bob = bob;
        }

        void LateUpdate()
        {
            AlignFeet();
        }

        /// <summary>
        /// Puts the lowest leg on the real ground every frame.
        ///
        /// The pack's spider hangs its claws a long way below the body in its animated pose, and a
        /// SkinnedMeshRenderer's bounds cover the whole animation (they are also only refreshed once
        /// per frame), so measuring the bounds leaves the spider hovering. This measures the skinned
        /// vertices themselves - the same maths the GPU does on the bones - and slides the rig down
        /// (or up) until they touch the ground.
        /// </summary>
        void AlignFeet()
        {
            if (!alignFeet || _rig == null) return;

            float lowest = LowestVertexY();
            if (float.IsNaN(lowest)) return;

            float ground = GroundHeight(transform.position.x, transform.position.z);
            float gap = ground + footGap - lowest;

            if (Mathf.Abs(gap) < 0.005f) return;

            Vector3 p = _rig.position;
            _rig.position = new Vector3(p.x, p.y + gap, p.z);
        }

        /// <summary>The world height of the lowest vertex the model really draws right now.</summary>
        float LowestVertexY()
        {
            if (_rig == null) return float.NaN;

            if (_renderers == null || _renderers.Length == 0) _renderers = _rig.GetComponentsInChildren<Renderer>(true);
            if (_renderers == null || _renderers.Length == 0) return float.NaN;

            bool any = false;
            float lowest = float.MaxValue;

            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer r = _renderers[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;

                float min;
                if (r is SkinnedMeshRenderer skinned && LowestSkinnedY(skinned, out min)) { /* measured */ }
                else min = r.bounds.min.y;

                if (!any || min < lowest) { lowest = min; any = true; }
            }

            return any ? lowest : float.NaN;
        }

        /// <summary>
        /// The lowest animated vertex of a skinned mesh: the mesh's own vertices deformed by their
        /// current bones (the same maths the skin shader does).
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

            Matrix4x4[] bindposes = mesh.bindposes;
            if (bindposes == null || bindposes.Length < bones.Length) return false;

            var localToWorld = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                localToWorld[i] = bones[i] != null ? bones[i].localToWorldMatrix : skinned.transform.localToWorldMatrix;

            Vector3[] positions = _meshPositions;
            BoneWeight[] weights = _meshBoneWeights;

            lowest = float.MaxValue;
            bool any = false;

            for (int v = 0; v < positions.Length; v++)
            {
                BoneWeight w = weights[v];
                if (w.weight0 + w.weight1 + w.weight2 + w.weight3 <= 0f) continue;

                Vector4 p = positions[v];

                Vector3 skinned3 = Blend(localToWorld, bindposes, w.weight0, w.boneIndex0, p)
                                 + Blend(localToWorld, bindposes, w.weight1, w.boneIndex1, p)
                                 + Blend(localToWorld, bindposes, w.weight2, w.boneIndex2, p)
                                 + Blend(localToWorld, bindposes, w.weight3, w.boneIndex3, p);

                if (!any || skinned3.y < lowest) { lowest = skinned3.y; any = true; }
            }

            return any;
        }

        /// <summary>One bone's contribution to a skinned vertex position.</summary>
        static Vector3 Blend(Matrix4x4[] bones, Matrix4x4[] bindposes, float weight, int bone, Vector4 vertex)
        {
            if (weight <= 0f || bone < 0 || bone >= bones.Length) return Vector3.zero;

            Vector4 p = bones[bone] * (bindposes[bone] * vertex);
            return new Vector3(p.x, p.y, p.z) * weight;
        }

        // =====================================================================
        //  Casting
        // =====================================================================
        void Cast(float dt, float distance)
        {
            // It keeps facing the player the whole time; only the spell waits for the cooldown.
            _cooldown -= dt;
            if (_cooldown > 0f) return;

            BossSpell spell = PickSpell(distance);
            if (spell == null) return;

            _cooldown = Mathf.Max(0.4f, cooldown) + Random.Range(0f, Mathf.Max(0f, cooldownJitter));

            switch (spell.kind)
            {
                case BossSpellKind.Projectile: CastProjectile(spell); break;
                case BossSpellKind.Nova: CastNova(spell); break;
                case BossSpellKind.Shockwave: CastShockwave(spell); break;
                default: CastOnGround(spell); break;
            }
        }

        /// <summary>Picks a spell that fits the current distance, weighted so it is not repetitive.</summary>
        BossSpell PickSpell(float distance)
        {
            float total = 0f;
            foreach (BossSpell s in spells)
                if (s != null && s.Fits(distance) && s.weight > 0f) total += s.weight;

            if (total <= 0f)
            {
                // Nothing fits: use the closest one rather than going quiet.
                BossSpell fallback = null;
                float best = float.MaxValue;

                foreach (BossSpell s in spells)
                {
                    if (s == null) continue;

                    float gap = distance < s.minRange ? s.minRange - distance
                              : distance > s.maxRange ? distance - s.maxRange
                              : 0f;

                    if (gap < best) { best = gap; fallback = s; }
                }

                return fallback;
            }

            float roll = Random.value * total;
            foreach (BossSpell s in spells)
            {
                if (s == null || !s.Fits(distance) || s.weight <= 0f) continue;

                roll -= s.weight;
                if (roll <= 0f) return s;
            }

            return null;
        }

        /// <summary>A burst that erupts on the ground under the player.</summary>
        void CastOnGround(BossSpell spell)
        {
            if (target == null) return;

            Vector3 point = target.position;
            point = new Vector3(point.x, GroundHeight(point.x, point.z), point.z);

            PlayOnce(spell.effect, point, spell.effectScale);
            DamageArea(point, spell);
            _stun = 0.25f;
        }

        /// <summary>A venom slash / bolt that travels to the player.</summary>
        void CastProjectile(BossSpell spell)
        {
            if (spell.effect == null || target == null) return;

            Vector3 origin = Muzzle();
            Vector3 direction = Flat(target.position + Vector3.up * 0.8f - origin);
            if (direction.sqrMagnitude < 0.0001f) direction = Flat(transform.forward);

            var level = new MagicSpellLevel
            {
                castEffect = spell.effect,
                impactEffect = impactEffect,
                delivery = SpellDelivery.Projectile,
                damage = spell.damage,
                radius = Mathf.Max(0.1f, spell.radius),
                speed = Mathf.Max(1f, spell.speed),
                hugGround = spell.hugGround,
                hugHeight = Mathf.Max(0f, spell.hugHeight),
                pierce = spell.pierce,
                splash = 0.5f,
                splashRadius = Mathf.Max(0.2f, spell.radius * 0.8f),
                castScale = Mathf.Max(0.01f, spell.effectScale),
                environmentScale = Mathf.Max(0f, spell.environmentScale),
                knockback = Mathf.Max(0f, spell.knockback)
            };

            var spec = new MagicCastSpec
            {
                damage = Mathf.Max(0f, spell.damage),
                power = 0.6f,
                environmentScale = Mathf.Max(0f, spell.environmentScale),
                direction = direction.normalized,
                debrisPerHit = 0
            };

            // The boss is the owner, so the projectile never hurts the caster.
            MagicProjectile.Launch(spell.effect, origin, direction, level, spec, gameObject, hitMask);
            _stun = 0.2f;
        }

        /// <summary>Everything around the boss is hit at once.</summary>
        void CastNova(BossSpell spell)
        {
            Vector3 center = transform.position + Vector3.up * 0.5f;

            PlayOnce(spell.effect, center, spell.effectScale);
            DamageArea(center, spell);
            ShakePlayer(0.35f);
            _stun = 0.4f;
        }

        /// <summary>A ring of the world around the boss is torn up, with a small hit on top.</summary>
        void CastShockwave(BossSpell spell)
        {
            Vector3 center = transform.position;

            MagicEnvironment.Impact(center, Mathf.Max(1f, spell.radius), 0.6f * Mathf.Max(0f, spell.environmentScale));
            PlayOnce(spell.effect, center, spell.effectScale);

            DamageArea(center, spell);
            ShakePlayer(0.25f);
            _stun = 0.35f;
        }

        /// <summary>Hurts everything with hit points inside the spell's radius (never the boss's own brood).</summary>
        void DamageArea(Vector3 point, BossSpell spell)
        {
            if (spell.damage <= 0f) return;

            Vector3 away = target != null ? Flat(target.position - point) : Flat(transform.forward);

            var info = new DamageInfo(Mathf.Max(0f, spell.damage), point, away,
                                      Mathf.Max(0f, spell.knockback), gameObject);

            var points = new List<Vector3>(8);

            // Nova / shockwave: it is an area around the caster, so the caster's own spiders are
            // spared (otherwise a 10 m shockwave would wipe out everything it just summoned).
            bool isArea = spell.kind == BossSpellKind.Nova || spell.kind == BossSpellKind.Shockwave;

            if (isArea) Damage.ApplyRadial(point, Mathf.Max(0.1f, spell.radius), info, transform, hitMask, points, MinionObjects());
            else Damage.ApplyRadial(point, Mathf.Max(0.1f, spell.radius), info, transform, hitMask, points);

            // A single impact spark where the blast went off (not one per victim).
            if (points.Count > 0) PlayOnce(impactEffect, point + Vector3.up * 0.6f, 1f);
        }

        /// <summary>Plays a one-shot effect at a point and cleans it up when it is done.</summary>
        static void PlayOnce(GameObject prefab, Vector3 point, float scale)
        {
            if (prefab == null) return;

            GameObject fx = MagicEffects.Spawn(prefab, point, Quaternion.identity, null, Mathf.Max(0.01f, scale));
            MagicEffects.ForcePlay(fx, false);
            MagicEffects.DestroyLater(fx);
        }

        /// <summary>Where its magic comes out: in front of the boss, a little above its middle.</summary>
        Vector3 Muzzle()
        {
            float k = Mathf.Max(0.2f, sizeMultiplier);
            return transform.position + Vector3.up * (0.45f * k) + Flat(transform.forward) * (0.35f * k);
        }

        void ShakePlayer(float amount)
        {
            if (target == null || amount <= 0f) return;

            var player = target.GetComponentInParent<SlimePlayer>();
            if (player != null) player.ShakeCamera(amount, 0.2f);
        }

        /// <summary>The stock kit, used when no spells were set up in the inspector.</summary>
        static BossSpell[] DefaultKit()
        {
            return new[]
            {
                new BossSpell
                {
                    name = "Ground burst", kind = BossSpellKind.GroundCast,
                    damage = 18f, radius = 3.4f, minRange = 0f, maxRange = 40f, weight = 1f
                },
                new BossSpell
                {
                    name = "Venom bolt", kind = BossSpellKind.Projectile,
                    damage = 14f, radius = 1.4f, speed = 18f, minRange = 6f, maxRange = 34f, weight = 1.2f
                },
                new BossSpell
                {
                    name = "Web nova", kind = BossSpellKind.Nova,
                    damage = 20f, radius = 6f, minRange = 0f, maxRange = 8f, weight = 1.5f, knockback = 4f
                },
                new BossSpell
                {
                    name = "Shockwave", kind = BossSpellKind.Shockwave,
                    damage = 8f, radius = 9f, minRange = 0f, maxRange = 30f, weight = 0.6f
                }
            };
        }

        // =====================================================================
        //  Summoning (magic circles first, spiders second)
        // =====================================================================
        void Summon(float dt)
        {
            // Circles already on the ground keep charging, whether or not the fight is still on.
            for (int i = _circles.Count - 1; i >= 0; i--)
            {
                SummonCircle c = _circles[i];
                c.timer -= dt;

                float t = Mathf.Clamp01(1f - c.timer / Mathf.Max(0.1f, summonDelay));

                // The circle spins faster and breathes as the spider is about to appear. Only the
                // effect inside the flat parent is touched, so it stays lying on the ground.
                Transform fx = c.circle != null && c.circle.transform.childCount > 0 ? c.circle.transform.GetChild(0) : null;
                if (fx != null)
                {
                    c.circle.transform.Rotate(Vector3.up, (40f + 120f * t) * dt, Space.World);
                    fx.localScale = Vector3.one * (1f + Mathf.Sin(Time.time * 9f) * 0.06f * t);
                }

                if (c.ring != null)
                {
                    float s = Mathf.Lerp(0.35f, 1f, t);
                    c.ring.transform.localScale = new Vector3(c.diameter * s, 0.06f, c.diameter * s);
                }

                if (c.timer > 0f) continue;

                // Time is up: the spider appears where the circle is.
                SpawnMinionAt(c.position);
                if (c.circle != null) MagicEffects.DestroyLater(c.circle, 1.2f);
                if (c.ring != null) Destroy(c.ring, 1.2f);
                _circles.RemoveAt(i);
            }

            if (_dying || !_engaged) return;

            _summonTimer -= dt;
            if (_summonTimer > 0f) return;

            if (summonCount <= 0 || minionPrefabs == null || minionPrefabs.Length == 0)
            {
                _summonTimer = Mathf.Max(1f, summonCooldown);
                return;
            }

            if (AliveMinions() >= Mathf.Max(1, maxMinions)) { _summonTimer = 2f; return; }

            BeginSummon();
        }

        /// <summary>Starts a ritual: one magic circle per spider, all on the ground around the boss.</summary>
        void BeginSummon()
        {
            float low = _health != null ? _health.Normalized : 1f;
            _summonTimer = low <= desperateBelow ? Mathf.Max(2f, desperateCooldown)
                                                 : Mathf.Max(2f, summonCooldown);

            int room = Mathf.Max(0, maxMinions - AliveMinions());
            int count = Mathf.Clamp(Mathf.Min(summonCount, room), 0, 12);
            if (count <= 0) return;

            List<Vector3> points = FindSummonSpots(count);

            for (int i = 0; i < points.Count; i++)
            {
                var c = new SummonCircle
                {
                    position = points[i],
                    timer = Mathf.Max(0.3f, summonDelay) + Random.Range(0f, 0.45f),
                    diameter = Random.Range(1.6f, 2.2f)
                };

                // The magic circle itself (Hovl pack / Resources effect), laid FLAT on the ground.
                // The spawned object is a plain parent ("Summon Circle") that holds the ground
                // orientation, and the effect is put inside it at its own scale - that way the
                // moving / spinning below can never tip it back up on edge.
                c.circle = SpawnOnGround(circleEffect, points[i], c.diameter);
                if (c.circle != null) MagicEffects.DestroyLater(c.circle, summonDelay + 3f);   // safety net

                // A ring of light under it, so there is always something to read on the ground.
                c.ring = CreateLightRing(points[i], c.diameter);

                _circles.Add(c);
            }
        }

        /// <summary>Somewhere near the boss where a spider may come out (never on top of the player).</summary>
        List<Vector3> FindSummonSpots(int count)
        {
            var spots = new List<Vector3>(count);
            Vector3 player = target != null ? target.position : transform.position;
            float keepAway = Mathf.Max(0f, minionPlayerDistance);
            float outer = Mathf.Max(summonMinRadius, summonRadius);

            for (int i = 0; i < count; i++)
            {
                Vector3 best = Vector3.zero;
                bool inTheOpen = false;

                for (int attempt = 0; attempt < 12; attempt++)
                {
                    Vector2 dir = Random.insideUnitCircle;
                    if (dir.sqrMagnitude < 0.0001f) dir = Vector2.right;
                    dir.Normalize();

                    float r = Random.Range(summonMinRadius, outer);
                    float x = _home.x + dir.x * r;
                    float z = _home.z + dir.y * r;

                    if (FlatDistance(new Vector3(x, 0f, z), player) < keepAway) continue;

                    best = new Vector3(x, GroundHeight(x, z), z);

                    if (!TownBounds.ContainsPadded(x, z)) { inTheOpen = true; break; }   // prefer open ground
                }

                if (!inTheOpen)
                {
                    // Fall back to an even ring around the boss so the ritual still looks tidy.
                    float angle = (i / (float)Mathf.Max(1, count)) * Mathf.PI * 2f;
                    float r = outer * 0.8f;
                    float x = _home.x + Mathf.Cos(angle) * r;
                    float z = _home.z + Mathf.Sin(angle) * r;
                    best = new Vector3(x, GroundHeight(x, z), z);
                }

                spots.Add(best);
            }

            return spots;
        }

        /// <summary>One of the boss's spiders crawls out of a finished circle - and hunts the player.</summary>
        void SpawnMinionAt(Vector3 position)
        {
            GameObject prefab = PickMinionPrefab();
            if (prefab == null) return;

            GameObject instance = Instantiate(prefab, position, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f), transform.parent);
            instance.name = prefab.name + " (summoned)";

            // The summoned brood is never big: a fraction of a normal spider, never the boss's size.
            float scale = Mathf.Clamp(minionScale, 0.15f, 1.5f);
            if (!Mathf.Approximately(scale, 1f)) instance.transform.localScale *= scale;

            var enemy = instance.GetComponent<SpiderEnemy>();
            if (enemy == null) enemy = instance.AddComponent<SpiderEnemy>();

            // The same call the director makes: it starts from its own fit and goes straight for the player.
            enemy.Activate(position, target, aggro: true);

            PruneMinions();
            _minions.Add(enemy);

            if (summonBurst != null)
                MagicEffects.DestroyLater(MagicEffects.Spawn(summonBurst, position, Quaternion.identity));
        }

        /// <summary>A usable spider prefab, from the inspector list or from the rest of the game.</summary>
        GameObject PickMinionPrefab()
        {
            if (minionPrefabs != null && minionPrefabs.Length > 0)
            {
                for (int i = 0; i < minionPrefabs.Length; i++)
                {
                    GameObject pick = minionPrefabs[Random.Range(0, minionPrefabs.Length)];
                    if (pick != null && pick.GetComponentInChildren<SpiderEnemy>(true) != null) return pick;
                }
            }

            // Nothing usable in the list: borrow whatever the director (or the scene) is already using.
            foreach (SpiderDirector director in FindObjectsByType<SpiderDirector>(FindObjectsInactive.Include))
            {
                if (director == null || director.spiders == null) continue;

                for (int i = 0; i < director.spiders.Length; i++)
                {
                    GameObject pick = director.spiders[i];
                    if (pick != null && pick.GetComponentInChildren<SpiderEnemy>(true) != null) return pick;
                }
            }

            return null;
        }

        int AliveMinions()
        {
            PruneMinions();
            return _minions.Count;
        }

        void PruneMinions()
        {
            for (int i = _minions.Count - 1; i >= 0; i--)
                if (_minions[i] == null) _minions.RemoveAt(i);
        }

        /// <summary>The brood as plain objects, so a nova never wipes out what the boss just summoned.</summary>
        List<GameObject> MinionObjects()
        {
            PruneMinions();

            var list = new List<GameObject>(_minions.Count);
            for (int i = 0; i < _minions.Count; i++)
                if (_minions[i] != null) list.Add(_minions[i].gameObject);

            return list;
        }

        /// <summary>
        /// Lays an effect flat on the ground - the same way the slime's circles lie.
        ///
        /// The pack's circles are vertical planes, so the effect is turned 90 degrees about X and put
        /// inside a plain "Summon Circle" parent. The parent holds the ground orientation and is the
        /// only thing ever turned or scaled afterwards, so the circle can never tip up on edge.
        /// </summary>
        static GameObject SpawnOnGround(GameObject prefab, Vector3 point, float diameter)
        {
            if (prefab == null) return null;

            GameObject fx = MagicEffects.Spawn(prefab, point, Quaternion.Euler(90f, 0f, 0f), null, 1f);
            if (fx == null) return null;

            if (fx.GetComponentsInChildren<Renderer>(true).Length == 0)
            {
                Destroy(fx);
                return null;
            }

            // An even, flat footprint for the circle (measured from the effect's own size).
            float footprint = Mathf.Max(0.5f, MagicEffects.Footprint(fx));
            float scale = Mathf.Clamp(Mathf.Max(0.5f, diameter) / footprint, 0.05f, 40f);

            var flat = new GameObject("Summon Circle");
            flat.transform.position = point;
            flat.transform.rotation = Quaternion.identity;
            flat.transform.localScale = Vector3.one * scale;

            fx.transform.SetParent(flat.transform, true);

            MagicEffects.ForcePlay(flat, true);

            return flat;
        }

        /// <summary>A thin ring of light - the always-there marker under a summoning circle.</summary>
        GameObject CreateLightRing(Vector3 point, float diameter)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Summon Ring";

            Collider collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            go.transform.position = point + Vector3.up * 0.02f;
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = new Vector3(diameter, 0.06f, diameter);

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                Color glow = new Color(0.55f, 0.92f, 0.5f, 1f);   // venom green

                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");

                if (shader != null)
                {
                    var unlit = new Material(shader) { color = glow };
                    if (unlit.HasProperty("_BaseColor")) unlit.SetColor("_BaseColor", glow);
                    renderer.material = unlit;
                }

                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            return go;
        }

        // =====================================================================
        //  Model
        // =====================================================================
        Transform FindModel()
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child == null || child.GetComponent<Camera>() != null) continue;
                if (child.GetComponentInChildren<Renderer>(true) != null) return child;
            }
            return GetComponentInChildren<Renderer>(true) != null ? transform : null;
        }

        /// <summary>
        /// Remembers the rest pose and blows the spider model up to the boss size.
        ///
        /// The real model is put inside a child "Model (feet rig)" object: the rig carries the
        /// breathing bob and the foot solver, while the model keeps exactly the offset the prefab
        /// (and the inspector) gave it. That way nothing fights over the model's own position.
        /// </summary>
        void FitModel()
        {
            if (model == null) return;

            if (model.parent == null || model.parent == transform)
            {
                var rigRoot = new GameObject("Model (feet rig)").transform;
                rigRoot.SetParent(transform, false);
                rigRoot.localPosition = model.parent != null ? model.localPosition : Vector3.zero;
                rigRoot.localRotation = model.parent != null ? model.localRotation : Quaternion.identity;
                rigRoot.localScale = Vector3.one;

                model.SetParent(rigRoot, false);
                model.localPosition = Vector3.zero;

                _rig = rigRoot;
            }
            else
            {
                _rig = model.parent;                 // the prefab already ships with the rig
            }

            model.localScale = model.localScale * Mathf.Max(0.1f, sizeMultiplier);

            _restRotation = _rig.localRotation;
            _restPosition = _rig.localPosition;
            _restScale = model.localScale;
        }

        // =====================================================================
        //  Health
        // =====================================================================
        void SetupHealth()
        {
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

            _health = GetComponent<Health>();
            if (_health == null) _health = gameObject.AddComponent<Health>();

            _health.maxHealth = Mathf.Max(1f, maxHealth);
            _health.destroyOnDeath = true;
            _health.destroyDelay = Mathf.Max(0.5f, deathDelay);
            _health.stirEnvironment = true;
            _health.environmentRadius = Mathf.Max(1.5f, sizeMultiplier);
            _health.flashColor = new Color(1f, 0.6f, 0.6f, 1f);
            _health.flashTime = 0.15f;

            _health.Damaged += OnDamaged;
            _health.Died += OnDied;
        }

        /// <summary>A hit shakes the camera and briefly interrupts the casting (never its place).</summary>
        void OnDamaged(DamageInfo info)
        {
            ShakePlayer(0.12f);
            _stun = Mathf.Max(_stun, 0.12f);
        }

        /// <summary>When it dies the circles fizzle out and the fight stops.</summary>
        void OnDied()
        {
            _dying = true;
            _stun = 0f;

            for (int i = 0; i < _circles.Count; i++)
            {
                if (_circles[i].circle != null) MagicEffects.DestroyLater(_circles[i].circle, 0.6f);
                if (_circles[i].ring != null) Destroy(_circles[i].ring, 0.6f);
            }
            _circles.Clear();
        }

        // =====================================================================
        //  World queries
        // =====================================================================
        float GroundHeight(float x, float z)
        {
            if (_chunks == null) _chunks = FindFirstObjectByType<ChunkManager>();
            if (_chunks != null) return _chunks.SampleHeight(x, z);

            return transform.position.y;
        }

        void ResolveTarget()
        {
            if (!autoFindPlayer) return;

#if UNITY_2023_1_OR_NEWER
            var player = Object.FindFirstObjectByType<SlimePlayer>(FindObjectsInactive.Exclude);
#else
            var player = Object.FindObjectOfType<SlimePlayer>();
#endif
            if (player != null) target = player.transform;
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.zero;
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // =====================================================================
        //  Boss bar (plain IMGUI, like the slime boss's)
        // =====================================================================
        void OnGUI()
        {
            if (!showHealthBar || _health == null || target == null) return;
            if (!_engaged && _health.Normalized >= 0.999f) return;    // quiet until the fight starts

            const float width = 520f;
            const float height = 22f;
            float x = (Screen.width - width) * 0.5f;
            float y = 28f;

            float value = _dying ? 0f : _health.Normalized;

            Color old = GUI.color;

            GUI.color = new Color(0.03f, 0.05f, 0.03f, 0.7f);
            GUI.DrawTexture(new Rect(x - 3f, y - 3f, width + 6f, height + 6f), Pixel());

            GUI.color = new Color(0.45f, 0.85f, 0.3f, 0.95f);
            GUI.DrawTexture(new Rect(x, y, width * value, height), Pixel());

            GUI.color = new Color(1f, 1f, 1f, 0.85f);
            GUI.Label(new Rect(x, y - 22f, width, 20f), bossName);

            GUI.color = old;
        }

        Texture2D Pixel()
        {
            if (_pixel != null) return _pixel;

            _pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _pixel.SetPixel(0, 0, Color.white);
            _pixel.Apply();
            _pixel.name = "SpiderBossPixel";
            _pixel.hideFlags = HideFlags.HideAndDontSave;
            return _pixel;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.5f, 1f, 0.4f, 0.25f);
            Gizmos.DrawWireSphere(transform.position, range);

            Gizmos.color = new Color(0.3f, 0.9f, 0.3f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, summonRadius);
        }
    }
}
