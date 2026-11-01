#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MysticMap.World;
using Object = UnityEngine.Object;

namespace MysticMap.EditorTools
{
    /// <summary>
    /// Puts the endless procedural world into the Mystic Map scene:
    ///   * adds a "Procedural World" object with the streamer + the town anchor,
    ///   * fills the prefab palette from the FantasyEnvironments folders,
    ///   * points the streamer at the player and makes sure the first-person controller is
    ///     switched on,
    ///   * offers an editor preview so the generated country can be inspected without
    ///     pressing Play.
    ///
    /// The town is never modified by this tool.
    /// </summary>
    public static class ProceduralWorldSetup
    {
        const string EnvDir = "Assets/FantasyEnvironments/Environments";
        const string TreeDir = EnvDir + "/Ambient-Occlusion-Trees/Prefabs";
        const string PropsDir = EnvDir + "/Prefabs";
        const string TownDir = EnvDir + "/Town/Prefabs";
        const string RootName = "Procedural World";

        // =====================================================================
        //  Menu
        // =====================================================================
        [MenuItem("MCP/Procedural World/2. Add the world to the scene")]
        public static void AddToSceneMenu()
        {
            var manager = Setup(true);
            if (manager == null) return;

            EditorUtility.DisplayDialog(
                "MysticMap",
                "The procedural world is in the scene.\n\n" +
                "Press Play and walk out of the north or south gate: the road continues into " +
                "endless procedural country. The town itself is untouched.\n\n" +
                "Tip: MCP > Procedural World > World Settings tunes the seed, the streaming " +
                "distance and the content.",
                "OK");
        }

        [MenuItem("MCP/Procedural World/3. Preview the world here")]
        public static void PreviewMenu()
        {
            var manager = Setup(false);
            if (manager == null) return;

            Vector3 pivot = SceneView.lastActiveSceneView != null
                ? SceneView.lastActiveSceneView.pivot
                : new Vector3(500f, 30f, 620f);

            manager.buildInEditor = true;
            manager.BuildAround(pivot, true);
            EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
            SceneView.RepaintAll();
            Debug.Log("[MysticMap.World] Preview built around " + pivot.ToString("0") +
                      " (" + manager.LiveChunks + " chunks, " + manager.LegacyChunksSkipped +
                      " skipped over the town).");
        }

        [MenuItem("MCP/Procedural World/Clear the preview")]
        public static void ClearPreviewMenu()
        {
            var manager = FindManager();
            if (manager == null) return;

            manager.ClearAll();
            EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
            SceneView.RepaintAll();
            Debug.Log("[MysticMap.World] Preview cleared.");
        }

        [MenuItem("MCP/Procedural World/5. Log the tree setup")]
        public static void LogTreeSetupMenu()
        {
            var manager = FindManager();
            if (manager == null)
            {
                Debug.LogWarning("[MysticMap.World] No procedural world in this scene.");
                return;
            }

            manager.EnsureBuilt();
            int pool = manager.TreePoolSize;
            Debug.Log("[MysticMap.World] Trees: " + pool + " prefabs available, " +
                      manager.TreesLive + " standing, view-only=" + manager.treesOnlyInPlayerView +
                      ", cone=" + manager.treeViewAngle + " deg, keep=" + manager.treeKeepRadius + " m, " +
                      "full density within " + manager.treeFullDensityChunks + " chunks." +
                      (pool == 0
                          ? "  -> The palette is empty: press 'Refresh palette' in World Settings."
                          : string.Empty));
        }

        [MenuItem("MCP/Procedural World/4. Use generated trees (remove the stored ones)")]
        public static void UseGeneratedTreesMenu()
        {
            if (!EditorUtility.DisplayDialog(
                    "MysticMap",
                    "Grow the trees procedurally instead of storing them?\n\n" +
                    "Keeps the fortress town exactly as it is (houses, walls, plaza, its own trees).\n" +
                    "Removes the trees, grass, plants and rocks that were baked into the scene around\n" +
                    "the town and lets the streamed world grow them instead (one undo step).\n\n" +
                    "The town itself is never touched.",
                    "Do it", "Cancel"))
                return;

            ConvertToGeneratedTrees(FindManager(), true);
        }

