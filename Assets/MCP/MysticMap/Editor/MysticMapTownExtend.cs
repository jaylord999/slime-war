#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MysticMap.EditorTools
{
    public static partial class MysticMapBuilder
    {
        // ---------------------------------------------------------------------
        //  Additive "town" pass, run in-place on the already-built MysticMap scene.
        //  It does NOT regenerate the walls/towers/gates (those are left untouched),
        //  only: (1) the road network + ground splat, (2) trees/grass/plants get
        //  cleared from every road, and (3) the town interior is re-filled with a
        //  ring of houses hugging the walls plus significant landmark buildings in
        //  the centre.
        // ---------------------------------------------------------------------
        public static void ExtendTownAndRoads()
        {
            const string scenePath = "Assets/Scenes/MysticMap.unity";
            if (!System.IO.File.Exists(scenePath))
            {
                Debug.LogWarning("[MysticMap] Scene not found: " + scenePath);
                return;
            }
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            var terrain = FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("[MysticMap] No Terrain in the open scene. Build the map first.");
                return;
            }
            _terrain = terrain;
            _td = terrain.terrainData;

            int hres = _td.heightmapResolution;
            _spacing = _td.size.x / (hres - 1);
            float[,] norm = _td.GetHeights(0, 0, hres, hres);
            _h2 = new float[hres, hres];
            for (int z = 0; z < hres; z++)
                for (int x = 0; x < hres; x++)
                    _h2[z, x] = norm[z, x] * _td.size.y;

            SetExtendedRoads();

            Log("Repainting ground splat with the extended road network...");
            ApplySplatmap();
            Log("Regenerating grass + trees (clear of roads)...");
            ApplyGrassDetails(_roadPolys);
            ApplyTrees();
            RemoveScatterFromRoads();
            EditorUtility.SetDirty(_td);

            RebuildTownHousing();

            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Log("Town housing + extended road network complete.");
        }

        // Roads that run through the town and then reach out toward the edges of the map,
        // all painted with the road terrain layer. The two country roads per axis spread
        // far from town so the road network visibly expands to the outer map.
        static void SetExtendedRoads()
        {
            var roads = new List<Vector2[]>();
            float vx = Village.x, vz = Village.y;
            const float Lo = 30f, Hi = 1000f - 30f;

            // Main north-south road: runs through the fortress gates to both map edges, wandering
            // like a country road instead of running dead straight.
            roads.Add(WigglyRoad(new Vector2(vx, Lo), new Vector2(vx, Hi), 64f, 0.9f));

            // Countryside roads (north/south of town) reaching the east/west map edges.
            roads.Add(WigglyRoad(new Vector2(Lo, vz - 350f), new Vector2(Hi, vz - 350f), 56f, 2.1f));
            roads.Add(WigglyRoad(new Vector2(Lo, vz + 350f), new Vector2(Hi, vz + 350f), 56f, 4.4f));

            // Countryside roads (west/east of town) reaching the north/south map edges.
            roads.Add(WigglyRoad(new Vector2(vx - 315f, Lo), new Vector2(vx - 315f, Hi), 56f, 1.5f));
            roads.Add(WigglyRoad(new Vector2(vx + 315f, Lo), new Vector2(vx + 315f, Hi), 56f, 5.2f));

            // Interior east-west avenue inside the town (stops short of the solid side walls).
            roads.Add(WigglyRoad(new Vector2(vx - (TownHX - 6f), vz), new Vector2(vx + (TownHX - 6f), vz), 22f, 3.3f));

            // The streets inside the town walls come from the builder's own town plan, so they
            // are added here too - the town floor is painted from this network.
            MysticMapBuilder.AddTownRoads(roads);

            _roadPolys = roads;

            // Give the builder the same network: the grass, the trees and the scatter it is about
            // to regenerate must all be cleared for these curves, not for an older road shape.
            MysticMapBuilder.UseRoadPolys(_roadPolys);
        }

        // Any scattered nature prop that now sits on a road, or inside the town, is removed
        // so roads stay clean and the town interior reads as built-up ground.
        static void RemoveScatterFromRoads()
        {
            var root = GameObject.Find("Nature Props");
            if (root == null) return;

            var kill = new List<GameObject>();
            foreach (Transform c in root.transform)
            {
                float px = c.position.x, pz = c.position.z;
                bool onRoad = RoadDistance(px, pz) < RoadClearance;

                float ax = Mathf.Abs(px - Village.x) - (TownHX + 6f);
                float az = Mathf.Abs(pz - Village.y) - (TownHZ + 6f);
                bool inTown = ax < 0f && az < 0f;

                if (onRoad || inTown) kill.Add(c.gameObject);
            }
            foreach (var g in kill)
                if (g != null) Object.DestroyImmediate(g);
            if (kill.Count > 0) Log("Removed " + kill.Count + " scatter props from roads/town.");
        }

        [MenuItem("MCP/Roads/Clear grass & plants from roads")]
        public static void ClearGrassFromRoadsMenu()
        {
            var scene = EditorSceneManager.GetActiveScene();
            CleanGrassFromRoads(scene);
        }

        public static void ClearGrassFromRoads()   // batch entry point
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/MysticMap.unity", OpenSceneMode.Single);
            CleanGrassFromRoads(scene);
        }

        // Regenerates the terrain grass detail + trees and drops any leftover scatter props so
        // a comfortable grass/plant-free shoulder exists around every road.
        static void CleanGrassFromRoads(Scene scene)
        {
            var terrain = FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("[MysticMap] No Terrain found.");
                return;
            }
            _terrain = terrain;
            _td = terrain.terrainData;
            int hres = _td.heightmapResolution;
            _spacing = _td.size.x / (hres - 1);
            float[,] norm = _td.GetHeights(0, 0, hres, hres);
            _h2 = new float[hres, hres];
            for (int z = 0; z < hres; z++)
                for (int x = 0; x < hres; x++)
                    _h2[z, x] = norm[z, x] * _td.size.y;

            SetExtendedRoads();
            ApplyGrassDetails(_roadPolys);
            ApplyTrees();
            RemoveScatterFromRoads();
            EditorUtility.SetDirty(_td);
            EditorSceneManager.SaveScene(scene, scene.path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Log("Regenerated grass + trees clear of all roads (clean shoulder).");
        }
        // ---- Town interior: the "walled town" design --------------------------------

        /// <summary>
        /// Re-fills the town interior with the "walled town" layout: the big central
        /// landmarks plus rows of houses hugging the inside of every wall.
        ///
        /// The layout is PLANNED as design pieces and stored in the town design, then the
        /// town is re-applied from that design - so the walls, towers and gates you see are
        /// the same ones, and the result is remembered for the next map rebuild.
        /// </summary>
        static void RebuildTownHousing()
        {
            var root = GameObject.Find(TownDesignState.TownRootName);
            if (root == null)
            {
                Log("No " + TownDesignState.TownRootName + " found; leaving town interior as-is.");
                return;
            }

            // Start from the town as it stands (walls, towers, gates + whatever is inside),
            // keep the perimeter and replace only the loose buildings.
            var design = TownDesignState.Capture(root.transform, false);
            var perimeter = new List<TownPiece>();
            foreach (var p in design)
                if (p.group == "Walls" || p.group == "Towers" || p.group == "Gates")
                    perimeter.Add(p);

            int order = 0;
            BuildWalledTownPlan(perimeter, ref order, true);

            TownDesignState.Store(perimeter, "walled");
            int placed = TownDesignState.Apply();
            Log("Town interior re-filled from the design: " + placed +
                " pieces (central landmarks + houses hugging the walls).");
        }

        /// <summary>
        /// The "walled town" layout: central landmarks, then rows of houses packed along the
        /// inside of every wall. <paramref name="list"/> may already hold the perimeter
        /// pieces; when <paramref name="withWalls"/> is true the whole fortress perimeter is
        /// planned first.
        /// </summary>
        internal static void BuildWalledTownPlan(List<TownPiece> list, ref int order, bool withWalls = true)
        {
            if (withWalls && (list == null || list.Count == 0))
            {
                PlanCornerTowers(list, ref order);
                PlanWallRuns(list, ref order);
                PlanGates(list, ref order);
            }

            // Fortress rectangle, taken from the towers already on the plan when there are any.
            float x0 = Village.x - TownHX, x1 = Village.x + TownHX;
            float z0 = Village.y - TownHZ, z1 = Village.y + TownHZ;
            bool first = true;
            foreach (var p in list)
            {
                if (p.group != "Towers") continue;
                if (first) { x0 = x1 = p.pos.x; z0 = z1 = p.pos.z; first = false; }
                else
                {
                    x0 = Mathf.Min(x0, p.pos.x); x1 = Mathf.Max(x1, p.pos.x);
                    z0 = Mathf.Min(z0, p.pos.z); z1 = Mathf.Max(z1, p.pos.z);
                }
            }
            float cx = (x0 + x1) * 0.5f;
            float cz = (z0 + z1) * 0.5f;

            var placed = new List<Bounds>();
            CollectStructuresFast(list, placed);   // keep new houses off every wall/tower/gate

            PlanCoreLandmarks(list, ref order, placed, cx, cz);
            PlanWallHouses(list, ref order, placed, cx, cz, x0, x1, z0, z1);
            if (withWalls && list.Count > 0) PlanTownProps(list, ref order);
        }

        static void PlanCoreLandmarks(List<TownPiece> list, ref int order, List<Bounds> placed,
                                      float cx, float cz)
        {
            PlanBld(list, ref order, placed, "TownCore", "Tavern",        cx - 24f, cz + 26f, 0f,  1.0f);
            PlanBld(list, ref order, placed, "TownCore", "Windmill",      cx + 28f, cz + 28f, 0f,  0.9f);
            PlanBld(list, ref order, placed, "TownCore", "Windmill",      cx - 28f, cz - 28f, 0f,  0.9f);
            PlanBld(list, ref order, placed, "TownCore", "WoodenHouse_2", cx + 24f, cz - 28f, 90f, 1.1f);
        }

        // Houses packed in rows that run parallel to each wall and hug the inside of it.
        // Each house is oriented so its long side runs along the wall; a second, slightly
        // inner row behind it fills the block towards the centre.
        static void PlanWallHouses(List<TownPiece> list, ref int order, List<Bounds> placed,
                                   float cx, float cz, float x0, float x1, float z0, float z1)
        {
            string[] pool = { "House1", "House2", "House3", "WoodenHouse_1", "House2", "House1" };
            int pi = 0;
            const float rowGap = 9f;

            // North (wall at z1) and South (wall at z0) walls run along X.
            foreach (var wall in new[] { z1, z0 })
            {
                int inward = wall > cz ? -1 : 1;
                foreach (var extra in new[] { 0f, rowGap })
                {
                    for (float x = x0 + 9f; x <= x1 - 9f;)
                    {
                        string nm = pool[pi++ % pool.Length];
                        Vector2 c = Footprint(nm);
                        float depth = Mathf.Min(c.x, c.y);
                        float width = Mathf.Max(c.x, c.y);
                        float lane = wall + inward * (depth * 0.5f + 2.6f + extra);
                        float yaw = (c.x >= c.y) ? 0f : 90f; // long side along the wall (X)
                        PlanBld(list, ref order, placed, "Houses", nm, x, lane, yaw, 1f);
                        x += width + 1.4f;
                    }
                }
            }

            // East (wall at x1) and West (wall at x0) walls run along Z.
            foreach (var wall in new[] { x1, x0 })
            {
                int inward = wall > cx ? -1 : 1;
                foreach (var extra in new[] { 0f, rowGap })
                {
                    for (float z = z0 + 9f; z <= z1 - 9f;)
                    {
                        string nm = pool[pi++ % pool.Length];
                        Vector2 c = Footprint(nm);
                        float depth = Mathf.Min(c.x, c.y);
                        float width = Mathf.Max(c.x, c.y);
                        float lane = wall + inward * (depth * 0.5f + 2.6f + extra);
                        float yaw = (c.y >= c.x) ? 0f : 90f; // long side along the wall (Z)
                        PlanBld(list, ref order, placed, "Houses", nm, lane, z, yaw, 1f);
                        z += width + 1.4f;
                    }
                }
            }
        }

        static readonly Dictionary<string, Vector2> _fpCache = new Dictionary<string, Vector2>();

        static Vector2 Footprint(string prefabName)
        {
            if (_fpCache.TryGetValue(prefabName, out var c)) return c;
            Vector2 r = new Vector2(8f, 8f);
            var p = LoadPrefab(TownDir + "/" + prefabName + ".prefab");
            if (p != null)
            {
                Bounds b = PlannedBoundsFast(p, 0f, 0f, 0f, 1f, 0f);
                r = new Vector2(Mathf.Max(1f, b.size.x), Mathf.Max(1f, b.size.z));
            }
            _fpCache[prefabName] = r;
            return r;
        }
    }
}
#endif
