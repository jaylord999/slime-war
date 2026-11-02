using System.Collections.Generic;
using MysticMap.World;
using UnityEngine;

namespace MysticMap
{
    /// <summary>
    /// Fills the world with wild slimes: every slime in the list is a colour variant of the same
    /// model (<c>anime_slime.fbx</c> and its (1)..(4) copies), and this picks one at random,
    /// drops it on a random patch of ground near the player and hands it a
    /// <see cref="SlimeWanderer"/> so it walks around and idles on its own.
    ///
    /// Nothing is stored in the scene: slimes are spawned at runtime, and when one is left far
    /// behind it is recycled back in around the player, so wherever you walk there are always
    /// slimes about.
    ///
    /// Positions come from <see cref="ChunkManager.SampleHeight"/>, so they follow the streamed
    /// procedural terrain, and the town / roadside life are respected when there is room to pick
    /// a spot outside them.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MysticMap/World/Wild Slime Spawner")]
    public class WildSlimeSpawner : MonoBehaviour
    {
        [Header("Slimes")]
        [Tooltip("The slime models to pick from at random (anime_slime.fbx and its coloured copies).")]
        public GameObject[] slimePrefabs = new GameObject[0];
        [Tooltip("How many wild slimes exist at the same time around the player.")]
        [Range(1, 60)] public int maxSlimes = 14;
        [Tooltip("Seconds between two spawns, so they pop in gently instead of all at once.")]
        [Min(0f)] public float spawnInterval = 0.45f;
        [Tooltip("Seconds to wait before the first slime appears.")]
        [Min(0f)] public float spawnDelay = 0.5f;

        [Header("Where they appear")]
        [Tooltip("Who the slimes are spread around (auto-filled with the slime player).")]
        public Transform target;
        [Tooltip("Look for the SlimePlayer automatically.")]
        public bool autoFindPlayer = true;
        [Tooltip("Slimes only spawn between these distances (m) from the player.")]
        public float minRadius = 9f;
        public float maxRadius = 34f;
        [Tooltip("A slime further than this (m) from the player is recycled back in around them.")]
        public float despawnRadius = 70f;
        [Tooltip("Keep the slimes out of the town (the roadside props there already fill it).")]
        public bool avoidTown = true;
        [Tooltip("Tries to find a spot outside the town before giving up and using any spot.")]
        [Range(1, 24)] public int spawnTries = 8;
        [Tooltip("Height (m) the models are placed above the ground (0.05 keeps the feet out of it).")]
        public float groundOffset = 0.05f;

        [Header("Variety")]
        [Tooltip("Random scale range. Leave at 1/1 to keep each slime model's own size.")]
        public Vector2 scaleRange = new Vector2(1f, 1f);
        [Tooltip("Random walking speed range (m/s).")]
        public Vector2 speedRange = new Vector2(0.9f, 1.9f);
        [Tooltip("Random wander radius range (m).")]
        public Vector2 wanderRange = new Vector2(4f, 9f);

        [Header("Health")]
        [Tooltip("Wild slimes get hit points, so the magic can hurt them (they hop away when hit).")]
        public bool giveHealth = true;
        [Tooltip("Random hit points range given to each new slime.")]
        public Vector2 healthRange = new Vector2(35f, 80f);

        [Header("Hierarchy")]
        [Tooltip("Where the spawned slimes are parented. Auto-created under this object when empty.")]
        public Transform slimeRoot;

        const string WildRootName = "Wild Slimes";

        readonly List<SlimeWanderer> _alive = new List<SlimeWanderer>();
        float _timer;
        float _elapsed;
        bool _warned;

        ChunkManager _chunks;

        /// <summary>How many wild slimes are alive right now.</summary>
        public int AliveCount
        {
            get
            {
                for (int i = _alive.Count - 1; i >= 0; i--)
                    if (_alive[i] == null) _alive.RemoveAt(i);
                return _alive.Count;
            }
        }

