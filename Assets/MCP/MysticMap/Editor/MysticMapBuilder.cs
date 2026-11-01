#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MysticMap.EditorTools
{
    /// <summary>
    /// One-click generator for a large (1km x 1km) mysterious fantasy map from the
    /// "FantasyEnvironments" asset. Menu: MCP -> Build Mystic Map
    ///
    /// Builds: rolling terrain, splat-painted ground (grass-dominant with roads),
    /// a network of roads, terrain trees (auto LOD/culled), lots of grass as terrain
    /// detail, scattered ground-cover props that are streamed in only around the
    /// player, a small village, fog + a short camera far plane so only nearby
    /// content is actually rendered, and a bouncing slime player seen from a third-person camera.
    /// </summary>
    public static partial class MysticMapBuilder
    {
        // ---- Where generated assets are stored --------------------------------
        internal const string GeneratedDir = "Assets/MCP/MysticMap/Generated";
        const string TerrainDataPath = GeneratedDir + "/MysticTerrain.asset";
        internal const string ScenePath = "Assets/Scenes/MysticMap.unity";

        // ---- Source asset folders (FantasyEnvironments) -----------------------
        const string EnvDir = "Assets/FantasyEnvironments/Environments";
        const string LayerDir = EnvDir + "/TerrainLayers";
        const string TreeDir = EnvDir + "/Ambient-Occlusion-Trees/Prefabs";
        const string PropsDir = EnvDir + "/Prefabs";
        internal const string TownDir = EnvDir + "/Town/Prefabs";

        // ---- Map size / resolution --------------------------------------------
        internal const float MapSize = 1000f;      // world metres (X and Z)
        const float MaxHeight = 90f;      // highest the terrain can be
        const int HeightRes = 513;        // heightmap samples (513 -> 512 cells)
        const int SplatRes = 512;         // splatmap (alphamap) resolution
        const int DetailRes = 512;        // grass detail resolution
        const int DetailPerPatch = 8;

        // Terrain layers, in the order they are added to the TerrainData. The order IS the
        // alphamap channel order used by ApplySplatmap / PaintTownGround, so it is written down
        // once here: 0 grass, 1 soil, 2 road (cobbles), 3 stone (slabs), 4 rock, 5 ghost.
        const string LAYER_GRASS = "layer_ground_grass";
        const string LAYER_SOIL = "layer_ground_soil";
        // Grey cracked rock: steep ground and the border mountains.
        const string LAYER_ROCK = "layer_ground_rock";
        // Town paving: large flat slabs - the town FLOOR.
        const string LAYER_STONE = "layer_ground_stones";
        // Sandy cobbles - the streets, the colonnade under the walls and the kerb.
        const string LAYER_ROAD = "layer_ground_road";
        // Invisible wrap layer (no diffuse texture). It lets the town floor carry a sixth
        // texture without adding a seventh alphamap channel to the TerrainData; the extra
        // floor textures are stacked through the material instead (see FloorPalette).
        const string LAYER_GHOST = "layer_ground_ghost";

        const int LAYER_COUNT = 6;
        const int L_GRASS = 0, L_SOIL = 1, L_ROAD = 2, L_STONE = 3, L_ROCK = 4, L_GHOST = 5;

        // Town / fortress (in the middle of the map)
        internal static readonly Vector2 Village = new Vector2(500f, 520f);
        internal const float PlazaRadius = 13f;    // paved market-square radius (m)
        internal const float RoadHalfWidth = 3.6f;
        // Nothing is grown or scattered this close to a road centre line. The road surface ends at
        // RoadHalfWidth, so the rest is a clean shoulder that keeps grass off the curves too.
        internal const float RoadClearance = RoadHalfWidth + 7f;
        internal const float RoadFloor = 24f;   // height the town + roads are flattened to (m)

        // ---- Town ground palette ------------------------------------------------
        // Inside the walls the floor is NOT grass: flat paving slabs (ground_stones) for the
        // open yards, sandy cobbles (ground_road) on every street, a cobbled colonnade running
        // along the inside of the walls and a dark dirt band (ground_soil) where the wall
        // stones meet the ground. All widths are in metres; they scale with the town.
        internal const float TownWayLip = 1.6f;   // road texture spills this far past the road edge
        internal const float TownWaySoft = 2.2f;  // then fades out over this width (a soft verge)
        internal const float TownCordon = 7.5f;   // ring road along the inside of the walls
        internal const float TownLane = 3.0f;     // lane along each house front, out from its walls
        internal const float TownPaveEdge = 1.4f; // soft edge of the town ground at the wall foot

        // Fortress footprint: world metres from the town centre to the wall centre line.
        // These are the BASE sizes; the town actually built is sized by TownAreaScale below,
        // so the design, the terrain plateau, the roads, the ground texture and the nature
        // scatter all grow together.
        internal const float FortHX = 78f;      // east / west walls
        internal const float FortHZ = 64f;      // north / south walls
        internal const float WallScale = 1.6f; // scale applied to the stone wall/tower pieces

        /// <summary>
        /// How much bigger the town is than the base footprint: 1 = base, 2 = twice as wide and
        /// deep (~4x the area). Prefab sizes never change - a bigger town simply has more
        /// walls, towers, gates, streets and houses.
        /// </summary>
        internal const float TownAreaScale = 2f;

        /// <summary>Actual half-width of the town (centre line to the east / west walls).</summary>
        internal static float TownHX => FortHX * TownAreaScale;

        /// <summary>Actual half-depth of the town (centre line to the north / south walls).</summary>
        internal static float TownHZ => FortHZ * TownAreaScale;

        // How far the approach roads run out of the two main (north / south) gates.
        internal const float RoadLen = 175f;

        // Player spawn: on the approach road just outside the north gate.
        internal static readonly Vector2 Spawn = new Vector2(500f, 520f + TownHZ + 9f);

        internal static Terrain _terrain;
        internal static TerrainData _td;
        internal static float _spacing;   // world metres per heightmap cell

        // Hill controls (read from Map Settings; default = current look).
        static float _hillHeight = 30f;
        static float _hillScale = 1f;

        // Large "rim" hills / mountains that ring the edges of the map.
        static bool _borderOn = true;      // on by default
        static float _borderHeight = 88f;  // tallest border crest (m)
        static float _borderRim = 235f;    // how far the mountains reach inward (m)

        public static bool DebugMode = true;

        // =========================================================================
        [MenuItem("MCP/Build Mystic Map (1km)")]
        public static void BuildFromMenu()
        {
            Build();
        }

        [MenuItem("MCP/Open Mystic Map scene")]
        public static void OpenSceneFromMenu()
        {
            if (System.IO.File.Exists(ScenePath))
                EditorSceneManager.OpenScene(ScenePath);
            else
                Debug.LogWarning("MysticMap scene not built yet. Use 'Build Mystic Map' first.");
        }

        /// <summary>
        /// Full build: a fresh map AND a fresh town.
        ///
        /// The town is a separate thing now (MCP > Town Builder). The map build only lays down
        /// the land, the roads and the nature; the town is then laid on top from the STORED
        /// town design (<see cref="TownDesignState"/>), so a town you edited or captured is
        /// exactly the town you get back - only when there is no stored design yet is the
        /// default town generated once.
        /// </summary>
        public static void Build()
        {
            Log("MCP Map Builder starting (this regenerates the land; the town comes from its stored design)...");
            EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();

            bool defaultTownGenerated = false;
            if (!TownDesignState.HasDesign())
            {
                TownDesignState.GenerateDefault(true);
                defaultTownGenerated = true;
                Log("No town design stored yet - a default fortress town was designed for this map.");
            }
            else
            {
                Log("Stored town design found (" + TownDesignState.PieceCount() +
                    " pieces, saved " + TownDesignState.LastSaved() + ") - keeping it.");
            }

            var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            try
            {
                GenerateTerrainAsset();
                CreateTerrainGameObject();
                CreateLightingAndFog();
                CreatePlayer();
                // The ground inside the walls is town paving, not grass. It is painted before the
                // town goes down so the paint can leave dirt rings round each building footprint.
                RepaintTownGround();
                TownDesignState.Apply();
                CreateScatter();
                Log("Terrain + content done.");
            }
            catch (System.Exception e)
            {
                Debug.LogError("MCP Map Builder failed: " + e);
            }

            SaveCurrentScene(newScene);
            Log("Mystic Map saved to " + ScenePath);
            if (defaultTownGenerated)
                Log("Tip: edit the town and run MCP > Town Builder > Capture the town design so the next map rebuild keeps it.");
            Log("Tip: press Play and walk with WASD (Shift to run). Use the MCP/Open menu to reopen.");
        }

        /// <summary>
        /// Rebuilds ONLY the town: the terrain, roads, grass, trees and props of the open scene
        /// are left exactly as they are and the stored town design is laid down again.
        /// Returns the number of pieces placed, or -1 when there is nothing to rebuild on.
        /// </summary>
        public static int RebuildTown()
        {
            var scene = EnsureMapSceneOpen();
            if (!scene.IsValid()) return -1;

            int placed = TownDesignState.Apply();
            if (placed < 0) return -1;

            TownDesignState.StampAnchor();
            SaveCurrentScene(scene);
            Log("Town rebuilt from its stored design (" + placed + " pieces). The map was not touched.");
            return placed;
        }

        /// <summary>
        /// Returns the open scene when it holds the map, otherwise opens the map scene.
        /// Returns an invalid scene (and logs a warning) when there is no map at all.
        /// </summary>
        internal static Scene EnsureMapSceneOpen()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid() && scene.isLoaded && FindSceneTerrain() != null) return scene;

            if (!System.IO.File.Exists(ScenePath))
            {
                Debug.LogWarning("[MysticMap] No map scene yet (" + ScenePath +
                                 "). Build the map first: MCP > Build Mystic Map (1km).");
                return default;
            }

            var opened = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (FindSceneTerrain() == null)
            {
                Debug.LogWarning("[MysticMap] No Terrain in the open scene. Build the map first.");
                return default;
            }
            return opened;
        }

        /// <summary>The town was changed in the scene: capture that look as the stored design.</summary>
        public static int CaptureTownDesign()
        {
            var town = GameObject.Find(TownDesignState.TownRootName);
            if (town == null)
            {
                Debug.LogWarning("[MysticMap] No " + TownDesignState.TownRootName +
                                 " in the open scene - there is nothing to capture.");
                return 0;
            }
            var list = TownDesignState.Capture(town.transform, true);
            TownDesignState.StampAnchor();
            Log("Town design captured (" + list.Count + " pieces) from the open scene.");
            return list.Count;
        }

        internal static void SaveCurrentScene(Scene scene)
        {
            if (!System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(ScenePath)))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // =========================================================================
        //  Terrain data generation
        // =========================================================================
        static void GenerateTerrainAsset()
        {
            if (System.IO.File.Exists(TerrainDataPath))
                AssetDatabase.DeleteAsset(TerrainDataPath);

            _td = new TerrainData();
            _td.heightmapResolution = HeightRes;
            _td.size = new Vector3(MapSize, MaxHeight, MapSize);
            _td.alphamapResolution = SplatRes;
            _td.baseMapResolution = 512;
            _td.SetDetailResolution(DetailRes, DetailPerPatch);
            _spacing = MapSize / (HeightRes - 1);

            _td.terrainLayers = new[]
            {
                LoadLayer(LAYER_GRASS),   // 0 - slime-green meadow grass
                LoadLayer(LAYER_SOIL),    // 1 - dark dirt under everything built
                LoadLayer(LAYER_ROAD),    // 2 - sandy cobbles: streets, colonnade and kerb
                LoadLayer(LAYER_STONE),   // 3 - flat paving slabs: the town floor
                LoadLayer(LAYER_ROCK),    // 4 - grey cracked rock: steep ground, mountains
                LoadLayer(LAYER_GHOST)    // 5 - invisible wrap for the top floor texture
            };

            ApplyHeights();
            ApplySplatmap();
            ApplyGrassDetails();
            ApplyTrees();

            AssetDatabase.CreateAsset(_td, TerrainDataPath);
            AssetDatabase.SaveAssets();
        }

        static float[,] _h2; // world-metre heights indexed [z, x]

        static void ApplyHeights()
        {
            _hillHeight = EditorPrefs.GetFloat("MM.HillHeight", 30f);
            _hillScale = Mathf.Max(0.2f, EditorPrefs.GetFloat("MM.HillScale", 1f));
            _borderOn = EditorPrefs.GetBool("MM.BorderHills", true);
            _borderHeight = Mathf.Clamp(EditorPrefs.GetFloat("MM.BorderHeight", 88f), 20f, 90f);
            _h2 = new float[HeightRes, HeightRes];
            float[,] hs = new float[HeightRes, HeightRes];
            for (int z = 0; z < HeightRes; z++)
            {
                float wz = z * _spacing;
                for (int x = 0; x < HeightRes; x++)
                {
                    float wx = x * _spacing;
                    float h = HeightWorld(wx, wz);
                    _h2[z, x] = h;
                    hs[z, x] = Mathf.Clamp01(h / MaxHeight);
                }
            }
            _td.SetHeights(0, 0, hs);
        }

        // Height at a world position, in metres:
        // a flat village centre, gentle rolling countryside around it, and a ring
        // of large border hills/mountains that rise up near the edges of the map so
        // the whole scene sits in a natural valley framed by an interesting skyline.
        static float HeightWorld(float x, float z)
        {
            float h = RollingField(x, z);            // gentle interior hills

            if (_borderOn)
            {
                // Border mountains gradually take over near an edge of the map.
                float a = BorderAmount(x, z);
                if (a > 0f) h = Mathf.Lerp(h, MountainHeight(x, z), a);
            }

            // Flatten a level plateau across the whole fortress so its walls, towers and
            // buildings sit on even ground, and flatten corridors along every road so the
            // long approach roads to the gates run level across the rolling countryside.
            {
                float ax = Mathf.Abs(x - Village.x) - (TownHX + 10f);
                float az = Mathf.Abs(z - Village.y) - (TownHZ + 10f);
                float dRect = Mathf.Max(ax, az);                  // <= 0 inside the plateau
                float rectBlend = Mathf.Clamp01(1f - dRect / 26f);

                float rd = RoadDistance(x, z);
                float rdBlend = Mathf.Clamp01(1f - Mathf.Max(0f, rd - (RoadHalfWidth + 5f)) / 22f);

                float flatBlend = Mathf.Max(rectBlend, rdBlend);
                if (flatBlend > 0.01f)
                    h = Mathf.Lerp(h, RoadFloor, Mathf.Clamp01(flatBlend));
            }

            return Mathf.Clamp(h, 12f, MaxHeight - 1f);
        }

        // Gentle rolling countryside that covers the middle of the map.
        static float RollingField(float x, float z)
        {
            float sx = x * 0.0024f * _hillScale + 31f;
            float sy = z * 0.0024f * _hillScale + 9f;
            float mx = x * 0.0080f * _hillScale + 7f;
            float my = z * 0.0080f * _hillScale + 19f;
            float hx = x * 0.0200f * _hillScale + 53f;
            float hy = z * 0.0200f * _hillScale + 3f;
            float s =
                Mathf.PerlinNoise(sx, sy) * 0.55f +
                Mathf.PerlinNoise(mx, my) * 0.30f +
                Mathf.PerlinNoise(hx, hy) * 0.15f;
            s = Mathf.Clamp01(s);
            return 20f + _hillHeight * Mathf.Pow(s, 1.1f);   // gentle rolling hills
        }

        // How strongly the border hills should appear at (x, z): 1 right against an
        // edge, fading smoothly to 0 once we are far enough into the map. Each side
        // has its own jittered reach so the ring around the map is irregular.
        static float BorderAmount(float x, float z)
        {
            float aL = BorderSide(x,           z, 2.3f, 0.6f);  // near x = 0
            float aR = BorderSide(MapSize - x, z, 9.7f, 3.1f);  // near x = MapSize
            float aB = BorderSide(z,           x, 5.5f, 1.7f);  // near z = 0
            float aT = BorderSide(MapSize - z, x, 13.1f, 4.9f);  // near z = MapSize
            return Mathf.Max(Mathf.Max(aL, aR), Mathf.Max(aB, aT));
        }

        // d = distance in metres from one straight edge of the map. 'along' runs
        // along that edge and jitters the reach, giving every side its own shape.
        static float BorderSide(float d, float along, float seedA, float seedB)
        {
            float reach = _borderRim * (0.6f + 0.8f * Mathf.PerlinNoise(along * 0.004f + seedA, seedB));
            float a = 1f - d / Mathf.Max(20f, reach);
            a = Mathf.Clamp01(a);
            return a * a * (3f - 2f * a);          // smooth falloff from the edge
        }

        // Large hills/mountains ringing the map: tall craggy peaks with lower passes
        // between them, so the border is an interesting skyline, not a flat wall.
        static float MountainHeight(float x, float z)
        {
            // Broad bumps decide where tall peaks and open passes sit.
            float broad = Mathf.Clamp01(
                Mathf.PerlinNoise(x * 0.0042f + 17f, z * 0.0042f + 27f) * 0.55f +
                Mathf.PerlinNoise(x * 0.0085f + 91f, z * 0.0085f + 55f) * 0.45f);

            // Ridged component sharpens the crests (instead of smooth dunes).
            float fine = Mathf.PerlinNoise(x * 0.024f + 3f, z * 0.024f + 8f);
            float ridge = Mathf.Pow(Mathf.Abs(2f * fine - 1f), 1.5f);

            // Small rocky detail so large slopes aren't perfectly smooth.
            float micro = Mathf.PerlinNoise(x * 0.052f + 60f, z * 0.052f + 41f);

            float t = Mathf.Clamp01(broad * 0.60f + ridge * 0.32f + micro * 0.08f);
            return Mathf.Lerp(38f, _borderHeight, t);
        }

        static float SlopeAt(int hx, int hz)
        {
            int x0 = Mathf.Max(0, hx - 1), x1 = Mathf.Min(HeightRes - 1, hx + 1);
            int z0 = Mathf.Max(0, hz - 1), z1 = Mathf.Min(HeightRes - 1, hz + 1);
            float riseX = Mathf.Abs(_h2[hz, x1] - _h2[hz, x0]);
            float riseZ = Mathf.Abs(_h2[z1, hx] - _h2[z0, hx]);
            float rise = Mathf.Max(riseX, riseZ);
            float run = 2f * _spacing;
            return Mathf.Atan2(rise, Mathf.Max(run, 0.01f)) * Mathf.Rad2Deg;
        }
        static void ApplySplatmap()
        {
            int res = _td.alphamapResolution;
            float cell = MapSize / res;

            // The alphamap MUST match the terrain's own layer count - an existing / older
            // terrain may still ship 4 layers (rock and stone swapped), so the paint is aimed
            // at whichever layer holds each texture instead of a fixed index.
            var layers = _td.terrainLayers;
            int n = (layers != null && layers.Length > 0) ? layers.Length : LAYER_COUNT;
            int iGrass = LayerIndex(layers, LAYER_GRASS, L_GRASS, n);
            int iSoil = LayerIndex(layers, LAYER_SOIL, L_SOIL, n);
            int iRoad = LayerIndex(layers, LAYER_ROAD, L_ROAD, n);
            int iStone = LayerIndex(layers, LAYER_STONE, L_STONE, n);
            int iRock = LayerIndex(layers, LAYER_ROCK, L_ROCK, n);
            int iGhost = LayerIndex(layers, LAYER_GHOST, L_GHOST, n);

            float[,,] amap = new float[res, res, n];
            TownGroundPalette pal = BuildTownPalette();

            for (int z = 0; z < res; z++)
            {
                float wz = (z + 0.5f) * cell;
                int hz = Mathf.Clamp(Mathf.RoundToInt(wz / _spacing), 0, HeightRes - 1);
                for (int x = 0; x < res; x++)
                {
                    float wx = (x + 0.5f) * cell;
                    int hx = Mathf.Clamp(Mathf.RoundToInt(wx / _spacing), 0, HeightRes - 1);
                    float rd = RoadDistance(wx, wz);
                    float slope = SlopeAt(hx, hz);

                    float steep = Mathf.Clamp01((slope - 16f) / 42f);
                    float g = 1f - steep;
                    float soil = 0.4f * steep;
                    float rock = 0.6f * steep;

                    // Broad dirt blotches so the open countryside isn't one flat green sheet.
                    float blot = Mathf.PerlinNoise(wx * 0.012f + 3f, wz * 0.012f + 9f);
                    if (blot > 0.82f) { soil += 0.5f; g -= 0.5f; }

                    // Inside the walls the town floor is town GROUND with a network of road
                    // texture streets through it. The ground is a patchwork of textures (dirt,
                    // slabs, cobbles, rock) so it has life and depth; the streets are painted
                    // on top of the mix and stay clean lines between the houses.
                    float lGrass = g, lSoil = soil, lRoad = 0f, lStone = 0f, lRock = rock, lGhost = 0f;
                    TownSurface(wx, wz, rd, pal, out float roadP, out float floorP);
                    if (floorP > 0f)
                    {
                        float k = floorP;

                        // The town floor is a PATCHWORK of textures, not one flat sheet: dirt,
                        // slabs, cobbles and grey rock spread over it in broad, organic patches
                        // (see FloorPatch). Streets are then painted on top of the mix.
                        float heave = Mathf.PerlinNoise(wx * 0.13f + 617f, wz * 0.13f + 733f);
                        float fGround = Mathf.Max(0f, 1f - roadP);

                        // One texture per patch, jittered per square metre so no two patches
                        // land on the same grid even though the regions are aligned.
                        float gw = wx * 0.20f + Mathf.PerlinNoise(wx * 0.71f, wz * 0.67f) * 0.55f;
                        float gh = wz * 0.20f + Mathf.PerlinNoise(wx * 0.63f + 31f, wz * 0.73f + 17f) * 0.55f;
                        float keep = FloorPatch(gw, gh);

                        float txF = 1f - ProcNoiseLite.TileNoise(gw * 0.55f, gh * 0.55f, 5.5f);
                        float stF = 1f - ProcNoiseLite.TileNoise(gw * 0.47f + 11f, gh * 0.47f + 7f, 4.5f);
                        float cgF = 1f - ProcNoiseLite.TileNoise(gw * 0.62f + 23f, gh * 0.62f + 29f, 6.5f);

                        txF = Mathf.Clamp01((txF - 0.80f) / 0.12f) * keep;
                        stF = Mathf.Clamp01((stF - 0.82f) / 0.12f) * keep;
                        cgF = Mathf.Clamp01((cgF - 0.84f) / 0.12f) * keep;

                        float patchSum = txF + stF + cgF;
                        if (patchSum > 1f) { txF /= patchSum; stF /= patchSum; cgF /= patchSum; patchSum = 1f; }
                        float px = Mathf.Max(0f, fGround - patchSum);

                        // The base ground of each patch: mostly dirt, the odd grassed corner,
                        // and bare rock where the ground has broken up.
                        float yardGrass = Mathf.Clamp01((heave - 0.84f) / 0.16f) * px * 0.32f;
                        float yardRock = Mathf.Clamp01((heave - 0.90f) / 0.10f) * px * 0.20f;

                        float baseG = Mathf.Max(0f, lGrass) * (1f - k) + yardGrass * k;
                        float baseR = lRock * (1f - k) + yardRock * k;
                        float baseS = lSoil * (1f - k) + Mathf.Max(0f, px - yardGrass - yardRock) * k;

                        // Spread the dirt patches out by mixing the base ground into the
                        // neighbouring texture patches, so they are a mix rather than stamping.
                        float b0 = 0.45f * (1f - keep), b1 = 0.30f * (1f - keep), b2 = 0.25f * (1f - keep);

                        lGrass = baseG;
                        lSoil = baseS * keep + baseS * b1 + baseS * b2;
                        lRock = baseR;
                        lStone = (stF + baseS * b0) * k;
                        lRoad += (cgF + baseS * b1) * k;
                        lGhost = (txF + baseS * b2) * k;
                    }

                    float road = Mathf.Clamp01((RoadHalfWidth + 1.5f - rd) / 1.5f);
                    float remain = Mathf.Clamp01(1f - road);
                    AddWeight(amap, z, x, iGrass, Mathf.Max(0f, lGrass) * remain);
                    AddWeight(amap, z, x, iSoil, Mathf.Max(0f, lSoil) * remain);
                    AddWeight(amap, z, x, iRoad, Mathf.Max(0f, lRoad) * remain + road);
                    AddWeight(amap, z, x, iStone, Mathf.Max(0f, lStone) * remain);
                    AddWeight(amap, z, x, iRock, Mathf.Max(0f, lRock) * remain);
                    AddWeight(amap, z, x, iGhost, Mathf.Max(0f, lGhost) * remain);

                    float sum = 0f;
                    for (int k = 0; k < n; k++) sum += amap[z, x, k];
                    if (sum > 0.0001f)
                        for (int k = 0; k < n; k++) amap[z, x, k] /= sum;
                    else
                        amap[z, x, 0] = 1f;
                }
            }
            _td.SetAlphamaps(0, 0, amap);
        }

        /// <summary>Index of the terrain layer holding <paramref name="layerName"/>, or -1.</summary>
        static int LayerIndex(TerrainLayer[] layers, string layerName, int fallback, int count)
        {
            if (layers != null)
                for (int i = 0; i < layers.Length; i++)
                    if (layers[i] != null && layers[i].name == layerName) return i;
            return fallback < count ? fallback : -1;
        }

        /// <summary>Adds colour weight to one alphamap channel (ignores textures the terrain lacks).</summary>
        static void AddWeight(float[,,] amap, int z, int x, int channel, float w)
        {
            if (channel >= 0) amap[z, x, channel] += w;
        }

        // ---- Town floor patchwork -------------------------------------------------

        /// <summary>
        /// The repeating patch mask of the town floor (input is a jittered metre grid, so the
        /// 5 x 5 m patches never line up into a visible grid). Returns 0 inside a texture patch
        /// and 1 outside it, so the caller can carve a differently seeded blob out of every
        /// patch - a patchwork of ground textures instead of one flat sheet.
        ///
        /// Patches are 5 x 5 m; a 2 x 2 block that a patch lands in can also be left as plain
        /// ground, which breaks up even the slowly-drifting patch rhythms.
        /// </summary>
        static float FloorPatch(float gridX, float gridZ)
        {
            float cell = 5f;
            int tx = Mathf.FloorToInt(gridX / cell);
            int tz = Mathf.FloorToInt(gridZ / cell);

            float blockHash = Hash01(tx / 2, tz / 2);
            if (blockHash > 0.80f) return 0.5f;              // this block stays ground

            float lx = gridX / cell - tx;
            float lz = gridZ / cell - tz;

            float jx = (Hash01(tx * 3 + 1, tz * 5 + 2) - 0.5f) * 0.35f;
            float jz = (Hash01(tx * 7 + 4, tz * 11 + 3) - 0.5f) * 0.35f;

            float dx = (lx - 0.5f - jx) / 0.42f;
            float dz = (lz - 0.5f - jz) / 0.42f;
            float r = Mathf.Sqrt(dx * dx + dz * dz);

            // A deterministic wobble so the patches are blobs, not circles.
            float wobble = (Hash01(tx * 13 + 9, tz * 17 + 5) - 0.5f) * 0.30f;
            return Mathf.Clamp01((r - 1f + wobble) / 0.30f);
        }

        /// <summary>Stable pseudo-random value in 0..1 for a pair of integers.</summary>
        static float Hash01(int a, int b)
        {
            unchecked
            {
                uint h = (uint)(a * 73856093) ^ (uint)(b * 19349663);
                h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                return (h & 0x00FFFFFF) / 16777215f;
            }
        }

        /// <summary>
        /// Tileable value noise in 0..1 (wraps every <paramref name="period"/> cells), so the
        /// fine texture blobs on the town floor repeat seamlessly instead of showing seams.
        /// </summary>
        internal static class ProcNoiseLite
        {
            public static float TileNoise(float x, float z, float period)
            {
                float fx = x * period, fz = z * period;
                int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
                float u = Smooth(fx - x0), v = Smooth(fz - z0);

                int x1 = Mathf.RoundToInt(Mathf.Repeat(x0 + 1, period));
                int z1 = Mathf.RoundToInt(Mathf.Repeat(z0 + 1, period));
                x0 = Mathf.RoundToInt(Mathf.Repeat(x0, period));
                z0 = Mathf.RoundToInt(Mathf.Repeat(z0, period));

                float a = Hash01(x0, z0), b = Hash01(x1, z0);
                float c = Hash01(x0, z1), d = Hash01(x1, z1);

                return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
            }

            static float Smooth(float t) => t * t * (3f - 2f * t);
        }

        // ---- Town floor palette -------------------------------------------------

        /// <summary>
        /// Everything the town-floor paint needs to know about the town's shape: the wall line,
        /// the half width of the stone drum (so the dirt band hugs the wall foot and not its
        /// centre line), and the world footprints of the buildings standing on the floor.
        /// </summary>
        internal struct TownGroundPalette
        {
            public float hx, hz;   // wall centre line, metres from the town centre
            public float drum;     // half width of the stone wall drum
            public List<Bounds> buildings;
        }

        static readonly string[] WallDrumNames = { "Wall_part1", "Wall_part2" };

        /// <summary>
        /// Reads the wall width and the building footprints out of the stored town design, so the
        /// ground paint follows the town that is actually standing - and stays correct when the
        /// town is scaled up or the design is re-applied.
        /// </summary>
        internal static TownGroundPalette BuildTownPalette()
        {
            var pal = new TownGroundPalette { hx = TownHX, hz = TownHZ, drum = 1.6f };
            try
            {
                var pieces = TownDesignState.Pieces;
                if (pieces == null || pieces.Count == 0) return pal;

                pal.buildings = new List<Bounds>();
                bool gotDrum = false;
                foreach (var p in pieces)
                {
                    if (p == null || string.IsNullOrEmpty(p.prefab)) continue;

                    if (!gotDrum && p.group == "Walls")
                        foreach (var n in WallDrumNames)
                            if (p.prefab.IndexOf("/" + n + ".prefab", System.StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var wp = AssetDatabase.LoadAssetAtPath<GameObject>(p.prefab);
                                if (wp != null)
                                {
                                    Bounds wb = PlannedBoundsFast(wp, 0f, 0f, 0f, p.scale, 0f);
                                    // The drum is the thinner of the two horizontal extents.
                                    float thin = Mathf.Min(wb.extents.x, wb.extents.z);
                                    if (thin > 0.05f) pal.drum = Mathf.Clamp(thin, 0.8f, 5f);
                                    gotDrum = true;
                                }
                            }

                    if (p.group != "Buildings" && p.group != "Houses") continue;
                    var pf = AssetDatabase.LoadAssetAtPath<GameObject>(p.prefab);
                    if (pf == null) continue;
                    pal.buildings.Add(PlannedBoundsFast(
                        pf, p.pos.x, p.pos.z, p.yaw, p.scale, p.pos.y));
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[MysticMap] Town ground palette: " + e.Message);
                pal.buildings = null;
            }
            return pal;
        }
        /// <summary>
        /// Paints the town FLOOR as <paramref name="roadP"/> (road texture) and the rest as
        /// <paramref name="floorP"/>. The town is a NETWORK OF ROAD LINES: every street, the
        /// plaza, the ring under the walls and the lane along each house front are road texture;
        /// everything else in town is ground. The streets are what carries the traffic, so the
        /// ground never uses a cobble texture itself - otherwise the lines would vanish into it.
        /// </summary>
        static void TownSurface(float wx, float wz, float rd, TownGroundPalette pal,
                                out float roadP, out float floorP)
        {
            roadP = floorP = 0f;
            if (pal.hx <= 1f || pal.hz <= 1f) return;

            float tax = Mathf.Abs(wx - Village.x) - pal.hx;
            float taz = Mathf.Abs(wz - Village.y) - pal.hz;
            float outside = Mathf.Max(tax, taz);   // < 0 inside the wall centre line
            float inner = -outside;                // distance in from the wall centre line

            if (outside > TownCordon) return;      // well outside the walls: leave the meadow be

            // ---- the street plan, both sides of the wall ----
            // 1) The roads themselves. WigglyRoad only sways by up to ~half its bend, so the
            //    avenue between the houses stays well inside the walls and reads as a clean line.
            roadP = Mathf.Clamp01((RoadHalfWidth + TownWayLip - rd) / TownWaySoft);

            // 2) Market square: the middle of the town is paved, it is where the streets meet.
            roadP = Mathf.Max(roadP, Mathf.Clamp01((PlazaRadius + 6f - rd) / 6f));

            // 3) Ring road: the lane running under the wall all the way round, so houses line a
            //    street instead of paving running straight into the battlements.
            float ring = Mathf.Clamp01((TownCordon - inner) / TownWaySoft);
            float ringNoise = 0.75f + 0.5f * Mathf.PerlinNoise(wx * 0.06f + 13f, wz * 0.06f + 7f);
            roadP = Mathf.Max(roadP, ring * ringNoise);

            // 4) A lane along the front of every building, so each house has street access and
            //    the road lines visibly run from one house to the next.
            if (pal.buildings != null)
            {
                for (int i = 0; i < pal.buildings.Count; i++)
                {
                    Bounds b = pal.buildings[i];
                    float dx = Mathf.Max(0f, Mathf.Abs(wx - b.center.x) - b.extents.x);
                    float dz = Mathf.Max(0f, Mathf.Abs(wz - b.center.z) - b.extents.z);
                    float lane = Mathf.Clamp01((TownLane - Mathf.Sqrt(dx * dx + dz * dz)) / TownLane);
                    if (lane > roadP) roadP = lane;
                }
            }

            // ---- where the town floor ends ----
            if (outside > 0f)
            {
                // KERB. The town stops on the wall line: road texture right up under the wall
                // stones (the ring road already covers that), then it gives way to the meadow.
                float edge = Mathf.Clamp01((pal.drum + TownPaveEdge - outside) / TownPaveEdge);
                floorP = Mathf.Clamp01((TownCordon - outside) / 2.4f) * 0.9f * (1f - edge * 0.5f);
                roadP = Mathf.Max(roadP, edge * 0.5f);
                return;
            }

            // Inside the walls the whole floor is town ground, streets on top of it.
            floorP = 1f;
            roadP = Mathf.Clamp01(roadP);
        }

        /// <summary>Repaints just the town floor of the open scene's terrain (no roads, no props).</summary>
        internal static void RepaintTownGround()
        {
            var terrain = FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("[MysticMap] No Terrain in the open scene - nothing to repaint.");
                return;
            }
            UseSceneTerrain(terrain);
            ApplySplatmap();
            EditorUtility.SetDirty(_td);
            AssetDatabase.SaveAssets();
            Log("Town floor repainted (texture patchwork: dirt + slabs + cobbles + rock, streets on top).");
        }




        // ---- Road network -------------------------------------------------------
        static List<Vector2[]> _roadPolys;

        /// <summary>
        /// Hands the road network over to another tool (the town / world extenders). Everything
        /// that is generated from "distance to the nearest road" - the ground splat, the road
        /// curves carved into the heightmap, the grass, the trees and the scatter - then all use
        /// this one network, so nothing can end up built for a road shape that no longer exists.
        /// </summary>
        internal static void UseRoadPolys(List<Vector2[]> roads)
        {
            _roadPolys = roads;
        }

        static void EnsureRoads()
        {
            if (_roadPolys != null) return;

            var roads = new List<Vector2[]>();

            // North / south main road through the two gates (long approach both ways).
            // The approach is a wandering curve instead of a ruler line: it snakes out of the
            // north gate, past the village and out of the south gate, but it is pinned to the
            // gate points at both ends so the gates themselves line up with it.
            AddTownRoads(roads);

            _roadPolys = roads;
        }

        /// <summary>The road network of the town area itself (also used by the town extender).</summary>
        internal static void AddTownRoads(List<Vector2[]> roads)
        {
            roads.Add(WigglyRoad(
                new Vector2(Village.x, Village.y - RoadLen),
                new Vector2(Village.x, Village.y + RoadLen),
                62f, 0.9f));

            // Interior east / west avenue linking the market square to the side quarters.
            roads.Add(WigglyRoad(
                new Vector2(Village.x - (TownHX - 8f), Village.y),
                new Vector2(Village.x + (TownHX - 8f), Village.y),
                24f, 2.3f));

            // Cross streets through the quarters north and south of the avenue, so the town reads
            // as a grid of streets with houses between them instead of one road through a field.
            // They stop short of the walls (the ring road runs along the inside of those).
            float runX = TownHX - 16f;
            float[] lanes = { 0.42f, 0.74f };
            for (int i = 0; i < lanes.Length; i++)
            {
                float off = (TownHZ - 12f) * lanes[i];
                roads.Add(WigglyRoad(
                    new Vector2(Village.x - runX, Village.y + off),
                    new Vector2(Village.x + runX, Village.y + off),
                    14f, 1.7f + i * 2.1f));
                roads.Add(WigglyRoad(
                    new Vector2(Village.x - runX, Village.y - off),
                    new Vector2(Village.x + runX, Village.y - off),
                    14f, 3.9f + i * 2.1f));
            }

            // Short north / south links: side streets joining those cross streets to the main
            // road, so there are through-routes past the houses and not just parallel lines.
            float linkZ = (TownHZ - 12f) * 0.58f;
            float[] links = { -0.55f, 0.55f };
            for (int i = 0; i < links.Length; i++)
            {
                float off = runX * links[i];
                roads.Add(WigglyRoad(
                    new Vector2(Village.x + off, Village.y - linkZ),
                    new Vector2(Village.x + off, Village.y + linkZ),
                    10f, 5.1f + i * 1.9f));
            }
        }

        /// <summary>
        /// Builds one baked road as a wiggly poly-line from <paramref name="a"/> to
        /// <paramref name="b"/>: the road swings sideways by up to <paramref name="bend"/> metres
        /// (a couple of slow curves), fading to nothing at both ends so it still meets its gates,
        /// the village and anything else that expects that exact spot.
        /// </summary>
        internal static Vector2[] WigglyRoad(Vector2 a, Vector2 b, float bend, float phase)
        {
            Vector2 dir = b - a;
            float len = dir.magnitude;
            if (len < 1f) return new[] { a, b };
            dir /= len;
            var side = new Vector2(-dir.y, dir.x);

            int steps = Mathf.Max(8, Mathf.CeilToInt(len / 2.5f));
            var pts = new Vector2[steps + 1];

            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;

                // Envelope: 0 at both ends, 1 in the middle - the gates stay put.
                float env = Mathf.Sin(t * Mathf.PI);
                env *= env;

                // A couple of slow curves, a middle wiggle and a long lazy one: enough swings that
                // the road reads as properly curly, but none so tight that it doubles back.
                float sway = Mathf.Sin(phase + t * Mathf.PI * 2f) * 0.46f
                           + Mathf.Sin(phase * 1.9f + t * Mathf.PI * 6f) * 0.32f
                           + Mathf.Sin(phase * 0.7f + t * Mathf.PI * 3f) * 0.14f
                           + Mathf.Sin(phase * 2.6f + t * Mathf.PI * 10f) * 0.08f;

                pts[i] = a + dir * (len * t) + side * (sway * bend * env);
            }

            return pts;
        }

        internal static float RoadDistance(float x, float z)
        {
            EnsureRoads();
            Vector2 p = new Vector2(x, z);

            float toCenter = Vector2.Distance(p, Village);
            if (toCenter < PlazaRadius) return 0f;
            float best = toCenter - PlazaRadius;

            for (int k = 0; k < _roadPolys.Count; k++)
            {
                Vector2[] pts = _roadPolys[k];
                for (int i = 1; i < pts.Length; i++)
                    best = Mathf.Min(best, DistToSeg(pts[i - 1], pts[i], p));
            }
            return best;
        }

        static float DistToSeg(Vector2 a, Vector2 b, Vector2 p)
        {
            Vector2 ab = b - a;
            float len = ab.sqrMagnitude;
            float t = len > 0.0001f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len) : 0f;
            return Vector2.Distance(a + ab * t, p);
        }
        // ---- Grass detail (dense ground grass rendered by the Terrain, cheap + auto-culled)
        static void ApplyGrassDetails()
        {
            ApplyGrassDetails(null);
        }

        /// <summary>
        /// Writes the terrain grass detail. <paramref name="roads"/> is the road network the grass
        /// must stay clear of: pass the one that was just baked, or null to use the builder's own.
        /// </summary>
        internal static void ApplyGrassDetails(List<Vector2[]> roads)
        {
            if (roads != null) _roadPolys = roads;

            float density = EditorPrefs.GetFloat("MM.GrassDensity", 1f);
            float scale = EditorPrefs.GetFloat("MM.GrassScale", 1f);

            Texture2D shortTex = MakeGrassTexAsset("grass_short", 0.5f, 41);
            Texture2D midTex = MakeGrassTexAsset("grass_med", 0.75f, 87);
            Texture2D tallTex = MakeGrassTexAsset("grass_tall", 1.05f, 199);

            _td.detailPrototypes = new[]
            {
                MakeGrassProto(shortTex, 0.2f * scale, 0.5f * scale, 0.35f, 0.6f),
                MakeGrassProto(midTex, 0.5f * scale, 0.9f * scale, 0.45f, 0.7f),
                MakeGrassProto(tallTex, 0.9f * scale, 1.6f * scale, 0.5f, 0.9f)
            };

            int res = DetailRes;
            float cell = MapSize / res;
            long total0 = 0, total1 = 0, total2 = 0;

            int[,] c0 = new int[res, res];
            int[,] c1 = new int[res, res];
            int[,] c2 = new int[res, res];
            for (int z = 0; z < res; z++)
            {
                float wz = (z + 0.5f) * cell;
                int hz = Mathf.Clamp(Mathf.RoundToInt(wz / _spacing), 0, HeightRes - 1);
                for (int x = 0; x < res; x++)
                {
                    float wx = (x + 0.5f) * cell;
                    float f = GrassFactor(wx, wz, hz);

                    // A cell that a road only clips in a corner still has "no grass" written on the
                    // cell as a whole, so a curvy road can never grow grass along its edge: the
                    // whole cell (centre + corners) is tested, not just the centre.
                    if (f > 0f && TouchesRoad(wx, wz, cell * 0.5f)) f = 0f;

                    // Dense field everywhere (short grass is the base layer).
                    int n0 = Mathf.RoundToInt(f * density * 13f);
                    // Medium blades, mostly in lush zones.
                    int n1 = Mathf.RoundToInt(Mathf.Max(0f, f - 0.25f) * density * 10f);
                    // Tall rye only in clumped patches -> natural variation in height.
                    float patch = Mathf.PerlinNoise(wx * 0.005f + 51f, wz * 0.005f + 12f);
                    int n2 = patch > 0.52f ? Mathf.RoundToInt(f * density * 9f) : 0;

                    c0[z, x] = n0; c1[z, x] = n1; c2[z, x] = n2;
                    total0 += n0; total1 += n1; total2 += n2;
                }
            }

            _td.SetDetailLayer(0, 0, 0, c0);
            _td.SetDetailLayer(0, 0, 1, c1);
            _td.SetDetailLayer(0, 0, 2, c2);
            Log(string.Format("Grass detail: short={0:N0}  med={1:N0}  tall={2:N0}", total0, total1, total2));
        }

        static float DistanceToSpawn(float x, float z) => Vector2.Distance(new Vector2(x, z), Spawn);

        /// <summary>
        /// True when any part of the detail cell around (<paramref name="x"/>, <paramref name="z"/>)
        /// is within <see cref="RoadClearance"/> of a road. A cell is a whole little quad of
        /// ground, so checking its corners as well as its middle is what keeps grass off a curvy
        /// road (a diagonal road can cross a cell whose centre is clear).
        /// </summary>
        static bool TouchesRoad(float x, float z, float half)
        {
            if (RoadDistance(x, z) < RoadClearance) return true;
            if (RoadDistance(x - half, z) < RoadClearance) return true;
            if (RoadDistance(x + half, z) < RoadClearance) return true;
            if (RoadDistance(x, z - half) < RoadClearance) return true;
            if (RoadDistance(x, z + half) < RoadClearance) return true;

            // The corners catch a road that cuts only across a corner of the cell.
            float c = half * 0.7071f;
            if (RoadDistance(x - c, z - c) < RoadClearance) return true;
            if (RoadDistance(x + c, z - c) < RoadClearance) return true;
            if (RoadDistance(x - c, z + c) < RoadClearance) return true;
            if (RoadDistance(x + c, z + c) < RoadClearance) return true;
            return false;
        }

        // Returns a grass-field density factor for a world position (0 = no grass).
        static float GrassFactor(float x, float z, int hz)
        {
            // Roads and the village/town stay clear of grass (plus a clean shoulder).
            float rd = RoadDistance(x, z);
            if (rd < RoadClearance) return 0f;
            if (Vector2.Distance(new Vector2(x, z), Village) < PlazaRadius + 8f) return 0f;

            // The whole fortress interior is kept clear of meadow grass (compact town floor).
            float tax = Mathf.Abs(x - Village.x) - (TownHX + 4f);
            float taz = Mathf.Abs(z - Village.y) - (TownHZ + 4f);
            if (tax < 0f && taz < 0f) return 0f;

            // Broad lushness: some big areas are thick meadows, others thinner.
            float lush = Mathf.PerlinNoise(x * 0.0011f + 3f, z * 0.0011f + 7f);
            // Fine clumping so the field reads as natural tufts, not a grid.
            float clump = Mathf.PerlinNoise(x * 0.006f + 40f, z * 0.006f + 20f);

            float f = Mathf.Lerp(0.55f, 1.15f, lush);
            f *= Mathf.Lerp(0.55f, 1.35f, clump);

            // Still grow under/around trees (this field covers the whole map except the town).
            int hx = Mathf.Clamp(Mathf.RoundToInt(x / _spacing), 0, HeightRes - 1);
            if (_h2 != null)
            {
                float slope = SlopeAt(hx, hz);
                f *= Mathf.Lerp(1f, 0.25f, Mathf.Clamp01((slope - 28f) / 45f)); // less on very steep cliffs
            }

            // Dithering noise so individual cells vary (some dense, some lighter).
            float dith = Mathf.PerlinNoise(x * 0.05f + 90f, z * 0.05f + 61f);
            f *= 0.45f + 0.8f * dith;

            return Mathf.Clamp(f, 0f, 2.2f);
        }


        static DetailPrototype MakeGrassProto(Texture2D tex, float minH, float maxH, float minW, float maxW)
        {
            return new DetailPrototype
            {
                renderMode = DetailRenderMode.Grass,
                prototypeTexture = tex,
                healthyColor = new Color(0.42f, 0.72f, 0.26f, 1f),
                dryColor = new Color(0.55f, 0.72f, 0.30f, 1f),
                noiseSpread = 0.12f,
                minHeight = minH, maxHeight = maxH,
                minWidth = minW, maxWidth = maxW,
                useInstancing = true
            };
        }

        // Generates a simple soft tuft-of-blades grass texture and stores it as an asset.
        static Texture2D MakeGrassTexAsset(string name, float bladeH, int rngSeed)
        {
            int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var cols = new Color[size * size];
            var rand = new System.Random(rngSeed);

            int blades = 26;
            for (int b = 0; b < blades; b++)
            {
                float bx = 8f + (float)(rand.NextDouble()) * (size - 16f);
                float hgt = bladeH * size * (0.6f + (float)rand.NextDouble() * 0.8f);
                float bend = ((float)rand.NextDouble() - 0.5f) * 6f;
                float startW = 1.4f + (float)rand.NextDouble() * 1.6f;
                for (int y = 0; y < (int)hgt; y++)
                {
                    int iy = size - 1 - y;
                    if (iy < 0 || iy >= size) continue;
                    float t = y / hgt;
                    float cx = bx + bend * t;
                    float halfW = Mathf.Lerp(startW, 0.3f, t);
                    int x0 = Mathf.Clamp((int)(cx - halfW), 0, size - 1);
                    int x1 = Mathf.Clamp((int)(cx + halfW), 0, size - 1);
                    for (int px = x0; px <= x1; px++)
                    {
                        int idx = iy * size + px;
                        cols[idx].r = cols[idx].g = cols[idx].b = 1f;
                        cols[idx].a = Mathf.Max(cols[idx].a, 1f);
                    }
                }
            }
            tex.SetPixels(cols);
            tex.Apply(false, true);

            string path = GeneratedDir + "/" + name + ".png";
            try { System.IO.File.WriteAllBytes(path, tex.EncodeToPNG()); }
            catch (System.Exception e) { Debug.LogWarning("Could not write " + path + ": " + e); }
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        // ---- Trees (placed as Terrain trees: auto-LOD + distance culling by Unity) ----
        static readonly string[] TreeNames =
        {
            "Oak_tree1", "Oak_tree3", "Deciduous_tree1", "Deciduous_tree4",
            "Pine_tree1", "Pine_tree2", "Birch_tree1", "Willow_tree1"
        };

        static void ApplyTrees()
        {
            var prototypes = new List<TreePrototype>();
            foreach (var name in TreeNames)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TreeDir + "/" + name + ".prefab");
                if (prefab != null) prototypes.Add(new TreePrototype { prefab = prefab });
                else Debug.LogWarning("Missing tree prefab: " + name);
            }
            if (prototypes.Count == 0) { Log("No tree prototypes found, skipping trees."); return; }
            _td.treePrototypes = prototypes.ToArray();

            const int target = 1600;
            const int margin = 40;
            var rand = new System.Random(1234);
            var list = new List<TreeInstance>();

            int attempts = 0;
            while (list.Count < target && attempts < target * 10)
            {
                attempts++;
                float x = margin + (float)(rand.NextDouble()) * (MapSize - margin * 2f);
                float z = margin + (float)(rand.NextDouble()) * (MapSize - margin * 2f);

                // Clustered forests, not a uniform grid.
                float forest = Mathf.PerlinNoise(x * 0.0009f + 31f, z * 0.0009f + 6f);
                if (forest < 0.40f) continue;
                if (forest < 0.60f && rand.NextDouble() > 0.5) continue;

                float rd = RoadDistance(x, z);
                if (rd < RoadHalfWidth + 9f) continue;          // clear of roads
                if (Vector2.Distance(new Vector2(x, z), Village) < PlazaRadius + 25f) continue; // clear village
                if (DistanceToSpawn(x, z) < 20f) continue;       // clear player spawn
                {
                    float tax = Mathf.Abs(x - Village.x) - (TownHX + 12f);
                    float taz = Mathf.Abs(z - Village.y) - (TownHZ + 12f);
                    if (tax < 0f && taz < 0f) continue;          // clear the fortress + a green belt
                }

                int hx = Mathf.Clamp(Mathf.RoundToInt(x / _spacing), 0, HeightRes - 1);
                int hz = Mathf.Clamp(Mathf.RoundToInt(z / _spacing), 0, HeightRes - 1);
                if (SlopeAt(hx, hz) > 38f) continue;             // no trees on cliffs

                float worldH = Mathf.Clamp(_h2[hz, hx], 0f, MaxHeight);
                int protoIdx = (int)(rand.NextDouble() * prototypes.Count);
                if (protoIdx >= prototypes.Count) protoIdx = prototypes.Count - 1;

                var ti = new TreeInstance
                {
                    position = new Vector3(x / MapSize, worldH / MaxHeight, z / MapSize),
                    prototypeIndex = protoIdx,
                    widthScale = 0.8f + (float)rand.NextDouble() * 0.6f,
                    heightScale = 0.8f + (float)rand.NextDouble() * 0.6f,
                    rotation = (float)(rand.NextDouble() * Mathf.PI * 2f),
                    color = Color.white,
                    lightmapColor = Color.white
                };
                list.Add(ti);
            }
            _td.treeInstances = list.ToArray();
            Log("Trees placed: " + list.Count);
        }
        // =========================================================================
        //  Scene setup
        // =========================================================================
        // Render distance in metres picked in Map Settings (= the camera far plane,
        // so nothing is drawn past it). Everything around the player uses this.
        static float RenderDistance()
        {
            return Mathf.Max(30f, EditorPrefs.GetFloat("MM.RenderDist", 130f));
        }

        // How far the streamed nature props are kept active around the player.
        static float PropRadius()
        {
            return Mathf.Max(40f, RenderDistance() * 0.8f);
        }

        // Push the chosen render distance into a terrain's tree / grass / basemap draws.
        static void ApplyTerrainRenderDistances(Terrain terrain, float rd)
        {
            if (terrain == null) return;
            terrain.treeDistance = Mathf.Max(40f, rd);              // trees pop in up to the far plane
            terrain.detailObjectDistance = Mathf.Max(25f, rd * 0.6f); // grass/details closer for speed
            terrain.basemapDistance = Mathf.Max(60f, rd);           // far ground falls back to baked basemap
        }

        static void CreateTerrainGameObject()
        {
            var go = new GameObject("Terrain");
            _terrain = go.AddComponent<Terrain>();
            _terrain.terrainData = _td;
            var col = go.AddComponent<TerrainCollider>();
            col.terrainData = _td;

            Shader s = Shader.Find("Nature/Terrain/Standard");
            if (s != null) _terrain.materialTemplate = new Material(s);
            else Debug.LogWarning("Terrain shader not found (are you on Built-in pipeline?).");

            // Performance: only content near the camera is rendered. These distances
            // follow the "Render distance" set in Map Settings (a fresh build already
            // respects it, and ApplyAtmosphereToScene can retune it without rebuilding).
            _terrain.heightmapPixelError = 4f;
            _terrain.detailObjectDensity = 1f;
            ApplyTerrainRenderDistances(_terrain, RenderDistance());
            go.transform.position = Vector3.zero;

            // Bake the basemap texture so distant terrain is cheap.
            EditorUtility.SetDirty(_td);
            Log("Terrain created (1000m x 1000m).");
        }

        static void CreateLightingAndFog()
        {
            var sunGo = new GameObject("Directional Light");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.05f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.sun = sun;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.45f, 0.52f, 0.56f);
            RenderSettings.ambientEquatorColor = new Color(0.32f, 0.34f, 0.31f);
            RenderSettings.ambientGroundColor = new Color(0.13f, 0.16f, 0.13f);

            // Mysterious fog - hides distant content so only what's close reads clearly.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = EditorPrefs.GetFloat("MM.FogDensity", 0.008f);
            RenderSettings.fogColor = new Color(0.58f, 0.68f, 0.70f);
            RenderSettings.skybox = null;
        }

        internal static GameObject SpawnPiece(Transform parent, GameObject prefab, Vector3 pos, float yaw, float scale)
        {
            var go = SpawnAt(parent, prefab, pos, yaw, scale);
            if (go != null) EditorUtility.SetDirty(go);
            return go;
        }

        /// <summary>The town prefab folder (Assets/FantasyEnvironments/Environments/Town/Prefabs).</summary>
        internal static string TownPrefabFolder => TownDir;

        /// <summary>
        /// Points the builder's cached terrain / height data at the open scene's terrain, so a
        /// tool that runs WITHOUT a full rebuild (e.g. rebuilding just the town) can sample the
        /// ground height. Never regenerates anything.
        /// </summary>
        internal static void UseSceneTerrain(Terrain terrain)
        {
            if (terrain == null || terrain.terrainData == null) return;
            _terrain = terrain;
            _td = terrain.terrainData;

            int hres = _td.heightmapResolution;
            _spacing = _td.size.x / (hres - 1);
            float[,] norm = _td.GetHeights(0, 0, hres, hres);
            _h2 = new float[hres, hres];
            for (int z = 0; z < hres; z++)
                for (int x = 0; x < hres; x++)
                    _h2[z, x] = norm[z, x] * _td.size.y;
        }

        internal static float WorldSampleHeight(float x, float z)
        {
            if (_terrain != null && _terrain.terrainData != null)
                return _terrain.SampleHeight(new Vector3(x, 0f, z));
            if (_h2 != null)
            {
                int hx = Mathf.Clamp(Mathf.RoundToInt(x / _spacing), 0, HeightRes - 1);
                int hz = Mathf.Clamp(Mathf.RoundToInt(z / _spacing), 0, HeightRes - 1);
                return _h2[hz, hx];
            }
            return RoadFloor;
        }

        static GameObject LoadPrefab(string path)
        {
            var p = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (p == null) Debug.LogWarning("Missing prefab: " + path);
            return p;
        }

        static TerrainLayer LoadLayer(string name)
        {
            var l = AssetDatabase.LoadAssetAtPath<TerrainLayer>(LayerDir + "/" + name + ".terrainlayer");
            if (l == null) Debug.LogError("Missing terrain layer: " + name);
            return l;
        }

        internal static void Log(string msg)
        {
            if (DebugMode) Debug.Log("[MysticMap] " + msg);
        }
        static void CreatePlayer()
        {
            var pGo = new GameObject("Player");
            pGo.transform.position = new Vector3(Spawn.x, WorldSampleHeight(Spawn.x, Spawn.y) + 0.2f, Spawn.y);

            // The (Mystic Map) player is a slime: the CharacterController is auto-sized to the
            // imported model by SlimePlayer.FitToModel(), so these are just safe defaults.
            var cc = pGo.AddComponent<CharacterController>();
            cc.height = 1.2f; cc.radius = 0.5f; cc.center = new Vector3(0f, 0.6f, 0f);

            var slime = pGo.AddComponent<SlimePlayer>();

            var slimeAsset = AssetDatabase.LoadAssetAtPath<GameObject>(SlimePlayerSetup.SlimeModelPath);
            if (slimeAsset != null)
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(slimeAsset, pGo.transform);
                model.name = slimeAsset.name;
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                slime.model = model.transform;
            }
            else
            {
                Debug.LogWarning("[MysticMap] Slime model not found at " + SlimePlayerSetup.SlimeModelPath +
                                 " - the player was created without a visible body.");
            }

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(pGo.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.6f, -4.5f);

            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 75f;
            cam.nearClipPlane = 0.05f;
            // Render distance ~1/4 of the original 500 m, heavy fog beyond it.
            cam.farClipPlane = EditorPrefs.GetFloat("MM.RenderDist", 130f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = RenderSettings.fogColor;
            camGo.AddComponent<AudioListener>();

            slime.cam = cam;
            slime.FitToModel();   // size the controller + camera pivot to the slime mesh

            Log("Slime player created at " + pGo.transform.position.ToString("0.0"));
        }

        // Buildings are deliberately the low-priority part: a handful around the plaza.
        // (The fortress town itself lives in MysticMapTownBuilder.cs / TownDesignState.cs.)
        static void CreateVillage()
        {
            var root = new GameObject("Village");
            root.transform.position = Vector3.zero;

            // name, offset direction (angle degrees), distance from center, scale
            var plan = new[]
            {
                new { name = "Tavern",        ang = 215f, dist = 33f, s = 1f  },
                new { name = "Windmill",      ang = 20f,  dist = 40f, s = 0.9f },
                new { name = "House1",        ang = 120f, dist = 30f, s = 0.9f },
                new { name = "House2",        ang = 300f, dist = 31f, s = 0.9f },
                new { name = "House3",        ang = 80f,  dist = 42f, s = 0.85f },
                new { name = "WoodenHouse_1", ang = 255f, dist = 30f, s = 0.9f },
                new { name = "WoodenHouse_2", ang = 170f, dist = 38f, s = 0.8f }
            };

            int placed = 0;
            foreach (var b in plan)
            {
                var prefab = LoadPrefab(TownDir + "/" + b.name + ".prefab");
                if (prefab == null) continue;

                float rad = b.ang * Mathf.Deg2Rad;
                float ox = Mathf.Sin(rad) * b.dist;
                float oz = Mathf.Cos(rad) * b.dist;
                float wx = Village.x + ox;
                float wz = Village.y + oz;
                if (wx < 5f || wx > MapSize - 5f || wz < 5f || wz > MapSize - 5f) continue;

                float h = WorldSampleHeight(wx, wz);
                var go = SpawnAt(root.transform, prefab, new Vector3(wx, h, wz),
                                 -b.ang + 180f, b.s);
                if (go != null) placed++;
            }

            // A few village props / storage barrels to give the square some life.
            var props = new[] { "cart1", "storage_barrel", "Furniture_table" };
            for (int i = 0; i < props.Length; i++)
            {
                var prefab = LoadPrefab(TownDir + "/" + props[i] + ".prefab");
                if (prefab == null) continue;
                float rad = (float)(i) / props.Length * 360f * Mathf.Deg2Rad;
                float rr = 10f + 6f * i;
                float wx = Village.x + Mathf.Sin(rad) * rr;
                float wz = Village.y + Mathf.Cos(rad) * rr;
                float h = WorldSampleHeight(wx, wz);
                SpawnAt(root.transform, prefab, new Vector3(wx, h, wz), rad * Mathf.Rad2Deg, 1f);
            }
            Log("Village placed (" + placed + " buildings).");
        }

        static GameObject SpawnAt(Transform parent, GameObject prefab, Vector3 pos, float yaw, float scale)
        {
            if (prefab == null) return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (go == null) go = (GameObject)Object.Instantiate(prefab);
            if (go == null) return null;

            go.transform.SetParent(parent, true);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * scale;
            return go;
        }

        static List<GameObject> LoadMany(string dir, string[] names)
        {
            var list = new List<GameObject>();
            foreach (var n in names)
            {
                var p = LoadPrefab(dir + "/" + n + ".prefab");
                if (p != null) list.Add(p);
            }
            return list;
        }
        // =========================================================================
        //  Scattered ground cover (grass tufts, ferns, flowers, rocks...)
        // =========================================================================
        static readonly System.Random R = new System.Random(20240601);

        static void CreateScatter()
        {
            var root = new GameObject("Nature Props");
            root.transform.position = Vector3.zero;

            // Preload prefab pools once (avoids thousands of disk loads).
            var tall = LoadMany(PropsDir, new[] { "Grass1", "Grass2", "Grass3", "Grass4", "Rye" });
            var lowPlants = LoadMany(PropsDir, new[] { "Fern1", "Fern2", "Fern3", "Plant1", "Plant2", "Plant3", "Sunflower" });
            var flowers = LoadMany(PropsDir, new[] { "Flower1", "Flower2", "Flower3", "Flower4", "Flower5", "Flower6", "Flower7", "Flower8", "Flower9" });
            var bushes = LoadMany(PropsDir, new[] { "Bush1" });
            var mushrooms = LoadMany(PropsDir, new[] { "Mushroom1", "Mushroom2", "Mushroom3", "Mushroom4", "Mushroom5" });
            var rocks = LoadMany(PropsDir, new[] { "Rock1", "Rock2", "Rock3", "Stone1", "Stone2", "Stone3" });

            int total = 0;
            total += ScatterPlants(root.transform, tall, 900, 0.7f, 1.6f,
                                   (x, z) => AllowField(x, z, 32f, false));
            total += ScatterPlants(root.transform, lowPlants, 700, 0.8f, 1.5f,
                                   (x, z) => AllowField(x, z, 30f, false));
            total += ScatterPlants(root.transform, flowers, 550, 0.7f, 1.4f,
                                   (x, z) => AllowField(x, z, 28f, true));
            total += ScatterPlants(root.transform, bushes, 160, 0.8f, 1.6f,
                                   (x, z) => AllowField(x, z, 36f, false));
            total += ScatterPlants(root.transform, mushrooms, 220, 0.8f, 1.5f,
                                   (x, z) => AllowForest(x, z, 30f));
            total += ScatterPlants(root.transform, rocks, 320, 0.6f, 2.2f,
                                   (x, z) => AllowRock(x, z));

            // Streamed activation: only props within the chosen render distance are alive.
            var streamer = root.AddComponent<PropStreamer>();
            streamer.radius = PropRadius();
            var cam = GameObject.FindGameObjectWithTag("MainCamera");
            if (cam != null) streamer.target = cam.transform;

            // Deactivate far props right away so the editor and Game view stay light.
            var spawn3 = new Vector3(Spawn.x, 0f, Spawn.y);
            for (int i = 0; i < root.transform.childCount; i++)
            {
                var ch = root.transform.GetChild(i);
                bool near = (ch.position - spawn3).sqrMagnitude <= (streamer.radius * streamer.radius);
                if (!near) ch.gameObject.SetActive(false);
            }

            Log("Scattered props total: " + total);
        }
        static int ScatterPlants(Transform parent, List<GameObject> pool, int count,
                                 float sMin, float sMax, System.Func<float, float, bool> allow)
        {
            if (pool == null || pool.Count == 0) return 0;
            const int margin = 15;
            int placed = 0, guard = 0;
            while (placed < count && guard < count * 12)
            {
                guard++;
                float x = margin + (float)R.NextDouble() * (MapSize - margin * 2f);
                float z = margin + (float)R.NextDouble() * (MapSize - margin * 2f);
                if (!allow(x, z)) continue;

                var prefab = pool[(int)(R.NextDouble() * pool.Count)];
                float h = WorldSampleHeight(x, z);
                float yaw = (float)(R.NextDouble() * 360f);
                float s = sMin + (float)R.NextDouble() * (sMax - sMin);
                if (SpawnAt(parent, prefab, new Vector3(x, h, z), yaw, s) != null) placed++;
            }
            return placed;
        }

        static float SlopeAtWorld(float x, float z)
        {
            int hx = Mathf.Clamp(Mathf.RoundToInt(x / _spacing), 0, HeightRes - 1);
            int hz = Mathf.Clamp(Mathf.RoundToInt(z / _spacing), 0, HeightRes - 1);
            return SlopeAt(hx, hz);
        }

        static bool AllowField(float x, float z, float maxSlope, bool openOnly)
        {
            float rd = RoadDistance(x, z);
            if (rd < RoadHalfWidth + 4f) return false;
            if (Vector2.Distance(new Vector2(x, z), Village) < PlazaRadius + 3f) return false;
            if (DistanceToSpawn(x, z) < 8f) return false;
            if (SlopeAtWorld(x, z) > maxSlope) return false;
            if (openOnly)
            {
                float forest = Mathf.PerlinNoise(x * 0.0009f + 31f, z * 0.0009f + 6f);
                if (forest > 0.62f) return false;      // flowers prefer open clearings
            }
            return true;
        }

        static bool AllowForest(float x, float z, float maxSlope)
        {
            float rd = RoadDistance(x, z);
            if (rd < RoadHalfWidth + 4f) return false;
            if (Vector2.Distance(new Vector2(x, z), Village) < PlazaRadius + 3f) return false;
            if (DistanceToSpawn(x, z) < 8f) return false;
            if (SlopeAtWorld(x, z) > maxSlope) return false;
            float forest = Mathf.PerlinNoise(x * 0.0009f + 31f, z * 0.0009f + 6f);
            return forest > 0.58f;                       // mushrooms like the woods
        }

        static bool AllowRock(float x, float z)
        {
            float rd = RoadDistance(x, z);
            if (rd < RoadHalfWidth + 4f) return false;
            if (Vector2.Distance(new Vector2(x, z), Village) < PlazaRadius + 3f) return false;
            if (DistanceToSpawn(x, z) < 10f) return false;
            if (x < 15f || x > MapSize - 15f || z < 15f || z > MapSize - 15f) return false;
            return true;                                  // rocks live anywhere, esp. slopes
        }

        // =========================================================================
        //  Live tools used by the "Map Settings" window (apply to the open scene)
        // =========================================================================
        public static Terrain FindSceneTerrain()
        {
            var terrains = Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None);
            foreach (var t in terrains)
                if (t.gameObject.name == "Terrain") return t;
            return terrains.Length > 0 ? terrains[0] : null;
        }

        public static void ApplyAtmosphereToScene(float farClip, float fogDensity)
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = Mathf.Max(0.0005f, fogDensity);
            RenderSettings.fogColor = new Color(0.58f, 0.68f, 0.70f);
            RenderSettings.skybox = null;

            var cams = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            foreach (var c in cams)
            {
                c.farClipPlane = Mathf.Max(30f, farClip);
                c.clearFlags = CameraClearFlags.SolidColor;
                c.backgroundColor = RenderSettings.fogColor;
            }

            var terrain = FindSceneTerrain();
            if (terrain != null) ApplyTerrainRenderDistances(terrain, farClip);

            // Match the prop-streaming radius so scenery appears up to the chosen range.
            var streamers = Object.FindObjectsByType<PropStreamer>(FindObjectsSortMode.None);
            foreach (var s in streamers) s.radius = Mathf.Max(40f, farClip * 0.8f);

            EditorPrefs.SetFloat("MM.RenderDist", farClip);
            EditorPrefs.SetFloat("MM.FogDensity", fogDensity);
            Log("Applied atmosphere: far=" + farClip + " fog=" + fogDensity);
        }

        public static void BakeGrassInScene(float density, float scale)
        {
            var terrain = FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("No Terrain in the open scene. Build the map first.");
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

            EditorPrefs.SetFloat("MM.GrassDensity", density);
            EditorPrefs.SetFloat("MM.GrassScale", scale);
            ApplyGrassDetails();
            EditorUtility.SetDirty(_td);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Log("Grass re-baked: density=" + density + " scale=" + scale);
        }
        public static int AddPropsToScene(string group, int count)
        {
            var terrain = FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("No Terrain in the open scene. Build the map first.");
                return 0;
            }
            _terrain = terrain;

            string[] names;
            float sMin, sMax;
            if (group == "stones" || group == "rocks")
            {
                names = new[] { "Rock1", "Rock2", "Rock3", "Stone1", "Stone2", "Stone3" };
                sMin = 0.6f; sMax = 2.2f;
            }
            else if (group == "flowers")
            {
                names = new[] { "Flower1", "Flower2", "Flower3", "Flower4", "Flower5", "Flower6", "Flower7", "Flower8", "Flower9" };
                sMin = 0.7f; sMax = 1.4f;
            }
            else if (group == "plants")
            {
                names = new[] { "Fern1", "Fern2", "Fern3", "Plant1", "Plant2", "Plant3", "Sunflower", "Bush1" };
                sMin = 0.8f; sMax = 1.6f;
            }
            else // grass tufts
            {
                names = new[] { "Grass1", "Grass2", "Grass3", "Grass4", "Rye" };
                sMin = 0.9f; sMax = 2.2f;
            }

            var pool = LoadMany(PropsDir, names);
            if (pool.Count == 0)
            {
                Debug.LogWarning("No prefabs found for group '" + group + "'.");
                return 0;
            }

            var root = GameObject.Find("Nature Props");
            if (root == null)
            {
                root = new GameObject("Nature Props");
                root.transform.position = Vector3.zero;
                var streamer = root.AddComponent<PropStreamer>();
                streamer.radius = PropRadius();
                var cam = GameObject.FindGameObjectWithTag("MainCamera");
                if (cam != null) root.GetComponent<PropStreamer>().target = cam.transform;
            }

            const int margin = 20;
            int placed = 0, guard = 0;
            while (placed < count && guard < count * 15)
            {
                guard++;
                float x = margin + (float)R.NextDouble() * (MapSize - margin * 2f);
                float z = margin + (float)R.NextDouble() * (MapSize - margin * 2f);

                float rd = RoadDistance(x, z);
                if (rd < RoadHalfWidth + 4f) continue;
                if (Vector2.Distance(new Vector2(x, z), Village) < PlazaRadius + 3f) continue;

                float h = _terrain.SampleHeight(new Vector3(x, 0f, z));
                float yaw = (float)(R.NextDouble() * 360f);
                float s = sMin + (float)R.NextDouble() * (sMax - sMin);
                var prefab = pool[(int)(R.NextDouble() * pool.Count)];
                if (SpawnAt(root.transform, prefab, new Vector3(x, h, z), yaw, s) != null) placed++;
            }
            EditorUtility.SetDirty(root);
            Log("Added " + placed + " '" + group + "' props.");
            return placed;
        }

        // ---- Magic aura + grass color ------------------------------------------
        public static void CreateMagicAuraInScene(Color color, float intensity, float radius,
                                                   float softness, bool followPlayer, bool enabled)
        {
            string auraName = "Magic Aura";
            var go = GameObject.Find(auraName);
            if (go == null)
            {
                go = new GameObject(auraName);
                go.transform.position = new Vector3(Village.x, 0f, Village.y); // over the village
            }

            var mc = go.GetComponent<MagicAura>();
            if (mc == null) mc = go.AddComponent<MagicAura>();
            mc.color = color;
            mc.intensity = intensity;
            mc.radius = radius;
            mc.softness = softness;
            mc.centerHeight = 1.5f;
            mc.followPlayer = followPlayer;

            // Make sure the glow disc + material exist so it shows in the Scene view.
            Material mat = LoadOrCreateAuraMaterial();
            Transform disc = go.transform.Find("GlowDisc");
            if (disc == null)
            {
                var quadGo = CreateAuraQuad();
                quadGo.transform.SetParent(go.transform, false);
                quadGo.name = "GlowDisc";
                disc = quadGo.transform;
            }
            var r = disc.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = mat;
            if (mat != null)
            {
                mat.SetColor("_Color", color);
                mat.SetFloat("_Intensity", Mathf.Max(0f, intensity));
                mat.SetFloat("_Falloff", Mathf.Max(0.05f, softness));
            }
            disc.localScale = new Vector3(radius * 2f, radius * 2f, 1f);

            go.SetActive(enabled);
            EditorUtility.SetDirty(go);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            Log("Magic aura " + (enabled ? "created/updated" : "hidden") + ".");
        }

        static Material LoadOrCreateAuraMaterial()
        {
            string path = GeneratedDir + "/MagicAuraMat.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Shader sh = Shader.Find("MysticMap/MagicAura");
            if (sh == null) return null;
            var mat = new Material(sh);
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();
            return mat;
        }

        static GameObject CreateAuraQuad()
        {
            var go = new GameObject("GlowDisc");
            var mf = go.AddComponent<MeshFilter>();
            var mesh = new Mesh();
            mesh.name = "AuraQuad";
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f)
            };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateNormals();
            mf.mesh = mesh;
            go.AddComponent<MeshRenderer>();
            return go;
        }

        public static void SetGrassVisualInScene(Color baseColor, float brightness)
        {
            var terrain = FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("No Terrain in the open scene. Build the map first.");
                return;
            }
            var td = terrain.terrainData;
            var protos = td.detailPrototypes;
            Color healthy = Bright(baseColor, brightness);
            Color dry = Bright(new Color(baseColor.r * 0.72f, baseColor.g * 0.85f, baseColor.b * 0.65f), brightness * 0.9f);
            for (int i = 0; i < protos.Length; i++)
            {
                protos[i].healthyColor = healthy;
                protos[i].dryColor = dry;
            }
            td.detailPrototypes = protos;
            EditorUtility.SetDirty(td);
            AssetDatabase.SaveAssets();
            Log("Grass color applied: " + baseColor + " brightness=" + brightness);
        }

        static Color Bright(Color c, float mul)
        {
            return new Color(
                Mathf.Clamp01(c.r * mul),
                Mathf.Clamp01(c.g * mul),
                Mathf.Clamp01(c.b * mul),
                1f);
        }

        // Glow applied to the grass sprites only (not roads/bare terrain). Removes the old ground blob.
        public static void SetGrassGlowInScene(Color glowColor, float strength, bool on)
        {
            var oldBlob = GameObject.Find("Magic Aura");
            if (oldBlob != null) Object.DestroyImmediate(oldBlob);

            var terrain = FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("No Terrain in the open scene. Build the map first.");
                return;
            }
            var td = terrain.terrainData;

            Color baseColor = PrefColor("MM.GrassColor", new Color(0.35f, 0.9f, 0.3f, 1f));
            float bright = EditorPrefs.GetFloat("MM.GrassBright", 1.3f);

            Color normal = Bright(baseColor, bright);
            Color normalDry = Bright(new Color(baseColor.r * 0.72f, baseColor.g * 0.85f, baseColor.b * 0.65f), bright * 0.9f);

            float t = on ? Mathf.Clamp01(strength) : 0f;
            Color glow = Bright(glowColor, 1f + strength);
            Color glowDry = Bright(new Color(glowColor.r * 0.55f, glowColor.g * 0.7f, glowColor.b * 0.55f), 1f + strength * 0.7f);

            var protos = td.detailPrototypes;
            for (int i = 0; i < protos.Length; i++)
            {
                protos[i].healthyColor = Color.Lerp(normal, glow, t);
                protos[i].dryColor = Color.Lerp(normalDry, glowDry, t);
            }
            td.detailPrototypes = protos;
            EditorUtility.SetDirty(td);
            AssetDatabase.SaveAssets();
            Log("Grass glow " + (on ? "applied (" + glowColor + " strength=" + strength + ")" : "off"));
        }

        static Color PrefColor(string key, Color def)
        {
            if (!EditorPrefs.HasKey(key + ".R")) return def;
            return new Color(
                EditorPrefs.GetFloat(key + ".R", def.r),
                EditorPrefs.GetFloat(key + ".G", def.g),
                EditorPrefs.GetFloat(key + ".B", def.b),
                EditorPrefs.GetFloat(key + ".A", def.a));
        }

        // ---- Grass field glow (soft ground glows, only where grass grows) ------
        public static void CreateGrassGlowFieldInScene(Color color, float strength, float spacing, bool enabled)
        {
            RemoveGrassGlowField();
            if (!enabled) return;

            var terrain = FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("No Terrain in the open scene. Build the map first.");
                return;
            }
            _terrain = terrain;

            Material mat = LoadOrCreateAuraMaterial();
            if (mat == null)
            {
                Debug.LogWarning("MagicAura shader not found.");
                return;
            }

            var root = new GameObject("Grass Glow Field");
            var orbs = root.AddComponent<GlowOrbs>();
            orbs.radius = Mathf.Max(60f, EditorPrefs.GetFloat("MM.RenderDist", 130f) * 0.7f);
            orbs.pulseSpeed = 1.1f;
            orbs.pulseAmount = 0.06f;
            var cam = GameObject.FindGameObjectWithTag("MainCamera");
            if (cam != null) orbs.target = cam.transform;

            float step = Mathf.Max(8f, spacing);
            int placed = 0;
            for (float x = 15f; x < MapSize - 15f; x += step)
            {
                for (float z = 15f; z < MapSize - 15f; z += step)
                {
                    // Jitter each point so glows never line up into a visible grid.
                    float jx = x + ((float)R.NextDouble() - 0.5f) * step * 0.95f;
                    float jz = z + ((float)R.NextDouble() - 0.5f) * step * 0.95f;
                    if (!IsGrassRegion(jx, jz)) continue;

                    float ground = terrain.SampleHeight(new Vector3(jx, 0f, jz));
                    float variation = 0.6f + (float)R.NextDouble() * 0.8f;   // per-orb strength variation
                    float radius = step * 0.18f * (0.9f + (float)R.NextDouble() * 0.6f);

                    var child = CreateAuraQuad();
                    child.name = "O";
                    child.transform.SetParent(root.transform, false);
                    float cy = ground + radius * 0.6f;       // low, sitting on the grass base
                    child.transform.position = new Vector3(jx, cy, jz);
                    child.transform.rotation = Quaternion.identity;   // GlowOrbs billboards it every frame
                    float d = radius * 2f;
                    child.transform.localScale = new Vector3(d, d * 0.6f, 1f);   // a low, rounded glow

                    var rend = child.GetComponent<MeshRenderer>();
                    if (rend != null)
                    {
                        rend.sharedMaterial = mat;
                        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        rend.receiveShadows = false;
                        var pb = new MaterialPropertyBlock();
                        pb.SetColor("_Color", color);
                        pb.SetFloat("_Intensity", Mathf.Max(0.001f, strength * variation));
                        pb.SetFloat("_Falloff", 3f);
                        rend.SetPropertyBlock(pb);
                    }
                    placed++;
                }
            }

            // Keep the editor light: leave only the glows near spawn active; the streamer
            // wakes the rest up as the player approaches during Play.
            Vector3 spawnPt = new Vector3(Spawn.x, 0f, Spawn.y);
            float nearR = orbs.radius;
            for (int i = 0; i < root.transform.childCount; i++)
            {
                var ch = root.transform.GetChild(i);
                if ((ch.position - spawnPt).sqrMagnitude > nearR * nearR)
                    ch.gameObject.SetActive(false);
            }

            EditorUtility.SetDirty(root);
            Log("Grass glow field placed: " + placed + " patches (strength=" + strength + " spacing=" + spacing + ")");
        }

        public static void RemoveGrassGlowField()
        {
            var old = GameObject.Find("Grass Glow Field");
            if (old != null) Object.DestroyImmediate(old);
            var oldBlob = GameObject.Find("Magic Aura");   // remove any leftover old glow blob too
            if (oldBlob != null) Object.DestroyImmediate(oldBlob);
        }

        // ---- Glow attached to the actual grass sprites (goes with the grass) ------
        static Mesh _auraQuadMesh;
        static Mesh AuraQuadMesh()
        {
            if (_auraQuadMesh != null) return _auraQuadMesh;
            _auraQuadMesh = new Mesh();
            _auraQuadMesh.name = "AuraQuad";
            _auraQuadMesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
            };
            _auraQuadMesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f)
            };
            _auraQuadMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _auraQuadMesh.RecalculateNormals();
            return _auraQuadMesh;
        }

        static Material LoadOrCreateGlowMaterial()
        {
            string path = GeneratedDir + "/MagicGlowMat.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            Shader sh = Shader.Find("MysticMap/BillboardGlow");
            if (sh == null) return null;
            var mat = new Material(sh);
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();
            return mat;
        }

        public static void CreateGrassSpriteGlowsInScene(Color color, float strength, float size, float height, bool enabled)
        {
            RemoveGrassGlowField();   // drop the old floating/orb glow first
            if (!enabled) return;

            var terrain = FindSceneTerrain();
            Material mat = LoadOrCreateGlowMaterial();
            if (mat == null)
            {
                Debug.LogWarning("BillboardGlow shader not found.");
                return;
            }

            string[] roots = { "Nature Props", "Painted Props" };
            int attached = 0;
            foreach (var rootName in roots)
            {
                var root = GameObject.Find(rootName);
                if (root == null) continue;
                int n = root.transform.childCount;
                for (int i = 0; i < n; i++)
                {
                    var prop = root.transform.GetChild(i);
                    if (prop == null) continue;

                    // Remove any glow already attached to this prop (so re-applying is clean).
                    var oldGlow = prop.Find("GlowSprite");
                    if (oldGlow != null) Object.DestroyImmediate(oldGlow.gameObject);

                    float gx = prop.position.x, gz = prop.position.z;
                    float ground = 0f;
                    if (terrain != null) ground = terrain.SampleHeight(new Vector3(gx, 0f, gz));

                    float variation = 0.7f + (float)R.NextDouble() * 0.7f;  // only brightness varies per sprite
                    // Glow width follows this grass's painted size (bigger grass -> wider glow).
                    // The "height above grass" stays uniform because the billboard shader
                    // ignores object scale, so we compute the width from the prop's scale.
                    float scl = Mathf.Max(0.3f, Mathf.Max(prop.lossyScale.x, prop.lossyScale.y));
                    float dia = Mathf.Max(0.15f, size * scl);              // world diameter, matches grass

                    var glow = new GameObject("GlowSprite");
                    glow.transform.SetParent(prop, false);
                    float gy = Mathf.Max(0.02f, height) + ground;           // height above the ground
                    glow.transform.position = new Vector3(gx, gy, gz);

                    var mf = glow.AddComponent<MeshFilter>();
                    mf.sharedMesh = AuraQuadMesh();
                    var mr = glow.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = mat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;

                    var pb = new MaterialPropertyBlock();
                    pb.SetColor("_Color", color);
                    pb.SetFloat("_Intensity", Mathf.Max(0.001f, strength * variation));
                    pb.SetFloat("_Falloff", 3f);
                    pb.SetFloat("_Size", dia);
                    pb.SetFloat("_Aspect", 0.7f);
                    mr.SetPropertyBlock(pb);
                    attached++;
                }
                if (root != null) EditorUtility.SetDirty(root);
            }
            Log("Grass glow attached to " + attached + " sprites (strength=" + strength + " size=" + size + " height=" + height + ").");
        }

        /// <summary>
        /// Deletes the per-prop "GlowSprite" children that were added to every grass tuft /
        /// plant / stone on the map. This is what actually removes the tens of thousands of
        /// glow quads and shrinks the (huge) scene file. The grass-glow FEATURE is kept - the
        /// settings and toggle remain in Map Settings, and it is set to OFF so auto-restore
        /// does not instantly re-add the glows the next time you leave Play mode.
        /// </summary>
        public static void RemoveGrassSpriteGlows()
        {
            string[] roots = { "Nature Props", "Painted Props" };
            int removed = 0;
            foreach (var rootName in roots)
            {
                var root = GameObject.Find(rootName);
                if (root == null) continue;
                Transform props = root.transform;
                for (int i = props.childCount - 1; i >= 0; i--)
                {
                    var prop = props.GetChild(i);
                    if (prop == null) continue;
                    var glow = prop.Find("GlowSprite");
                    if (glow == null) continue;
                    Object.DestroyImmediate(glow.gameObject);
                    removed++;
                }
                EditorUtility.SetDirty(root);
            }

            // Don't let auto-restore instantly re-add them next time you leave Play.
            EditorPrefs.SetBool("MM.GlowOn", false);

            // Drop the old orb-field root too if it lingers.
            RemoveGrassGlowField();

            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            Log("Removed " + removed + " grass glow sprites (feature kept, ready to re-enable).");
        }

        [MenuItem("MCP/Remove grass glows from the map (keep feature)")]
        static void RemoveGrassGlowsMenu()
        {
            RemoveGrassSpriteGlows();
        }

        // ---- Floating magical dust (rises from the ground near the player) ---------
        public static void CreateMagicalDustInScene(Color color, bool enabled, int count, float radius,
                                                    float size, float speed, float glow,
                                                    float pulseAmount, float pulseSpeed,
                                                    bool illuminate, float lightStrength)
        {
            RemoveMagicalDust();
            if (!enabled) return;

            var root = new GameObject("Magical Dust");
            root.transform.position = new Vector3(Village.x, 0f, Village.y);

            var dust = root.AddComponent<MagicalDust>();
            dust.color = color;
            dust.glowStrength = Mathf.Max(0f, glow);
            dust.pulseAmount = Mathf.Clamp01(pulseAmount);
            dust.pulseSpeed = Mathf.Max(0f, pulseSpeed);
            dust.count = Mathf.Clamp(count, 1, 300);
            dust.radius = Mathf.Max(2f, radius);
            dust.particleSize = Mathf.Max(0.03f, size);
            dust.riseSpeed = Mathf.Max(0.05f, speed);
            dust.illuminate = illuminate;
            dust.lightStrength = Mathf.Clamp(lightStrength, 0f, 2f);

            var cam = GameObject.FindGameObjectWithTag("MainCamera");
            if (cam != null) dust.target = cam.transform;

            EditorUtility.SetDirty(root);
            Log("Magical dust added: count=" + count + " radius=" + radius + " size=" + size +
                " speed=" + speed + " glow=" + glow + " pulse=" + pulseAmount + " pulseSpeed=" + pulseSpeed);
        }

        public static void RemoveMagicalDust()
        {
            var old = GameObject.Find("Magical Dust");
            if (old != null) Object.DestroyImmediate(old);
        }

        // Cap how far away we render grass and the glow under it (power saving).
        public static void ApplyGrassDistancesToScene(float grassDist, float glowDist)
        {
            grassDist = Mathf.Clamp(grassDist, 5f, 600f);
            glowDist = Mathf.Clamp(glowDist, 5f, 600f);

            // 1) Stop drawing far terrain grass - the big, expensive "grass field".
            var terrain = FindSceneTerrain();
            if (terrain != null)
            {
                terrain.detailObjectDistance = grassDist;
                EditorUtility.SetDirty(terrain);
            }

            // 2) Grass / ground-cover props + their glow under them.
            string[] roots = { "Nature Props", "Painted Props" };
            var cam = GameObject.FindGameObjectWithTag("MainCamera");
            Vector3 camPos = (cam != null) ? cam.transform.position : new Vector3(Spawn.x, 0f, Spawn.y);

            foreach (var rn in roots)
            {
                var root = GameObject.Find(rn);
                if (root == null) continue;

                var ps = root.GetComponent<PropStreamer>();
                if (ps == null) ps = root.AddComponent<PropStreamer>();
                ps.radius = grassDist;
                if (cam != null) ps.target = cam.transform;

                var gc = root.GetComponent<GlowDistanceCuller>();
                if (gc == null) gc = root.AddComponent<GlowDistanceCuller>();
                gc.radius = glowDist;
                if (cam != null) gc.target = cam.transform;

                // Reflect the change immediately (even in the Scene view), not only after
                // pressing Play. PropStreamer keeps this updated as you move in Play mode.
                float sqrG = grassDist * grassDist;
                float sqrGl = glowDist * glowDist;
                int n = root.transform.childCount;
                for (int i = 0; i < n; i++)
                {
                    var ch = root.transform.GetChild(i);
                    if (ch == null) continue;
                    float sqr = (ch.position - camPos).sqrMagnitude;
                    bool on = sqr <= sqrG;
                    if (ch.gameObject.activeSelf != on) ch.gameObject.SetActive(on);
                    if (on)
                    {
                        var glow = ch.Find("GlowSprite");
                        if (glow != null)
                            glow.gameObject.SetActive(sqr <= sqrGl);
                    }
                }
            }

            EditorPrefs.SetFloat("MM.GrassDist", grassDist);
            EditorPrefs.SetFloat("MM.GlowDist", glowDist);
            Log("Applied grass distances: grass=" + grassDist + "m glow=" + glowDist + "m.");
        }

        // Where is grass allowed to glow? Everywhere except roads and the village/town.
        static bool IsGrassRegion(float x, float z)
        {
            float rd = RoadDistance(x, z);
            if (rd < RoadHalfWidth + 3f) return false;
            if (Vector2.Distance(new Vector2(x, z), Village) < PlazaRadius + 6f) return false;
            if (x < 12f || x > MapSize - 12f || z < 12f || z > MapSize - 12f) return false;
            return true;
        }

    }
}
#endif