        /// <summary>
        /// Switches the world from STORED vegetation to GENERATED vegetation:
        ///   1. the streamer keeps only the town square as hand-built (<see cref="TownAnchor.keepArea"/>),
        ///   2. every stored tree / grass / plant / rock outside that square is removed from the scene
        ///      (the town's own trees, houses and walls stay exactly as they are),
        ///   3. the region around the player is streamed right away, so the scene view already shows the
        ///      generated country without pressing Play.
        /// </summary>
        public static int ConvertToGeneratedTrees(ChunkManager manager, bool report)
        {
            if (manager == null)
            {
                if (report) Debug.LogWarning("[MysticMap.World] No world in the scene yet. Use menu 2 first.");
                return 0;
            }

            var anchor = manager.GetComponent<TownAnchor>();
            if (anchor == null) anchor = manager.gameObject.AddComponent<TownAnchor>();

            Terrain terrain = FindTerrain();
            anchor.legacyTerrain = terrain;
            anchor.keepArea = LegacyArea.Town;
            if (terrain != null && terrain.terrainData != null)
                anchor.townCenter = new Vector2(terrain.transform.position.x + terrain.terrainData.size.x * 0.5f,
                                                terrain.transform.position.z + terrain.terrainData.size.z * 0.5f);
            anchor.Refresh();

            manager.EnsureBuilt();
            manager.settings.Validate();
            if (manager.settings.palette.IsEmpty) FillPalette(manager);

            int removedProps = RemoveStoredOutsideTown(anchor);
            int removedTrees = RemoveTerrainTreesOutside(terrain, anchor);

            // Show the generated country immediately (the streamer would only build it in Play mode).
            Vector3 pivot = PlayerPosition();
            manager.buildInEditor = true;
            manager.BuildAround(pivot, true);

            EditorUtility.SetDirty(anchor);
            EditorUtility.SetDirty(manager);
            EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
            SceneView.RepaintAll();

            if (report)
                Debug.Log("[MysticMap.World] Trees are generated now: removed " + removedProps +
                          " stored props and " + removedTrees + " stored terrain trees outside the town, " +
                          "and streamed " + manager.LiveChunks + " chunks around " + pivot.ToString("0") + ".");

            return removedProps + removedTrees;
        }

        [MenuItem("MCP/Procedural World/Fill the prefab palette")]
        public static void FillPaletteMenu()
        {
            var manager = FindManager();
            if (manager == null)
            {
                Debug.LogWarning("[MysticMap.World] No world in the scene yet. Use menu 2 first.");
                return;
            }

            FillPalette(manager);
            EditorUtility.SetDirty(manager);
            EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
            Debug.Log("[MysticMap.World] Palette filled: " +
                      manager.settings.palette.trees.Length + " trees, " +
                      manager.settings.palette.grass.Length + " grass, " +
                      manager.settings.palette.flowers.Length + " flowers, " +
                      manager.settings.palette.rocks.Length + " rocks, " +
                      manager.settings.palette.walls.Length + " wall pieces.");
        }

        // =====================================================================
        //  Setup
        // =====================================================================
        static ChunkManager Setup(bool report)
        {
            var manager = FindManager();
            if (manager == null)
            {
                var go = new GameObject(RootName);
                Undo.RegisterCreatedObjectUndo(go, "Add Procedural World");
                manager = go.AddComponent<ChunkManager>();
                go.transform.position = Vector3.zero;
            }

            var anchor = manager.GetComponent<TownAnchor>();
            if (anchor == null) anchor = manager.gameObject.AddComponent<TownAnchor>();

            // The hand-built map (never modified - only measured).
            Terrain terrain = null;
            var terrains = Object.FindObjectsByType<Terrain>();
            foreach (var t in terrains)
            {
                if (t == null) continue;
                if (t.gameObject.name == "Terrain") { terrain = t; break; }
                if (terrain == null) terrain = t;
            }
            anchor.legacyTerrain = terrain;

            // Road exits: front and back of the town plus the country roads.
            anchor.exitNorth = MysticMapBuilder.RoadExitNorth;
            anchor.exitSouth = MysticMapBuilder.RoadExitSouth;
            anchor.sideExits = MysticMapBuilder.RoadSideExits();
            anchor.Refresh();

            if (manager.settings.palette.IsEmpty) FillPalette(manager);

            // Point the streamer at the player and make sure the controller is enabled.
            var players = Object.FindObjectsByType<SlimePlayer>();
            if (players != null && players.Length > 0)
            {
                manager.target = players[0].transform;
                if (!players[0].enabled)
                {
                    players[0].enabled = true;
                    Debug.Log("[MysticMap.World] Re-enabled the SlimePlayer on '" +
                              players[0].name + "' (it was switched off).");
                }
            }

            manager.settings.Validate();
            manager.EnsureBuilt();

            EditorUtility.SetDirty(manager);
            EditorUtility.SetDirty(anchor);
            EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);

            if (report)
                Debug.Log("[MysticMap.World] Streamer ready on '" + manager.name + "', anchored to " +
                          (terrain != null ? terrain.name : "no terrain (using the default 1 km map)") + ".");
            return manager;
        }

        public static ChunkManager FindManager()
        {
            var found = Object.FindObjectsByType<ChunkManager>();
            return found != null && found.Length > 0 ? found[0] : null;
        }

        // =====================================================================
        //  Removing the stored (baked) vegetation
        // =====================================================================
        static Terrain FindTerrain()
        {
            Terrain terrain = null;
            foreach (Terrain t in Object.FindObjectsByType<Terrain>())
            {
                if (t == null) continue;
                if (t.gameObject.name == "Terrain") return t;
                if (terrain == null) terrain = t;
            }
            return terrain;
        }