        void Awake()
        {
            ResolveRoot();
            if (target == null) ResolveTarget();
            _timer = Mathf.Max(0f, spawnDelay);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            _elapsed += dt;

            if (target == null) ResolveTarget();

            RecycleFarSlimes();
            SpawnOverTime(dt);
        }

        // =====================================================================
        //  Spawning
        // =====================================================================
        void SpawnOverTime(float dt)
        {
            if (maxSlimes <= 0) return;

            if (slimePrefabs == null || slimePrefabs.Length == 0)
            {
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning("[MysticMap] WildSlimeSpawner has no slime models. Run " +
                                     "MCP -> Slimes -> Wild slime spawner to fill the list.");
                }
                return;
            }

            _timer -= dt;
            if (_timer > 0f) return;
            if (target == null || _elapsed < spawnDelay) return;   // wait for the world / player
            if (AliveCount >= maxSlimes) return;

            _timer = Mathf.Max(0.02f, spawnInterval);
            SpawnOne();
        }

        /// <summary>Creates one slime: random variant, random spot, random size and speed.</summary>
        public SlimeWanderer SpawnOne()
        {
            if (slimePrefabs == null || slimePrefabs.Length == 0) return null;

            GameObject prefab = PickPrefab();
            if (prefab == null) return null;

            ResolveRoot();

            Vector3 spot = FindSpawnPosition();
            var instance = Instantiate(prefab, spot, Quaternion.identity, slimeRoot);
            instance.name = prefab.name;

            // A wild slime is always a plain slime: if a boss (or anything carrying the boss script)
            // ever ends up in the list, its size multiplier is dropped so nothing spawns gigantic.
            if (instance.GetComponentInParent<SlimeBoss>(true) != null || instance.GetComponent<SlimeBoss>() != null)
                instance.transform.localScale = Vector3.one;

            var wanderer = instance.GetComponent<SlimeWanderer>();
            if (wanderer == null) wanderer = instance.AddComponent<SlimeWanderer>();

            // The model is the instance itself when the prefab is the bare FBX, or its first child
            // when the FBX is nested (Unity imports an FBX with its own root object).
            wanderer.SetModel(instance, FindModelTransform(instance, prefab));

            // Random personality: pace and how far it strays from where it was put. The scale is
            // left alone so the replaced models keep their own size (the range defaults to 1..1).
            float speed = Random.Range(Mathf.Min(speedRange.x, speedRange.y),
                                       Mathf.Max(speedRange.x, speedRange.y));
            float wander = Random.Range(Mathf.Min(wanderRange.x, wanderRange.y),
                                        Mathf.Max(wanderRange.x, wanderRange.y));

            float scale = Random.Range(Mathf.Min(scaleRange.x, scaleRange.y),
                                       Mathf.Max(scaleRange.x, scaleRange.y));
            if (!Mathf.Approximately(scale, 1f)) instance.transform.localScale = Vector3.one * Mathf.Max(0.1f, scale);

            wanderer.moveSpeed = speed;
            wanderer.wanderRadius = wander;
            wanderer.turnSpeed = 90f + Random.Range(0f, 160f);

            // Hit points (the Health component itself is added by the wanderer on Awake).
            wanderer.addHealth = giveHealth;
            wanderer.maxHealth = Random.Range(Mathf.Min(healthRange.x, healthRange.y),
                                              Mathf.Max(healthRange.x, healthRange.y));

            wanderer.PlaceAt(spot);

            _alive.Add(wanderer);
            return wanderer;
        }

        GameObject PickPrefab()
        {
            for (int i = 0; i < slimePrefabs.Length; i++)
            {
                GameObject candidate = slimePrefabs[Random.Range(0, slimePrefabs.Length)];
                if (candidate != null) return candidate;
            }
            return null;
        }

