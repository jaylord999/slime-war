using System.Collections.Generic;
using MysticMap.World;
using UnityEngine;

namespace MysticMap
{
    /// <summary>
    /// The Slime King: one very large, stationary slime that owns a stretch of the world.
    ///
    /// It never walks anywhere. It hops on the spot, shuffles a little side to side, and keeps its
    /// face on the player. What it does is cast: it carries a small spell kit (a ground explosion,
    /// a travelling slash, a nova, a rock burst and a summoning ritual), picks one that fits the
    /// distance it is at, and throws it at the player on a short cooldown while the player is
    /// inside its range.
    ///
    /// Every spell goes through the same code the player's magic uses
    /// (<see cref="MagicProjectile"/>, <see cref="Damage"/>, <see cref="MagicEffects"/>), so the
    /// Hovl Studio pack effects and the project's damage plumbing work exactly as they do for the
    /// slime - only the caster is the boss, so it never hurts itself or its own minions.
    ///
    /// The summoning is the show piece: magic circles bloom on the ground where the minions are
    /// about to appear (drawn from the Hovl pack / Resources effects, with a ring of light
    /// underneath as a fallback), they charge for a moment, and then the slimes pop out of them.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MysticMap/Slime Boss (stationary caster + summoner)")]
    public class SlimeBoss : MonoBehaviour
    {
        /// <summary>How a spell reaches its victim.</summary>
        public enum BossSpellKind
        {
            /// <summary>A burst on the ground under the player (the area is hurt on impact).</summary>
            GroundCast,
            /// <summary>A travelling slash / bolt fired at the player.</summary>
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

            [Tooltip("Effect that is actually played (a Hovl pack prefab, e.g. 'AoE effects/Meteors AOE').")]
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
            [Tooltip("Travel speed (m/s).")]
            public float speed = 20f;
            [Tooltip("Fly at a fixed height above the ground (ground-skimming slashes).")]
            public bool hugGround = true;
            public float hugHeight = 0.9f;
            [Tooltip("Carry on through victims instead of stopping at the first one.")]
            public bool pierce;

            [Tooltip("Scale of the played effect (the pack prefabs are built at very different sizes).")]
            public float effectScale = 1f;

            /// <summary>True when the player is close enough for this spell.</summary>
            public bool Fits(float distance) => distance >= minRange && distance <= maxRange;
        }

        [Header("Identity")]
        [Tooltip("Name shown in the boss bar.")]
        public string bossName = "Slime King";
        [Tooltip("How much bigger than a normal slime it is (the model is scaled by this).")]
        public float sizeMultiplier = 4f;
        [Tooltip("The slime mesh (auto-filled from the first renderer when empty).")]
        public Transform model;

        [Header("Who it fights")]
        [Tooltip("The player (auto-filled with the slime when empty).")]
        public Transform target;
        public bool autoFindPlayer = true;
        [Tooltip("How far (m) the boss notices and keeps hunting the player.")]
        public float range = 34f;
        [Tooltip("What its spells may hit.")]
        public LayerMask hitMask = ~0;

        [Header("Casting")]
        [Tooltip("Seconds between two spells. Short - it is a boss.")]
        public float cooldown = 1.6f;
        [Tooltip("Random extra wait added to the cooldown, in seconds.")]
        public float cooldownJitter = 0.8f;
        [Tooltip("The spells it can use, with the distance each one is used from.")]
        public BossSpell[] spells = new BossSpell[0];

        [Header("Idle motion (it never walks)")]
        [Tooltip("Upward speed of each hop on the spot.")]
        public float hopSpeed = 2.4f;
        [Tooltip("Seconds between two hops.")]
        public float hopInterval = 3.2f;
        [Tooltip("Downward acceleration (m/s^2).")]
        public float gravity = 22f;
        [Tooltip("How fast it turns to face the player (degrees/s).")]
        public float turnSpeed = 140f;
        [Tooltip("Metres it shuffles side to side while waiting (0 = dead centre).")]
        public float sideStep = 0.7f;
        [Tooltip("How flat it gets when it lands.")]
        [Range(0f, 0.6f)] public float squash = 0.3f;
        [Tooltip("How tall it gets at the top of a hop.")]
        [Range(0f, 0.6f)] public float stretch = 0.26f;
        [Tooltip("Vertical speed -> stretch factor.")]
        public float bounceStretch = 0.05f;
        [Tooltip("How quickly the squash / stretch follows the bounce.")]
        public float squashBlend = 12f;

