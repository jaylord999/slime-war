#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using MysticMap.World;

namespace MysticMap.EditorTools
{
    /// <summary>
    /// Tuning panel for the streamed procedural world (menu: MCP > Procedural World > World
    /// Settings). It edits the settings that live on the scene's ChunkManager, so a change is
    /// stored with the scene and takes effect on the next chunk.
    /// </summary>
    public class WorldSettingsWindow : EditorWindow
    {
        ChunkManager _manager;
        Vector2 _scroll;
        bool _showRoads = true;
        bool _showContent = true;

        [MenuItem("MCP/Procedural World/World Settings")]
        public static void Open()
        {
            var w = GetWindow<WorldSettingsWindow>(true, "Procedural World");
            w.minSize = new Vector2(380, 520);
            w.Refresh();
        }

        void OnEnable() => Refresh();

        void OnFocus() => Refresh();

        void Refresh()
        {
            _manager = ProceduralWorldSetup.FindManager();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_manager == null)
            {
                EditorGUILayout.HelpBox(
                    "There is no procedural world in this scene yet.\n\n" +
                    "1. Run  MCP > Procedural World > 1. Open the town road passes\n" +
                    "2. Then  MCP > Procedural World > 2. Add the world to the scene",
                    MessageType.Info);

                if (GUILayout.Button("Add the world to the scene")) ProceduralWorldSetup.AddToSceneMenu();
                EditorGUILayout.EndScrollView();
                return;
            }

