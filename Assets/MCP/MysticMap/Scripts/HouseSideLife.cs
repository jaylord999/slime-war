using System.Collections.Generic;
using UnityEngine;

namespace MysticMap
{
    /// <summary>
    /// Dresses the foot of every town house with small rocks and grass tufts WITHOUT storing
    /// anything in the scene. The house positions are baked once (MCP > Town > House edges >
    /// Bake house-side life) into <see cref="houses"/>; from then on the props are generated on
    /// the fly around the player and recycled as soon as the house / prop is out of range.
    ///
    /// The props sit in a ring OUTSIDE each house footprint, so a building reads as a stone
    /// foundation bed with weeds creeping in at its base instead of growing out of the wall.
    ///
    /// Same shape as <see cref="RoadsideLife"/>: a check every <see cref="checkInterval"/>
    /// seconds, a target count per house, a pool for reuse and no per-frame allocations.
    /// </summary>
    public class HouseSideLife : MonoBehaviour
    {
        [Header("Tracking")]
        public Transform target;
        [Tooltip("Houses further than this from the player are left bare (m).")]
        [Range(20f, 150f)] public float radius = 58f;
        [Range(0.1f, 1f)] public float checkInterval = 0.3f;

        [Header("House footprints (baked: x, z, half extent, yaw - do not edit by hand)")]
        [Tooltip("One entry per house. Baked by MCP > Town > House edges > Bake house-side life.")]
        public Vector4[] houses = new Vector4[0];

        [Header("Small rocks (pebbles) at the wall foot")]
        public bool rocksOn = true;
        [Tooltip("How many pebbles a single house may have around it.")]
        [Range(0, 24)] public int rocksPerHouse = 8;
        [Tooltip("Pebbles start this far out from the wall (m).")]
        [Range(0f, 2f)] public float rockInner = 0.15f;
        [Tooltip("Pebbles never reach further than this from the wall (m).")]
        [Range(0.5f, 8f)] public float rockOuter = 2.4f;
        [Tooltip("Pebble size. The stone prefabs are already small; keep this well under 1 so " +
                 "they read as loose stones, not boulders.")]
        [Range(0.05f, 1.5f)] public float rockScale = 0.32f;
        public GameObject[] rockPrefabs = new GameObject[0];

        [Header("Grass around the buildings")]
        public bool grassOn = true;
        [Tooltip("How many grass tufts a single house may have around it.")]
        [Range(0, 24)] public int grassPerHouse = 10;
        [Tooltip("Grass stays in a wider, softer ring than the rocks (m).")]
        [Range(0.5f, 10f)] public float grassOuter = 3.6f;
        [Tooltip("Grass tuft size.")]
        [Range(0.2f, 2f)] public float grassScale = 0.7f;
        public GameObject[] grassPrefabs = new GameObject[0];

        [Header("Cost")]
        [Tooltip("Hard cap on live props, whatever the per-house counts add up to.")]
        [Range(20, 900)] public int maxLive = 420;

        [Tooltip("Lift the props this far off the ground so they never sink into the floor (m).")]
        [Range(0f, 0.3f)] public float groundOffset = 0.02f;

        const float DespawnPad = 16f;   // props are recycled this far past the radius
        const float RockSlack = 0.9f;   // extra clearance so a pebble never pokes into a wall

        class Deco { public GameObject go; public Vector3 pos; }

        readonly List<Deco> _rocks = new List<Deco>();
        readonly List<Deco> _grass = new List<Deco>();
        readonly List<GameObject> _poolRock = new List<GameObject>();
        readonly List<GameObject> _poolGrass = new List<GameObject>();

        float _despawnDist;
        float _timer;

        // Scratch buffer, reused every pass: the houses currently close enough to be dressed.
        readonly List<int> _near = new List<int>();

        void OnEnable()
        {
            if (target == null) ResolveTarget();
            _despawnDist = radius + DespawnPad;

            // Let the slime's magic sweep the house-side rubble as well.
            MagicEnvironment.Register(transform, MagicEnvMode.Hide, radius + DespawnPad);
        }

        void Update()
        {
            if (target == null) { ResolveTarget(); if (target == null) return; }

            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = checkInterval;

            Vector3 c = target.position;
            float rsq = _despawnDist * _despawnDist;

            // Anything that drifted out of range goes back to the pool first.
            Cull(_rocks, _poolRock, c, rsq);
            Cull(_grass, _poolGrass, c, rsq);

            if (houses == null || houses.Length == 0) return;

            CollectNearbyHouses(c, radius * radius);

            // ---- how much of each kind the nearby houses ask for ----
            int rockTarget = rocksOn && rockPrefabs.Length > 0 ? _near.Count * rocksPerHouse : 0;
            int grassTarget = grassOn && grassPrefabs.Length > 0 ? _near.Count * grassPerHouse : 0;

            if (rockTarget + grassTarget > maxLive)
            {
                float f = maxLive / (float)(rockTarget + grassTarget);
                rockTarget = Mathf.FloorToInt(rockTarget * f);
                grassTarget = Mathf.FloorToInt(grassTarget * f);
            }

            // ---- grow the deficit, trimming any excess -------------------------
            // Pebbles first (they hug the wall), then the grass, which sits in the wider ring -
            // but never let the stones eat the whole budget and starve the grass entirely.
            Grow(_rocks, _poolRock, rockPrefabs, rockTarget, c, false);
            Grow(_grass, _poolGrass, grassPrefabs, grassTarget, c, true);
        }