        static Transform FindModelTransform(GameObject instance, GameObject prefab)
        {
            // A prefab that already holds the mesh on its root: the root itself is the model (and
            // keeps whatever orientation the importer gave it).
            if (prefab != null && prefab.GetComponentInChildren<Renderer>(true) != null)
                return null;   // SetModel falls back to the instance itself

            // A nested FBX: the first child is the imported root.
            for (int i = 0; i < instance.transform.childCount; i++)
            {
                Transform child = instance.transform.GetChild(i);
                if (child != null && child.GetComponentInChildren<Renderer>(true) != null) return child;
            }
            return null;
        }

        /// <summary>
        /// A random spot on the ground between <see cref="minRadius"/> and <see cref="maxRadius"/>
        /// from the player. It stays out of the town when <see cref="avoidTown"/> is on.
        /// </summary>
        public Vector3 FindSpawnPosition()
        {
            Vector3 center = target != null ? target.position : transform.position;

            int tries = Mathf.Max(1, spawnTries);
            Vector3 best = Vector3.zero;
            bool found = false;

            for (int i = 0; i < tries; i++)
            {
                Vector2 dir = Random.insideUnitCircle;
                if (dir.sqrMagnitude < 0.0001f) dir = Vector2.right;
                dir.Normalize();

                float distance = Random.Range(Mathf.Max(0f, minRadius), Mathf.Max(minRadius, maxRadius));
                float x = center.x + dir.x * distance;
                float z = center.z + dir.y * distance;

                Vector3 candidate = new Vector3(x, GroundHeight(x, z) + groundOffset, z);

                if (!found) { best = candidate; found = true; }

                // Keep looking for a spot in the wild, but never come back empty handed.
                if (avoidTown && TownBounds.ContainsPadded(x, z)) continue;

                best = candidate;
                break;
            }

            return best;
        }

        /// <summary>Puts anything that wandered too far back in around the player.</summary>
        void RecycleFarSlimes()
        {
            if (target == null) return;

            Vector3 c = target.position;
            float limit = Mathf.Max(maxRadius, despawnRadius);
            float limitSq = limit * limit;

            for (int i = _alive.Count - 1; i >= 0; i--)
            {
                SlimeWanderer slime = _alive[i];
                if (slime == null)
                {
                    _alive.RemoveAt(i);
                    continue;
                }

                // Killed by the magic: Health is about to remove it, so it no longer counts and
                // a new slime takes its place.
                Health health = slime.Health;
                if (health != null && !health.IsAlive)
                {
                    _alive.RemoveAt(i);
                    continue;
                }

                Vector3 p = slime.transform.position;
                float dx = p.x - c.x, dz = p.z - c.z;
                if (dx * dx + dz * dz <= limitSq) continue;

                // Same slime, a fresh patch of ground near the player.
                Vector3 spot = FindSpawnPosition();
                slime.PlaceAt(spot);
            }
        }

        // =====================================================================
        //  Tools
        // =====================================================================
        float GroundHeight(float x, float z)
        {
            if (_chunks == null) _chunks = FindChunkManager();
            if (_chunks == null) return transform.position.y + 1f;

            try { return _chunks.SampleHeight(x, z); }
            catch { _chunks = null; return transform.position.y + 1f; }
        }

        static ChunkManager FindChunkManager()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<ChunkManager>(FindObjectsInactive.Include);
#else
            return Object.FindObjectOfType<ChunkManager>(true);
#endif
        }

        void ResolveRoot()
        {
            if (slimeRoot != null) return;

            Transform existing = transform.Find(WildRootName);
            if (existing == null)
            {
                var go = new GameObject(WildRootName);
                go.transform.SetParent(transform, false);
                existing = go.transform;
            }
            slimeRoot = existing;
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

        void OnDrawGizmosSelected()
        {
            Vector3 center = target != null ? target.position : transform.position;

            Gizmos.color = new Color(0.5f, 0.9f, 1f, 0.35f);
            DrawCircle(center, minRadius);
            DrawCircle(center, maxRadius);
            DrawCircle(center, despawnRadius);
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