        [Header("Summoning")]
        [Tooltip("Slimes it can call (empty = the wild slimes of the scene).")]
        public GameObject[] minionPrefabs = new GameObject[0];
        [Tooltip("Start the ritual as soon as the fight begins.")]
        public bool summonOnStart = true;
        [Tooltip("Seconds between two summoning rituals.")]
        public float summonCooldown = 14f;
        [Tooltip("Seconds between two rituals while its health is low (it gets desperate).")]
        public float desperateCooldown = 8f;
        [Tooltip("Below this fraction of health the desperate cooldown is used.")]
        [Range(0f, 1f)] public float desperateBelow = 0.35f;
        [Tooltip("How many slimes one ritual calls.")]
        public int summonCount = 3;
        [Tooltip("Dearest circle (m) from the boss where a minion may appear.")]
        public float summonRadius = 9f;
        [Tooltip("Closest circle (m) from the boss.")]
        public float summonMinRadius = 4.5f;
        [Tooltip("Seconds the magic circles charge before the slimes appear.")]
        public float summonDelay = 1.8f;
        [Tooltip("Circle drawn under every summon (empty = a ring of light is built at runtime).")]
        public GameObject circleEffect;
        [Tooltip("One-shot played where a minion pops out.")]
        public GameObject summonBurst;
        [Tooltip("Largest number of its own minions alive at once.")]
        public int maxMinions = 8;
        [Tooltip("Minions may not appear closer than this (m) to the player.")]
        public float minionPlayerDistance = 3f;

        [Header("Health")]
        [Tooltip("Hit points of the boss.")]
        public float maxHealth = 600f;
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
        readonly List<GameObject> _minions = new List<GameObject>();
        readonly List<SummonCircle> _circles = new List<SummonCircle>();

        Vector3 _home;
        float _yaw;
        float _cooldown;
        float _summonTimer;
        float _hopTimer;
        float _verticalVelocity;
        bool _airborne;
        float _sidePhase;
        float _scaleY = 1f;
        float _stun;
        bool _engaged;
        bool _dying;

        Vector3 _restScale = Vector3.one;
        Vector3 _homeLocalPosition;
        float _bottomY;
        Quaternion _restRotation = Quaternion.identity;
        int _upAxis = 1;
        bool _restPoseCaptured;

        ChunkManager _chunks;

        /// <summary>A magic circle waiting for the slime it is about to bring in.</summary>
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

        void Awake()
        {
            if (model == null) model = FindModel();
            if (target == null) ResolveTarget();

            _home = transform.position;
            _yaw = transform.eulerAngles.y;
            _sidePhase = Random.Range(0f, 10f);

            FitModel();
            _homeLocalPosition = model != null ? model.localPosition : Vector3.zero;

            SetupHealth();

            if (spells == null || spells.Length == 0) spells = DefaultKit();

            // The summoning circles need a visual; if none was set up, use the magic effect that
            // ships in Resources (the same one the player's charging circles come from).
            if (circleEffect == null) circleEffect = Resources.Load<GameObject>("ThirdPersonMagicFX/MagicCircle");

            _cooldown = 1.2f;
            _summonTimer = summonOnStart ? 2.5f : summonCooldown;
        }

        void Start()
        {
            if (spawnEffect != null)
                MagicEffects.DestroyLater(MagicEffects.Spawn(spawnEffect, transform.position, Quaternion.identity));
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            if (_dying) { AnimateModel(dt, false); return; }

            if (target == null) { ResolveTarget(); if (target == null) return; }

            float distance = FlatDistance(transform.position, target.position);
            _engaged = distance <= Mathf.Max(1f, range);

            FacePlayer(dt);
            KeepOnGround(dt);

            if (_stun > 0f) { _stun -= dt; AnimateModel(dt, false); return; }

            if (_engaged) Cast(dt, distance);
            Summon(dt);

            AnimateModel(dt, _engaged);
        }