            var settings = _manager.settings;
            if (settings == null)
            {
                EditorGUILayout.HelpBox("The streamer has no settings object.", MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.LabelField("World", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            EditorGUI.BeginChangeCheck();

            settings.seed = EditorGUILayout.IntField(
                new GUIContent("Seed", "Changing this regenerates the whole country. The town never changes."),
                settings.seed);

            settings.chunkSize = EditorGUILayout.FloatField(
                new GUIContent("Chunk size (m)", "Must divide 100 so the town roads line up."),
                settings.chunkSize);

            settings.viewRadiusChunks = EditorGUILayout.IntSlider(
                new GUIContent("View radius (chunks)", "How far the ground is generated around the player."),
                settings.viewRadiusChunks, 1, 10);

            settings.detailRadiusChunks = EditorGUILayout.IntSlider(
                new GUIContent("Detail radius (chunks)", "How far trees, grass, rocks and ruins are generated."),
                settings.detailRadiusChunks, 0, settings.viewRadiusChunks);

            settings.chunksPerFrame = EditorGUILayout.IntSlider(
                new GUIContent("Chunks per frame", "Lower = smoother, but the world fills in more slowly."),
                settings.chunksPerFrame, 1, 4);

            settings.unloadMarginChunks = EditorGUILayout.IntSlider(
                new GUIContent("Keep-alive margin", "Chunks are released this many chunks past the view radius."),
                settings.unloadMarginChunks, 0, 3);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Ground", EditorStyles.boldLabel);

            settings.meshResolution = EditorGUILayout.IntSlider("Near mesh resolution", settings.meshResolution, 9, 65);
            settings.farMeshResolution = EditorGUILayout.IntSlider("Far mesh resolution", settings.farMeshResolution, 5, 33);
            settings.lodDistanceChunks = EditorGUILayout.Slider("LOD distance (chunks)", settings.lodDistanceChunks, 1f, 6f);
            settings.baseHeight = EditorGUILayout.Slider("Base height (m)", settings.baseHeight, 0f, 60f);
            settings.maxHeight = EditorGUILayout.Slider("Max height (m)", settings.maxHeight, 60f, 300f);
            settings.seamBand = EditorGUILayout.Slider(
                new GUIContent("Seam blend (m)", "Distance over which the new ground blends into the hand-built map."),
                settings.seamBand, 40f, 400f);

            _showRoads = EditorGUILayout.Foldout(_showRoads, "Roads", true);
            if (_showRoads)
            {
                EditorGUI.indentLevel++;
                settings.roadHalfWidthNear = EditorGUILayout.Slider("Half width near town (m)", settings.roadHalfWidthNear, 2f, 12f);
                settings.roadHalfWidthFar = EditorGUILayout.Slider("Half width far away (m)", settings.roadHalfWidthFar, 2f, 16f);
                settings.roadRelief = EditorGUILayout.Slider("Height variation (m)", settings.roadRelief, 0f, 40f);
                settings.roadMaxApron = EditorGUILayout.Slider("Max cut apron (m)", settings.roadMaxApron, 20f, 160f);
                settings.roadStraightRange = EditorGUILayout.Slider(
                    new GUIContent("Straight for (m)", "Roads leave the town dead straight for this distance."),
                    settings.roadStraightRange, 5f, 600f);
                settings.roadWindRange = EditorGUILayout.Slider("Winds by (m)", settings.roadWindRange, 100f, 3000f);
                settings.roadMaxTurnDegrees = EditorGUILayout.Slider("Max wander (deg)", settings.roadMaxTurnDegrees, 0f, 80f);

                settings.roadCurly = EditorGUILayout.Slider(
                    new GUIContent("Curly roads", "0 = the old gentle curve, 1 = every road loiters, loops and S-bends. " +
                                                  "Curly roads need a bigger 'Max cut apron' so their loops are graded too."),
                    settings.roadCurly, 0f, 1f);
                if (settings.roadCurly > 0.001f)
                {
                    EditorGUI.indentLevel++;
                    settings.roadCurlyMaxWander = EditorGUILayout.Slider(
                        new GUIContent("Curl reach (m)", "How far a curl may loop sideways off the road's own line."),
                        settings.roadCurlyMaxWander, 0f, 60f);
                    settings.roadCurlySwirlPercent = EditorGUILayout.Slider(
                        new GUIContent("Swirl length (%)", "How much longer the curls make a road. Must stay below the " +
                                                           "curl field margin or a loop can reach past its graded ground."),
                        settings.roadCurlySwirlPercent, 0f, 30f);
                    settings.roadCurlyTownSpan = EditorGUILayout.Slider(
                        new GUIContent("Town road span (m)", "The hand-built roads that leave the town are curled in stretches this long."),
                        settings.roadCurlyTownSpan, 20f, 300f);
                    settings.roadCurlyFieldMargin = EditorGUILayout.Slider(
                        new GUIContent("Curl field margin (m)", "Reserved around every road so a curl always stays inside its road field."),
                        settings.roadCurlyFieldMargin, 0f, 100f);
                    EditorGUI.indentLevel--;
                }
                EditorGUI.indentLevel--;
            }

            _showContent = EditorGUILayout.Foldout(_showContent, "Content", true);
            if (_showContent)
            {
                EditorGUI.indentLevel++;
                settings.generateEnvironment = EditorGUILayout.Toggle("Trees / grass / ruins", settings.generateEnvironment);
                settings.maxTreesPerChunk = EditorGUILayout.IntSlider(
                    new GUIContent("Max trees per chunk", "How many of the chunk's tree spots may actually grow a tree."),
                    settings.maxTreesPerChunk, 0, 200);
                settings.maxGroundPlantsPerChunk = EditorGUILayout.IntSlider("Max plants per chunk", settings.maxGroundPlantsPerChunk, 0, 300);
                settings.maxRocksPerChunk = EditorGUILayout.IntSlider("Max rocks per chunk", settings.maxRocksPerChunk, 0, 40);
                settings.roadClearance = EditorGUILayout.Slider("Road clearance (m)", settings.roadClearance, 0f, 25f);
                settings.ruinChance = EditorGUILayout.Slider("Ruin chance", settings.ruinChance, 0f, 1f);
                settings.mazeChance = EditorGUILayout.Slider("Maze chance", settings.mazeChance, 0f, 1f);
                settings.mazeCells = EditorGUILayout.IntSlider("Maze size (cells)", settings.mazeCells, 5, 15);
                EditorGUI.indentLevel--;
            }

            if (EditorGUI.EndChangeCheck())
            {
                settings.Validate();
                EditorUtility.SetDirty(_manager);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh palette")) ProceduralWorldSetup.FillPaletteMenu();
                if (GUILayout.Button("Preview here")) ProceduralWorldSetup.PreviewMenu();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Clear preview")) ProceduralWorldSetup.ClearPreviewMenu();
                if (GUILayout.Button("Open road passes")) MysticMapBuilder.OpenRoadPassesMenu();
            }

            WorldPrefabPalette palette = settings.palette;
            EditorGUILayout.HelpBox(
                "Palette: " + Count(palette.trees) + " trees, " + Count(palette.bushes) + " bushes, " +
                Count(palette.grass) + " grass, " + Count(palette.flowers) + " flowers, " +
                Count(palette.rocks) + " rocks, " + Count(palette.stones) + " stones, " +
                Count(palette.walls) + " walls, " + Count(palette.props) + " props.\n\n" +
                "Live chunks: " + _manager.LiveChunks + "   queued: " + _manager.QueuedChunks +
                "   town chunks skipped: " + _manager.LegacyChunksSkipped + "\n" +
                "Trees standing: " + _manager.TreesLive + "   tree prefabs available: " + _manager.TreePoolSize,
                MessageType.None);

            EditorGUILayout.LabelField("Trees around the player", EditorStyles.boldLabel);
            _manager.treesOnlyInPlayerView = EditorGUILayout.Toggle(
                new GUIContent("Only in the player's view",
                    "Trees are grown where the player can see them and given back when they look away. " +
                    "Nothing is stored in the scene."),
                _manager.treesOnlyInPlayerView);
            _manager.treeViewAngle = EditorGUILayout.Slider(
                new GUIContent("View cone (deg)", "Trees outside this cone are only kept when they are close."),
                _manager.treeViewAngle, 40f, 360f);
            _manager.treeKeepRadius = EditorGUILayout.Slider(
                new GUIContent("Keep radius (m)", "Trees this close to the player always stay."),
                _manager.treeKeepRadius, 5f, 120f);
            _manager.treeFullDensityChunks = EditorGUILayout.IntSlider(
                new GUIContent("Full density (chunks)", "Chunks this close to the player grow their whole forest."),
                _manager.treeFullDensityChunks, 0, 4);
            _manager.treeRefreshInterval = EditorGUILayout.Slider(
                new GUIContent("Refresh (s)", "Seconds between 'what can the player see now?' checks."),
                _manager.treeRefreshInterval, 0.05f, 1f);
            _manager.treeRebuildsPerTick = EditorGUILayout.IntSlider(
                new GUIContent("Chunks per refresh", "Trees grown or given back per check."),
                _manager.treeRebuildsPerTick, 1, 6);

            if (_manager.TreePoolSize == 0)
                EditorGUILayout.HelpBox(
                    "No tree prefabs were found. Press 'Refresh palette' (the selection is saved with the " +
                    "scene), or copy the tree prefabs to Assets/Resources/" +
                    "FantasyEnvironments/Environments/Ambient-Occlusion-Trees/Prefabs so the fallback can load them.",
                    MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "The hand-built town is never regenerated: chunks that overlap it are skipped and " +
                "the procedural ground and roads blend into its edges. Walk out of the north or " +
                "south gate to reach the endless country.",
                MessageType.Info);

            EditorGUILayout.EndScrollView();
        }

        static int Count(GameObject[] a) => a == null ? 0 : a.Length;
    }
}
#endif