        /// <summary>Fills the scratch list with the indices of the houses around the player.</summary>
        void CollectNearbyHouses(Vector3 c, float rsq)
        {
            _near.Clear();
            for (int i = 0; i < houses.Length; i++)
            {
                Vector4 h = houses[i];
                float dx = h.x - c.x, dz = h.y - c.z;
                if (dx * dx + dz * dz <= rsq) _near.Add(i);
            }
        }

        /// <summary>
        /// Tops one category up to its target (spawning at random points around the nearby houses)
        /// and trims it back down when the target drops, so lowering the amount really thins it.
        /// </summary>
        void Grow(List<Deco> act, List<GameObject> pool, GameObject[] pre, int target,
                  Vector3 c, bool grass)
        {
            if (pre == null || pre.Length == 0) { RecycleAll(act, pool); return; }

            int need = target - act.Count;

            if (need > 0 && _near.Count > 0)
            {
                int tries = 0;
                int limit = need * 8 + 40;
                while (need > 0 && tries++ < limit)
                {
                    if (TryHousePoint(c, grass, out Vector3 p) && Spawn(act, pool, pre, p, grass))
                        need--;
                }
            }

            while (act.Count > target) RecycleAt(act, pool, act.Count - 1);
        }

        /// <summary>A random point in the ring outside one nearby house, or false if it is unusable.</summary>
        bool TryHousePoint(Vector3 c, bool grass, out Vector3 p)
        {
            p = Vector3.zero;

            Vector4 h = houses[_near[Random.Range(0, _near.Count)]];

            float inner = grass ? 0.1f : Mathf.Max(0f, rockInner) + RockSlack;
            float outer = grass ? Mathf.Max(inner + 0.4f, grassOuter) : Mathf.Max(inner + 0.3f, rockOuter);

            // A point on the ring: random side of the rectangular footprint, random distance out.
            // The footprint is a square of half extent h.z, so the ring follows the house's shape.
            float half = Mathf.Max(0.4f, h.z);
            float outDist = Random.Range(inner, outer);

            Vector2 local;
            int side = Random.Range(0, 4);
            float t = Random.Range(-1f, 1f);
            switch (side)
            {
                case 0: local = new Vector2(half + outDist, t * (half + outDist)); break;
                case 1: local = new Vector2(-(half + outDist), t * (half + outDist)); break;
                case 2: local = new Vector2(t * (half + outDist), half + outDist); break;
                default: local = new Vector2(t * (half + outDist), -(half + outDist)); break;
            }

            // Rotate into the house's own facing, then into world space.
            float yaw = h.w * Mathf.Deg2Rad;
            float cos = Mathf.Cos(yaw), sin = Mathf.Sin(yaw);
            float wx = h.x + local.x * cos - local.y * sin;
            float wz = h.y + local.x * sin + local.y * cos;

            // Never outside the range being kept alive, so nothing pops in far away.
            float dx = wx - c.x, dz = wz - c.z;
            if (dx * dx + dz * dz > _despawnDist * _despawnDist) return false;

            p = new Vector3(wx, SampleGround(wx, wz, c.y) + groundOffset, wz);
            return true;
        }

        bool Spawn(List<Deco> act, List<GameObject> pool, GameObject[] pre, Vector3 p, bool grass)
        {
            GameObject go = pool.Count > 0 ? pool[pool.Count - 1] : null;
            if (go == null)
            {
                var prefab = pre[Random.Range(0, pre.Length)];
                if (prefab == null) return false;
                go = Instantiate(prefab, transform);
                if (go == null) return false;
            }
            else { pool.RemoveAt(pool.Count - 1); go.SetActive(true); }

            go.transform.position = p;
            go.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

            float s = grass
                ? grassScale * Random.Range(0.7f, 1.3f)
                : rockScale * Random.Range(0.55f, 1.25f);
            go.transform.localScale = new Vector3(s, s, s);
            act.Add(new Deco { go = go, pos = p });
            return true;
        }

        void Cull(List<Deco> act, List<GameObject> pool, Vector3 c, float rsq)
        {
            for (int i = act.Count - 1; i >= 0; i--)
            {
                float dx = act[i].pos.x - c.x, dz = act[i].pos.z - c.z;
                if (dx * dx + dz * dz > rsq) RecycleAt(act, pool, i);
            }
        }

        void RecycleAll(List<Deco> act, List<GameObject> pool)
        {
            while (act.Count > 0) RecycleAt(act, pool, act.Count - 1);
        }

        void RecycleAt(List<Deco> act, List<GameObject> pool, int i)
        {
            Deco d = act[i];
            act.RemoveAt(i);
            if (d.go != null) { d.go.SetActive(false); pool.Add(d.go); }
        }

        void ResolveTarget()
        {
            Camera cam = Camera.main;
            if (cam != null) target = cam.transform;
            else
            {
                var go = GameObject.Find("Main Camera");
                if (go != null) target = go.transform;
            }
        }

        static float SampleGround(float x, float z, float fallbackY)
        {
            var t = Terrain.activeTerrain;
            if (t != null) return t.SampleHeight(new Vector3(x, 0f, z));
            if (Physics.Raycast(new Vector3(x, 200f, z), Vector3.down, out RaycastHit hit, 400f))
                return hit.point.y;
            return fallbackY;
        }

        void OnDisable()
        {
            MagicEnvironment.Unregister(transform);

            for (int i = 0; i < transform.childCount; i++)
            {
                var ch = transform.GetChild(i);
                if (ch != null) ch.gameObject.SetActive(false);
            }
            _rocks.Clear(); _grass.Clear();
            _poolRock.Clear(); _poolGrass.Clear();
            _near.Clear();
        }
    }
}