        // =====================================================================
        //  Facing + hopping on the spot (it never walks anywhere)
        // =====================================================================
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
        /// Stays exactly where it was put: its position comes from its home spot (plus a small
        /// side-to-side shuffle) and the height of the ground under it. Only the hop lifts it.
        /// </summary>
        void KeepOnGround(float dt)
        {
            float ground = GroundHeight(_home.x, _home.z);
            if (_restHeight < -1000f) _restHeight = ground;

            _hopTimer -= dt;

            // Hops on the spot (a boss slime is always a little impatient).
            if (!_airborne && _hopTimer <= 0f)
            {
                _verticalVelocity = Mathf.Max(0.2f, hopSpeed);
                _airborne = true;
                _hopTimer = Mathf.Max(0.2f, hopInterval) + Random.Range(-0.4f, 0.6f);
            }

            if (_airborne)
            {
                _verticalVelocity -= Mathf.Max(0.01f, gravity) * dt;
                _restHeight += _verticalVelocity * dt;

                if (_restHeight <= ground)
                {
                    _restHeight = ground;
                    _verticalVelocity = 0f;
                    _airborne = false;
                }
            }
            else
            {
                _restHeight = ground;
            }

            // A slow shuffle around the home spot, so it feels alive without ever walking off.
            Vector3 lateral = Vector3.zero;
            if (sideStep > 0.01f)
            {
                float s = Mathf.Sin((Time.time + _sidePhase) * 0.5f) * sideStep;
                lateral = transform.right * s;
            }

            var want3 = new Vector3(_home.x + lateral.x, _restHeight, _home.z + lateral.z);
            transform.position = Vector3.Lerp(transform.position, want3, 1f - Mathf.Exp(-14f * dt));
        }

        float _restHeight = float.MinValue;

        // =====================================================================
        //  Casting
        // =====================================================================
        void Cast(float dt, float distance)
        {
            // It keeps facing and hopping the whole time; only the spell waits for the cooldown.
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
                    float gap = distance < s.minRange ? s.minRange - distance : Mathf.Max(0f, distance - s.maxRange);
                    if (gap < best) { best = gap; fallback = s; }
                }
                return fallback;
            }

