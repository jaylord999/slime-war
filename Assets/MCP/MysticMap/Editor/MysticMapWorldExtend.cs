#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MysticMap.EditorTools
{
    /// <summary>
    /// Additive pass that turns the hand-built 1 km map into the "town island" of the endless
    /// procedural world.
    ///
    /// It does NOT touch the fortress, its walls, its buildings or its interior. It only:
    ///   1. runs the existing road network out to the very edge of the map (the original
    ///      roads stopped 30 m short) and snaps the side roads onto the chunk grid so the
    ///      procedural network can continue from them exactly,
    ///   2. re-grades the ground along those roads, cutting a walkable pass through the
    ///      border mountains (the ramp widens with the size of the cut, so the pass has a
    ///      walkable grade instead of a cliff),
    ///   3. repaints the ground splat and re-bakes grass + trees so the new passes are clear.
    ///
    /// Run it once before playing:  MCP > Procedural World > 1. Open the town road passes.
    /// </summary>
    public static partial class MysticMapBuilder
    {
        /// <summary>Grid the procedural roads live on (must match WorldSettings.chunkSize).</summary>
        public const float RoadGrid = 100f;

        static float SnapGrid(float v) => Mathf.Round(v / RoadGrid) * RoadGrid;

        /// <summary>Where the procedural road network takes over from the town's roads.</summary>
        public static Vector2 RoadExitNorth => new Vector2(Village.x, MapSize);
        public static Vector2 RoadExitSouth => new Vector2(Village.x, 0f);

        /// <summary>The country-road exits (snapped onto the road grid).</summary>
        public static Vector2[] RoadSideExits()
        {
            float west = SnapGrid(Village.x - 300f);     // 200
            float east = SnapGrid(Village.x + 300f);     // 800
            float south = SnapGrid(Village.y - 320f);    // 200
            float north = SnapGrid(Village.y + 380f);    // 900
            return new[]
            {
                new Vector2(west, 0f), new Vector2(west, MapSize),
                new Vector2(east, 0f), new Vector2(east, MapSize),
                new Vector2(0f, south), new Vector2(MapSize, south),
                new Vector2(0f, north), new Vector2(MapSize, north),
            };
        }

        [MenuItem("MCP/Procedural World/1. Open the town road passes")]
        public static void OpenRoadPassesMenu()
        {
            if (!OpenRoadPasses()) return;
            EditorUtility.DisplayDialog(
                "MysticMap",
                "Road passes are open.\n\nThe town's roads now reach the map edge through graded " +
                "mountain passes, so the front and back gates lead straight into the procedural world.\n\n" +
                "Next: MCP > Procedural World > 2. Add the world to the scene.",
                "OK");
        }

        /// <summary>Does the work and returns false when there was nothing to do.</summary>
        public static bool OpenRoadPasses()
        {
            const string scenePath = "Assets/Scenes/MysticMap.unity";
            if (!System.IO.File.Exists(scenePath))
            {
                Debug.LogWarning("[MysticMap] Scene not found: " + scenePath);
                return false;
            }

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var terrain = FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("[MysticMap] No Terrain in the scene. Build the map first.");
                return false;
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

            SetThroughRoads();
            CutRoadPasses(hres);

            Log("Repainting the ground splat for the opened road passes...");
            ApplySplatmap();
            Log("Re-baking grass and trees clear of the roads...");
            ApplyGrassDetails();
            ApplyTrees();
            RemoveScatterFromRoads();

            EditorUtility.SetDirty(_td);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Log("Road passes open - the town roads now reach all four map edges.");
            return true;
        }

        /// <summary>
        /// The town's road network, run all the way to the map edge and aligned to the
        /// procedural road grid so both networks meet exactly at the boundary.
        /// </summary>
        static void SetThroughRoads()
        {
            float vx = Village.x, vz = Village.y;
            const float Lo = 0.5f;
            float Hi = MapSize - 0.5f;

            float west = SnapGrid(vx - 300f);
            float east = SnapGrid(vx + 300f);
            float south = SnapGrid(vz - 320f);
            float north = SnapGrid(vz + 380f);

            _roadPolys = new List<Vector2[]>
            {
                // Main north / south road: through both gates and on to the map edges, curving
                // like a real road instead of a ruler line.
                WigglyRoad(new Vector2(vx, Lo), new Vector2(vx, Hi), 64f, 0.9f),

                // Country roads (north / south of town) reaching the east / west edges.
                WigglyRoad(new Vector2(Lo, south), new Vector2(Hi, south), 56f, 2.1f),
                WigglyRoad(new Vector2(Lo, north), new Vector2(Hi, north), 56f, 4.4f),

                // Country roads (west / east of town) reaching the north / south edges.
                WigglyRoad(new Vector2(west, Lo), new Vector2(west, Hi), 56f, 1.5f),
                WigglyRoad(new Vector2(east, Lo), new Vector2(east, Hi), 56f, 5.2f),

                // Interior east-west avenue inside the town (stops short of the side walls).
                WigglyRoad(new Vector2(vx - (FortHX - 6f), vz), new Vector2(vx + (FortHX - 6f), vz), 22f, 3.3f),
            };

            // The streets inside the town walls come from the builder's own town plan, so they
            // are added here too - the town floor is painted from this network.
            MysticMapBuilder.AddTownRoads(_roadPolys);

            // Every later pass (height carving, splat, grass, trees) must clear these curves.
            MysticMapBuilder.UseRoadPolys(_roadPolys);
        }

        /// <summary>
        /// Applies exactly the road flattening the builder uses, but with an apron that widens
        /// with the size of the cut, so the roads cross the border mountains along a graded
        /// pass instead of a cliff. Away from the border (where the ground is already at road
        /// level) the result is identical to the original terrain.
        /// </summary>
        static void CutRoadPasses(int hres)
        {
            var hs = new float[hres, hres];
            int touched = 0;

            for (int z = 0; z < hres; z++)
            {
                float wz = z * _spacing;
                for (int x = 0; x < hres; x++)
                {
                    float wx = x * _spacing;
                    float h = _h2[z, x];

                    float inner = RoadClearance;
                    float rd = RoadDistance(wx, wz);

                    if (rd <= inner)
                    {
                        h = RoadFloor;
                        touched++;
                    }
                    else
                    {
                        float diff = Mathf.Abs(h - RoadFloor);
                        float apron = Mathf.Min(150f, Mathf.Max(inner + 24f, inner + diff * 1.6f));
                        float blend = 1f - Mathf.Clamp01((rd - inner) / Mathf.Max(4f, apron - inner));
                        if (blend > 0.01f)
                        {
                            h = Mathf.Lerp(h, RoadFloor, blend);
                            touched++;
                        }
                    }

                    _h2[z, x] = h;
                    hs[z, x] = Mathf.Clamp01(h / MaxHeight);
                }
            }

            _td.SetHeights(0, 0, hs);
            Log("Road passes graded (" + touched + " heightmap samples touched).");
        }
    }
}
#endif