        static Vector3 PlayerPosition()
        {
            var players = Object.FindObjectsByType<SlimePlayer>();
            if (players != null && players.Length > 0) return players[0].transform.position;

            Camera cam = Camera.main;
            if (cam != null) return cam.transform.position;

            return new Vector3(500f, 30f, 620f);
        }

        /// <summary>
        /// Every baked prop of the map (the trees, grass, plants and rocks stored as real objects under
        /// the "Nature Props" / "Painted Props" roots) that sits outside the town square is destroyed.
        /// Everything inside the square stays exactly as it was authored.
        /// </summary>
        public static int RemoveStoredOutsideTown(TownAnchor anchor)
        {
            string[] rootNames = { "Nature Props", "Painted Props" };
            int removed = 0;

            foreach (string rootName in rootNames)
            {
                GameObject root = GameObject.Find(rootName);
                if (root == null) continue;

                var doomed = new List<GameObject>();
                Transform rt = root.transform;

                for (int i = rt.childCount - 1; i >= 0; i--)
                {
                    Transform child = rt.GetChild(i);
                    if (child == null) continue;
                    if (InsideTown(anchor, child.position)) continue;

                    doomed.Add(child.gameObject);
                }

                foreach (GameObject go in doomed)
                {
                    Undo.DestroyObjectImmediate(go);
                    removed++;
                }

                // Keep the (now smaller) root so the streamer and the glow tools still find it.
            }

            return removed;
        }

        static bool InsideTown(TownAnchor anchor, Vector3 world)
        {
            return anchor == null || anchor.DistanceToTown(world.x, world.z) <= 0f;
        }

        /// <summary>
        /// Removes the authored trees (Terrain tree instances) outside the town square. Inside it they
        /// stay, so the town keeps its own forest.
        /// </summary>
        public static int RemoveTerrainTreesOutside(Terrain terrain, TownAnchor anchor)
        {
            if (terrain == null || terrain.terrainData == null) return 0;

            TerrainData data = terrain.terrainData;
            TreeInstance[] all = data.treeInstances;
            if (all == null || all.Length == 0) return 0;

            Vector3 pos = terrain.transform.position;
            Vector3 size = data.size;

            var keep = new List<TreeInstance>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                TreeInstance ti = all[i];
                float x = pos.x + ti.position.x * size.x;
                float z = pos.z + ti.position.z * size.z;

                if (InsideTown(anchor, new Vector3(x, 0f, z))) keep.Add(ti);
            }

            if (keep.Count == all.Length) return 0;

            Undo.RecordObject(data, "Remove stored trees");
            data.treeInstances = keep.ToArray();
            return all.Length - keep.Count;
        }

        // =====================================================================
        //  Prefab palette
        // =====================================================================
        public static void FillPalette(ChunkManager manager)
        {
            if (manager == null || manager.settings == null) return;
            WorldPrefabPalette p = manager.settings.palette;

            // Trees: every ambient-occlusion tree (the stumps become scenery instead).
            p.trees = LoadAll(TreeDir, n => !n.ToLowerInvariant().Contains("stump"));

            p.bushes = LoadNames(PropsDir, "Bush1", "Fern1", "Fern2", "Fern3",
                                 "Plant1", "Plant2", "Plant3", "Plant4", "Plant5");
            p.grass = LoadNames(PropsDir, "Grass1", "Grass2", "Grass3", "Grass4", "Rye");
            p.flowers = LoadNames(PropsDir, "Flower1", "Flower2", "Flower3", "Flower4", "Flower5",
                                  "Flower6", "Flower7", "Flower8", "Flower9");
            p.rocks = LoadNames(PropsDir, "Rock1", "Rock2", "Rock3", "Stone2", "Stone3");
            p.stones = LoadNames(PropsDir, "Stone2", "Stone3", "Stone1", "Stone1_detail");

            // Ruins are built from the town pack's stone wall pieces.
            p.walls = LoadNames(TownDir, "Wall_part1", "Wall_part2", "Wall_corner", "Wall_entrance");
            p.props = LoadNames(TownDir, "cart1", "cart2", "cart3", "storage_barrel",
                                "storage_barrel_small", "storage_basket", "storage_bag");
        }

        static GameObject[] LoadNames(string dir, params string[] names)
        {
            var list = new List<GameObject>();
            foreach (string n in names)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(dir + "/" + n + ".prefab");
                if (prefab != null) list.Add(prefab);
            }
            return list.ToArray();
        }

        static GameObject[] LoadAll(string dir, System.Func<string, bool> filter)
        {
            var list = new List<GameObject>();
            if (!Directory.Exists(dir)) return list.ToArray();

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { dir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = Path.GetFileNameWithoutExtension(path);
                if (filter != null && !filter(name)) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null) list.Add(prefab);
            }
            return list.ToArray();
        }
    }
}
#endif