            float roll = Random.Range(0f, total);
            foreach (BossSpell s in spells)
            {
                if (s == null || !s.Fits(distance) || s.weight <= 0f) continue;
                roll -= s.weight;
                if (roll <= 0f) return s;
            }
            return null;
        }

        /// <summary>
        /// A burst on the ground under the player (the element drops on them) and everything
        /// nearby is hurt.
        /// </summary>
        void CastOnGround(BossSpell spell)
        {
            if (target == null) return;

            Vector3 point = target.position;
            point = new Vector3(point.x, GroundHeight(point.x, point.z), point.z);

            PlayOnce(spell.effect, point, spell.effectScale);
            DamageArea(point, spell);
            _stun = 0.25f;
        }

        /// <summary>A slash / bolt that travels to the player.</summary>
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

        /// <summary>Hurts everything with hit points inside the spell's radius (never the boss itself).</summary>
        void DamageArea(Vector3 point, BossSpell spell)
        {
            if (spell.damage <= 0f) return;

            Vector3 away = target != null ? Flat(target.position - point) : Flat(transform.forward);

            var info = new DamageInfo(Mathf.Max(0f, spell.damage), point, away,
                                      Mathf.Max(0f, spell.knockback), gameObject);

            var points = new List<Vector3>(8);

            // Nova / shockwave: it is an area around the caster, so the caster's own slimes are
            // spared (otherwise a 10 m shockwave would wipe out everything it just summoned).
            bool isArea = spell.kind == BossSpellKind.Nova || spell.kind == BossSpellKind.Shockwave;

            if (isArea) Damage.ApplyRadial(point, Mathf.Max(0.1f, spell.radius), info, transform, hitMask, points, _minions);
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
                    name = "Slash", kind = BossSpellKind.Projectile,
                    damage = 14f, radius = 1.4f, speed = 18f, minRange = 6f, maxRange = 34f, weight = 1.2f
                },
                new BossSpell
                {
                    name = "Nova", kind = BossSpellKind.Nova,
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
        //  Summoning (magic circles first, slimes second)
        // =====================================================================
        void Summon(float dt)
        {
            // Circles already on the ground keep charging, whether or not the fight is still on.
            for (int i = _circles.Count - 1; i >= 0; i--)
            {
                SummonCircle c = _circles[i];
                c.timer -= dt;

                float t = Mathf.Clamp01(1f - c.timer / Mathf.Max(0.1f, summonDelay));

                // The circle spins faster and breathes as the slime is about to appear.
                if (c.circle != null)
                {
                    c.circle.transform.Rotate(Vector3.up, (40f + 120f * t) * dt, Space.World);
                    c.circle.transform.localScale = Vector3.one * (c.diameter * (1f + Mathf.Sin(Time.time * 9f) * 0.04f * t));
                }

                if (c.ring != null)
                {
                    float s = Mathf.Lerp(0.35f, 1f, t);
                    c.ring.transform.localScale = new Vector3(c.diameter * s, 0.06f, c.diameter * s);
                }

                if (c.timer > 0f) continue;

                // Time is up: the slime appears where the circle is.
                SpawnMinionAt(c.position);
                if (c.circle != null) MagicEffects.DestroyLater(c.circle, 1.2f);
                if (c.ring != null) Destroy(c.ring, 1.2f);
                _circles.RemoveAt(i);
            }

            if (_dying || !_engaged) return;

            _summonTimer -= dt;
            if (_summonTimer > 0f) return;

            if (summonCount <= 0) { _summonTimer = Mathf.Max(1f, summonCooldown); return; }
            if (AliveMinions() >= maxMinions) { _summonTimer = 2f; return; }

            BeginSummon();
        }

        /// <summary>Starts a ritual: one magic circle per slime, all on the ground around the boss.</summary>
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
                    diameter = Random.Range(1.5f, 2.1f)
                };

                // The magic circle itself (Hovl pack / Resources effect), laid flat on the ground.
                c.circle = SpawnOnGround(circleEffect, points[i], c.diameter * 0.35f);
                if (c.circle != null)
                {
                    c.circle.transform.localScale = Vector3.one * c.diameter;
                    MagicEffects.ForcePlay(c.circle, true);
                    MagicEffects.DestroyLater(c.circle, summonDelay + 3f);   // safety net
                }

                // A ring of light under it, so there is always something to read on the ground.
                c.ring = CreateLightRing(points[i], c.diameter);

                _circles.Add(c);
            }
        }

        /// <summary>Somewhere near the boss where a slime may come out (never on top of the player).</summary>
        List<Vector3> FindSummonSpots(int count)
        {
            var spots = new List<Vector3>(count);
            Vector3 player = target != null ? target.position : transform.position;
            float keepAway = Mathf.Max(0f, minionPlayerDistance);
            float outer = Mathf.Max(summonMinRadius, summonRadius);

            for (int i = 0; i < count; i++)
            {
                Vector3 best = Vector3.zero;
                bool found = false;
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
                    found = true;

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

        /// <summary>One slime climbs out of a finished circle.</summary>
        void SpawnMinionAt(Vector3 position)
        {
            GameObject prefab = PickMinionPrefab();
            if (prefab == null) return;

            GameObject instance = Instantiate(prefab, position, Quaternion.identity);
            instance.name = prefab.name;

            // A summoned slime is a NORMAL slime: whatever root scale the source object carried
            // (the boss's own instance carries its 4x multiplier) is dropped, so the minions are
            // never giant.
            instance.transform.localScale = Vector3.one;

            // Parent them under the boss (but keep the world position), so the scene stays tidy.
            instance.transform.SetParent(transform.parent, true);

            // The model is the instance itself, or its first child when the FBX is nested.
            Transform modelTransform = null;
            for (int i = 0; i < instance.transform.childCount; i++)
            {
                Transform child = instance.transform.GetChild(i);
                if (child != null && child.GetComponentInChildren<Renderer>(true) != null) { modelTransform = child; break; }
            }

            // NOTE: the model has to be known before the wanderer is added, because Awake runs the
            // moment a component is added (Start / Update come later, Awake does not).
            var wanderer = instance.GetComponent<SlimeWanderer>();
            if (wanderer == null) wanderer = instance.AddComponent<SlimeWanderer>();

            wanderer.SetModel(instance, modelTransform);

            // Same size as any other slime, whatever the source model's own scale happens to be.
            wanderer.normalizeModelHeight = true;
            wanderer.slimeHeight = 1f;

            wanderer.addHealth = true;
            wanderer.maxHealth = 40f;
            wanderer.moveSpeed = Random.Range(1.4f, 2.2f);
            wanderer.wanderRadius = Random.Range(3f, 6f);
            wanderer.PlaceAt(position);

            // Re-fit now the settings above are in place.
            wanderer.FitToModel();

            _minions.Add(instance);

            PlayOnce(summonBurst, position + Vector3.up * 0.4f, 1f);
            MagicEnvironment.Impact(position, 1.6f, 0.25f);
        }

        /// <summary>
        /// A slime model the boss may call: never the boss itself, and never an object that came
        /// from it (a reference to the boss's own model would carry the boss's size multiplier).
        /// </summary>
        static bool IsValidMinion(GameObject candidate)
        {
            if (candidate == null) return false;

            // A slime model must have geometry; an empty wrapper is not something to summon.
            if (candidate.GetComponentInChildren<Renderer>(true) == null) return false;

            // Never the boss (nor anything it owns): that would be a second, huge boss.
            if (candidate.GetComponent<SlimeBoss>() != null) return false;
            if (candidate.GetComponentInParent<SlimeBoss>(true) != null) return false;

            return true;
        }

        GameObject PickMinionPrefab()
        {
            if (minionPrefabs != null && minionPrefabs.Length > 0)
            {
                for (int i = 0; i < minionPrefabs.Length; i++)
                {
                    GameObject pick = minionPrefabs[Random.Range(0, minionPrefabs.Length)];
                    if (IsValidMinion(pick)) return pick;
                }
            }

            // No usable list: borrow the wild slimes of the scene (always plain slime models).
            var spawner = FindWildSpawner();
            if (spawner != null && spawner.slimePrefabs != null)
            {
                for (int i = 0; i < spawner.slimePrefabs.Length; i++)
                {
                    GameObject pick = spawner.slimePrefabs[Random.Range(0, spawner.slimePrefabs.Length)];
                    if (IsValidMinion(pick)) return pick;
                }
            }

            return null;
        }

        int AliveMinions()
        {
            for (int i = _minions.Count - 1; i >= 0; i--)
                if (_minions[i] == null) _minions.RemoveAt(i);
            return _minions.Count;
        }

        /// <summary>Lays an effect flat on the ground (the pack's circles are vertical planes).</summary>
        static GameObject SpawnOnGround(GameObject prefab, Vector3 point, float scale)
        {
            if (prefab == null) return null;

            GameObject go = MagicEffects.Spawn(prefab, point, Quaternion.Euler(90f, 0f, 0f), null,
                                              Mathf.Max(0.01f, scale));
            if (go == null) return null;

            if (go.GetComponentsInChildren<Renderer>(true).Length == 0)
            {
                Object.Destroy(go);
                return null;
            }
            return go;
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
                Color glow = new Color(0.55f, 0.42f, 1f, 1f);

                // Prefer an additive material from the magic pack so it reads as light on the ground.
                Material mat = FindAdditiveMaterial();
                if (mat != null)
                {
                    renderer.material = new Material(mat) { color = glow };
                }
                else
                {
                    Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (shader == null) shader = Shader.Find("Unlit/Color");
                    if (shader != null)
                    {
                        var unlit = new Material(shader) { color = glow };
                        if (unlit.HasProperty("_BaseColor")) unlit.SetColor("_BaseColor", glow);
                        renderer.material = unlit;
                    }
                }

                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            return go;
        }

        static Material FindAdditiveMaterial()
        {
#if UNITY_EDITOR
            string[] candidates =
            {
                "Assets/Hovl Studio/Magic effects pack/Materials/Additive.mat",
                "Assets/Hovl Studio/Magic effects pack/Materials/Circle.mat"
            };

            foreach (string path in candidates)
            {
                var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat != null) return mat;
            }
#endif
            return null;
        }

        static WildSlimeSpawner FindWildSpawner()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<WildSlimeSpawner>(FindObjectsInactive.Include);
#else
            return Object.FindObjectOfType<WildSlimeSpawner>(true);
#endif
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

        /// <summary>Remembers the rest pose and blows the model up to the boss size.</summary>
        void FitModel()
        {
            if (model == null) return;

            if (!_restPoseCaptured)
            {
                _restPoseCaptured = true;
                _restRotation = model.localRotation;

                Vector3 up = Quaternion.Inverse(_restRotation) * Vector3.up;
                float ax = Mathf.Abs(up.x), ay = Mathf.Abs(up.y), az = Mathf.Abs(up.z);
                _upAxis = (ax > ay && ax > az) ? 0 : (az > ay ? 2 : 1);
            }

            _restScale = model.localScale * Mathf.Max(0.1f, sizeMultiplier);
            model.localScale = _restScale;
            _bottomY = 0f;
        }

        // =====================================================================
        //  Health
        // =====================================================================
        void SetupHealth()
        {
            if (TryGetComponent(out _health) && _health != null)
            {
                _health.maxHealth = Mathf.Max(1f, maxHealth);
                _health.Damaged += OnDamaged;
                _health.Died += OnDied;
                return;
            }

            if (!TryGetComponent(out Collider collider) || collider == null)
            {
                var capsule = gameObject.AddComponent<CapsuleCollider>();
                float h = Mathf.Max(1f, sizeMultiplier);
                capsule.center = new Vector3(0f, h * 0.5f, 0f);
                capsule.height = h;
                capsule.radius = h * 0.42f;
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
            _health.destroyDelay = Mathf.Max(0.5f, deathDelay);
            _health.stirEnvironment = true;
            _health.environmentRadius = Mathf.Max(1.5f, sizeMultiplier);
            _health.flashColor = new Color(1f, 0.6f, 0.6f, 1f);
            _health.flashTime = 0.15f;

            _health.Damaged += OnDamaged;
            _health.Died += OnDied;
        }

        void OnDestroy()
        {
            if (_health == null) return;
            _health.Damaged -= OnDamaged;
            _health.Died -= OnDied;
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
        //  Bounce + helpers
        // =====================================================================
        /// <summary>Squash on landing, stretch while rising, and a slow heavy sway while it waits.</summary>
        void AnimateModel(float dt, bool engaged)
        {
            if (model == null) return;

            float target;
            if (!_airborne) target = engaged ? 1f - squash * 0.6f : 1f + Mathf.Sin(Time.time * 1.6f) * 0.03f;
            else target = 1f + Mathf.Clamp(_verticalVelocity * Mathf.Max(0f, bounceStretch), -squash, stretch);

            _scaleY = Mathf.Lerp(_scaleY, Mathf.Clamp(target, 0.5f, 1.6f), 1f - Mathf.Exp(-Mathf.Max(0.01f, squashBlend) * dt));

            float sxz = 1f / Mathf.Sqrt(_scaleY);

            Vector3 scale = _restScale;
            scale.x = _restScale.x * sxz;
            scale.z = _restScale.z * sxz;
            if (_upAxis == 1) scale.y = _restScale.y * _scaleY;
            else if (_upAxis == 0) scale.x = _restScale.x * _scaleY;
            else scale.z = _restScale.z * _scaleY;

            model.localScale = scale;

            float y = _homeLocalPosition.y + (_bottomY - _homeLocalPosition.y) * (1f - _scaleY);
            model.localPosition = new Vector3(_homeLocalPosition.x, y, _homeLocalPosition.z);
        }

        float GroundHeight(float x, float z)
        {
            if (_chunks == null) _chunks = FindChunkManager();
            if (_chunks == null) return _home.y;

            try { return _chunks.SampleHeight(x, z); }
            catch { _chunks = null; return _home.y; }
        }

        static ChunkManager FindChunkManager()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<ChunkManager>(FindObjectsInactive.Include);
#else
            return Object.FindObjectOfType<ChunkManager>(true);
#endif
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
        //  Boss bar (plain IMGUI, like the player's health bar)
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

            GUI.color = new Color(0.04f, 0.03f, 0.07f, 0.7f);
            GUI.DrawTexture(new Rect(x - 3f, y - 3f, width + 6f, height + 6f), Pixel());

            GUI.color = new Color(0.75f, 0.2f, 0.95f, 0.95f);
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
            _pixel.name = "SlimeBossPixel";
            _pixel.hideFlags = HideFlags.HideAndDontSave;
            return _pixel;
        }

        Texture2D _pixel;

        void OnDrawGizmosSelected()
        {
            Vector3 center = Application.isPlaying ? _home : transform.position;

            Gizmos.color = new Color(1f, 0.4f, 0.9f, 0.8f);
            DrawCircle(center, range);

            Gizmos.color = new Color(0.7f, 0.4f, 1f, 0.6f);
            DrawCircle(center, summonMinRadius);
            DrawCircle(center, summonRadius);
        }

        static void DrawCircle(Vector3 center, float radius)
        {
            const int steps = 48;
            Vector3 prev = center + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= steps; i++)
            {
                float a = (i / (float)steps) * Mathf.PI * 2f;
                Vector3 next = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                Gizmos.DrawLine(prev, next);
                prev = next;
            }
        }
    }
}

