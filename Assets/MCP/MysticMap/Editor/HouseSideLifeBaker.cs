#if UNITY_EDITOR
using System.Collections.Generic;
using MysticMap;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MysticMap.EditorTools
{
    public static partial class MysticMapBuilder
    {
        // ---------------------------------------------------------------------
        //  House-side life: small rocks + grass around the base of every house.
        //
        //  Nothing is placed in the scene. This baker only writes the house
        //  FOOTPRINTS (x, z, half extent, yaw) and the prefab lists onto the
        //  HouseSideLife component; at runtime the props are generated around the
        //  player and recycled as soon as they (or their house) are out of range,
        //  exactly like the terrain grass.
        // ---------------------------------------------------------------------
        const string HLRockAmountKey = "MM.HouseRockAmount";
        const string HLGrassAmountKey = "MM.HouseGrassEdgeAmount";

        static readonly string[] HLRockNames = { "Stone1", "Stone2", "Stone3" };
        static readonly string[] HLGrassNames = { "Grass1", "Grass2", "Grass3", "Grass4", "Rye" };

        [MenuItem("MCP/Town/House edges/Bake house-side life")]
        public static void BakeHouseSideLifeMenu()
        {
            BakeHouseSideLife();
            Log("House-side life baked: rocks + grass around every house (runtime streamed, nothing saved).");
        }

        [MenuItem("MCP/Town/House edges/More rocks +")]
        public static void MoreHouseRocksMenu() => BumpHouseSideAmount(HLRockAmountKey, 2);

        [MenuItem("MCP/Town/House edges/Fewer rocks -")]
        public static void FewerHouseRocksMenu() => BumpHouseSideAmount(HLRockAmountKey, -2);

        [MenuItem("MCP/Town/House edges/More grass +")]
        public static void MoreHouseEdgeGrassMenu() => BumpHouseSideAmount(HLGrassAmountKey, 2);

        [MenuItem("MCP/Town/House edges/Fewer grass -")]
        public static void FewerHouseEdgeGrassMenu() => BumpHouseSideAmount(HLGrassAmountKey, -2);

        static void BumpHouseSideAmount(string key, int delta)
        {
            int current = EditorPrefs.GetInt(key, 6);
            int value = Mathf.Clamp(current + delta, 0, 24);
            EditorPrefs.SetInt(key, value);
            Log("House-side amount (" + key + ") set to " + value + " -> re-applied.");

            if (TryGetHouseSideLife(out var life)) ApplyHouseSideSettings(life);
        }

        [MenuItem("MCP/Town/House edges/Remove house-side life")]
        public static void RemoveHouseSideLifeMenu()
        {
            var go = GameObject.Find("House Side Life");
            if (go != null) Object.DestroyImmediate(go);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            Log("Removed house-side life.");
        }

        /// <summary>Batch-mode entry point.</summary>
        public static void BakeHouseSideLife()
        {
            var go = EnsureHouseSideLifeObject();

            // ---- the house footprints, straight out of the stored town design ----
            var list = new List<Vector4>();
            var pieces = TownDesignState.Pieces;

            // A design that has gone empty (never captured) would silently produce no houses and
            // only a small part of the town would get dressed. Fall back to the town that is
            // actually standing in the scene instead.
            if (pieces == null || pieces.Count == 0)
            {
                var town = GameObject.Find(TownDesignState.TownRootName);
                if (town != null)
                {
                    pieces = TownDesignState.Capture(town.transform, false);
                    if (pieces != null && pieces.Count > 0)
                        Debug.Log("[MysticMap] The stored town design was empty - reading the " +
                                  TownDesignState.TownRootName + " in the scene instead (" +
                                  pieces.Count + " pieces). Use MCP > Town > 6 to store it.");
                }
            }

            if (pieces != null)
            {
                foreach (var p in pieces)
                {
                    if (p == null || string.IsNullOrEmpty(p.prefab)) continue;
                    if (p.group != "Houses" && p.group != "Buildings") continue;

                    var pf = AssetDatabase.LoadAssetAtPath<GameObject>(p.prefab);
                    if (pf == null) continue;

                    Bounds b = PlannedBoundsFast(pf, p.pos.x, p.pos.z, p.yaw, p.scale, p.pos.y);

                    // A square footprint that covers the building's longer side, so the ring
                    // outside it always clears the walls even when the house is not square.
                    float half = Mathf.Max(b.extents.x, b.extents.z);
                    if (half < 0.4f) half = 0.4f;

                    list.Add(new Vector4(b.center.x, b.center.z, half, p.yaw));
                }
            }

            if (list.Count == 0)
            {
                Debug.LogWarning("[MysticMap] No houses in the stored town design - bake the town first. " +
                                 "House-side life will cover nothing.");
            }

            var life = go.GetComponent<HouseSideLife>();
            life.houses = list.ToArray();
            life.rockPrefabs = LoadNamesByPath(PropsDir, HLRockNames);
            life.grassPrefabs = LoadNamesByPath(PropsDir, HLGrassNames);

            ApplyHouseSideSettings(life);

            EditorUtility.SetDirty(life);
            EditorSceneManager.MarkSceneDirty(go.scene);
            AssetDatabase.SaveAssets();
        }

        /// <summary>Pushes the density prefs onto the component (also used by the +/- menu items).</summary>
        static void ApplyHouseSideSettings(HouseSideLife life)
        {
            if (life == null) return;

            life.rocksPerHouse = Mathf.Clamp(EditorPrefs.GetInt(HLRockAmountKey, 8), 0, 24);
            life.grassPerHouse = Mathf.Clamp(EditorPrefs.GetInt(HLGrassAmountKey, 10), 0, 24);

            // Pebble-sized stones (Stone1/2/3) and small tufts: the stone meshes are already
            // compact, so a scale near 0.3 reads as loose stones on the ground, not boulders.
            life.rockScale = 0.32f;
            life.grassScale = 0.7f;

            // Re-size the hard cap so a busy street of houses does not blow the instance budget.
            life.maxLive = Mathf.Clamp((life.rocksPerHouse + life.grassPerHouse) * 22, 40, 900);
        }

        static GameObject EnsureHouseSideLifeObject()
        {
            var go = GameObject.Find("House Side Life");
            if (go == null) go = new GameObject("House Side Life");
            if (go.GetComponent<HouseSideLife>() == null) go.AddComponent<HouseSideLife>();
            return go;
        }

        static bool TryGetHouseSideLife(out HouseSideLife life)
        {
            life = null;
            var go = GameObject.Find("House Side Life");
            if (go != null) life = go.GetComponent<HouseSideLife>();
            return life != null;
        }

        static GameObject[] LoadNamesByPath(string dir, string[] names)
        {
            var outList = new List<GameObject>();
            foreach (string n in names)
            {
                var pf = AssetDatabase.LoadAssetAtPath<GameObject>(dir + "/" + n + ".prefab");
                if (pf != null) outList.Add(pf);
            }
            return outList.ToArray();
        }
    }
}
#endif
