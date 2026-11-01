#if UNITY_EDITOR
using MysticMap;
using MysticMap.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MysticMap.EditorTools
{
    /// <summary>
    /// The spider fitting room (menu: MCP -> Enemies -> Spiders: size, offset & hit box).
    ///
    /// The spider's mesh and its collider are deliberately independent, so everything here can be
    /// changed without rebuilding anything:
    ///
    ///   * MESH - how large the spider looks, how far the mesh sits from the centre (up / forward /
    ///     sideways), and whether the lowest leg is glued to the ground. The feet-on-the-ground solver
    ///     is what fixes a spider that floats: the collider can only know one contact point, and the
    ///     walk / run clip moves the legs, so the mesh is measured (legs included) and slid onto the
    ///     real ground every few frames.
    ///   * HIT BOX - the CharacterController capsule. Keep the radius near the BODY, not the leg span.
    ///     A low step height also stops two spiders from climbing on top of each other.
    ///   * PERSONAL SPACE - how hard spiders steer around each other so they do not pile up.
    ///   * SIZE - the leg span used when the mesh is rebuilt from the pack's FBX.
    ///
    /// Everything is stored in EditorPrefs (like the map / magic windows) and written to the spider
    /// prefab, so it applies to every spider the director spawns from now on.
    /// </summary>
    public class SpiderSettingsWindow : EditorWindow
    {
        // ---- mesh -----------------------------------------------------------
        float modelScale = 1f;
        float modelHeight;
        float modelForward;
        float modelSide;

        // ---- feet on the ground ---------------------------------------------
        SpiderEnemy.FootAlign feetAlign = SpiderEnemy.FootAlign.Center;
        SpiderEnemy.FootPart footPart = SpiderEnemy.FootPart.Claws;
        float probeRadius = 0.6f;
        float footGap;
        float alignSpeed = 14f;

        // ---- hit box --------------------------------------------------------
        bool tuneCollider = true;
        float colliderRadius = 0.5f;
        float colliderHeight = 1.7f;
        float colliderCenterY = 0.85f;
        float colliderStep = 0.3f;

        // ---- personal space -------------------------------------------------
        float personalSpace = 2.5f;
        float separationPush = 2.5f;

        // ---- rebuild --------------------------------------------------------
        float legSpan = 2.3f;

        Vector2 _scroll;
        static GameObject _preview;

        [MenuItem("MCP/Enemies/Spiders: size, offset & hit box...")]
        public static void Open()
        {
            var w = GetWindow<SpiderSettingsWindow>(true, "Spider size & hit box");
            w.minSize = new Vector2(420, 560);
            w.Load();
        }

        void OnEnable() => Load();

        void OnDisable() => RemovePreview();      // the temporary spider never outlives the window

        void Load()
        {
            modelScale = EditorPrefs.GetFloat("MM.SpiderScale", 1f);
            modelHeight = EditorPrefs.GetFloat("MM.SpiderModelHeight", 0f);
            modelForward = EditorPrefs.GetFloat("MM.SpiderModelForward", 0f);
            modelSide = EditorPrefs.GetFloat("MM.SpiderModelSide", 0f);

            feetAlign = (SpiderEnemy.FootAlign)EditorPrefs.GetInt("MM.SpiderFeetAlign", (int)SpiderEnemy.FootAlign.Center);
            footPart = (SpiderEnemy.FootPart)EditorPrefs.GetInt("MM.SpiderFootPart", (int)SpiderEnemy.FootPart.Claws);
            probeRadius = EditorPrefs.GetFloat("MM.SpiderProbe", 0.6f);
            footGap = EditorPrefs.GetFloat("MM.SpiderFootGap", 0f);
            alignSpeed = EditorPrefs.GetFloat("MM.SpiderAlignSpeed", 14f);

            tuneCollider = EditorPrefs.GetBool("MM.SpiderTuneCollider", true);
            colliderRadius = EditorPrefs.GetFloat("MM.SpiderColRadius", 0.5f);
            colliderHeight = EditorPrefs.GetFloat("MM.SpiderColHeight", 1.7f);
            colliderCenterY = EditorPrefs.GetFloat("MM.SpiderColCenter", 0.85f);
            colliderStep = EditorPrefs.GetFloat("MM.SpiderColStep", 0.3f);

            personalSpace = EditorPrefs.GetFloat("MM.SpiderSpace", 2.5f);
            separationPush = EditorPrefs.GetFloat("MM.SpiderSpacePush", 2.5f);

            legSpan = EditorPrefs.GetFloat("MM.SpiderLegSpan", 2.3f);
        }

        void Save()
        {
            EditorPrefs.SetFloat("MM.SpiderScale", modelScale);
            EditorPrefs.SetFloat("MM.SpiderModelHeight", modelHeight);
            EditorPrefs.SetFloat("MM.SpiderModelForward", modelForward);
            EditorPrefs.SetFloat("MM.SpiderModelSide", modelSide);

            EditorPrefs.SetInt("MM.SpiderFeetAlign", (int)feetAlign);
            EditorPrefs.SetInt("MM.SpiderFootPart", (int)footPart);
            EditorPrefs.SetFloat("MM.SpiderProbe", probeRadius);
            EditorPrefs.SetFloat("MM.SpiderFootGap", footGap);
            EditorPrefs.SetFloat("MM.SpiderAlignSpeed", alignSpeed);

            EditorPrefs.SetBool("MM.SpiderTuneCollider", tuneCollider);
            EditorPrefs.SetFloat("MM.SpiderColRadius", colliderRadius);
            EditorPrefs.SetFloat("MM.SpiderColHeight", colliderHeight);
            EditorPrefs.SetFloat("MM.SpiderColCenter", colliderCenterY);
            EditorPrefs.SetFloat("MM.SpiderColStep", colliderStep);

            EditorPrefs.SetFloat("MM.SpiderSpace", personalSpace);
            EditorPrefs.SetFloat("MM.SpiderSpacePush", separationPush);

            EditorPrefs.SetFloat("MM.SpiderLegSpan", legSpan);
        }

        // =====================================================================
        //  GUI
        // =====================================================================
        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandWidth(true));
            GUILayout.Space(4);

            EditorGUILayout.HelpBox(
                "The mesh and the hit box are independent: these numbers change how the spider LOOKS and\n" +
                "how big its collider is, and they are written to the spider prefab, so every spider the\n" +
                "director spawns uses them. Press 'Apply', then 'Preview' to judge the fit in the scene.",
                MessageType.None);

            // ---- mesh ------------------------------------------------------
            GUILayout.Space(6);
            GUILayout.Label("MESH (size and offset from the centre)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            modelScale = EditorGUILayout.Slider("Mesh size (x)", modelScale, 0.2f, 3f);
            modelHeight = EditorGUILayout.Slider("Up / down (m)", modelHeight, -1.5f, 1.5f);
            modelForward = EditorGUILayout.Slider("Forward (m)", modelForward, -1.5f, 1.5f);
            modelSide = EditorGUILayout.Slider("Sideways (m)", modelSide, -1.5f, 1.5f);
            if (EditorGUI.EndChangeCheck()) Save();

            EditorGUILayout.HelpBox(
                "'Up / down' is the quick fix when the legs hover above the ground (negative sinks the\n" +
                "mesh into it). The offsets move the MESH only - the collider and the walking stay put.",
                MessageType.None);

            // ---- feet ------------------------------------------------------
            GUILayout.Space(6);
            GUILayout.Label("FEET ON THE GROUND", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            feetAlign = (SpiderEnemy.FootAlign)EditorGUILayout.EnumPopup("Ground tracking", feetAlign);
            if (feetAlign != SpiderEnemy.FootAlign.Off)
            {
                footPart = (SpiderEnemy.FootPart)EditorGUILayout.EnumPopup("Foot part", footPart);
                probeRadius = EditorGUILayout.Slider("Probe radius (m)", probeRadius, 0f, 2f);
                footGap = EditorGUILayout.Slider("Gap above the ground (m)", footGap, -0.3f, 0.3f);
                alignSpeed = EditorGUILayout.Slider("Follow speed", alignSpeed, 0f, 40f);
            }
            if (EditorGUI.EndChangeCheck()) Save();

            EditorGUILayout.HelpBox(
                "Keeps the feet on the real ground, whatever the walk / run clip does with the legs and\n" +
                "however the terrain rolls - this is what stops a spider floating. 'Foot part' picks what\n" +
                "has to touch the ground: Claws = the body and legs (the pack's fangs hang lower than the\n" +
                "feet, so this is what you normally want); Whole mesh = the lowest point of everything.\n" +
                "Follow speed 0 snaps every frame instead of gliding. 'Apply' also stands the prefab itself\n" +
                "on the ground, so it looks right in the Scene view and on the very first frame.",
                MessageType.None);

            DrawHitBox();
            DrawSpaceAndButtons();
        }

        void DrawHitBox()
        {
            GUILayout.Space(6);
            GUILayout.Label("HIT BOX (the CharacterController)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            tuneCollider = EditorGUILayout.Toggle("Tune the hit box at runtime", tuneCollider);
            using (new EditorGUI.DisabledScope(!tuneCollider))
            {
                colliderRadius = EditorGUILayout.Slider("Radius (m)", colliderRadius, 0.1f, 2f);
                colliderHeight = EditorGUILayout.Slider("Height (m)", colliderHeight, 0.3f, 4f);
                colliderCenterY = EditorGUILayout.Slider("Centre height (m)", colliderCenterY, 0f, 4f);
                colliderStep = EditorGUILayout.Slider("Step height (m)", colliderStep, 0f, 1f);
            }
            if (EditorGUI.EndChangeCheck()) Save();

            EditorGUILayout.HelpBox(
                "Keep the radius near the BODY: the legs reach 3-4x further, and a collider that wide\n" +
                "would shove the whole world around. 'Centre height' should be about half the height so\n" +
                "the capsule bottom sits on the ground. The small step height is what stops two spiders\n" +
                "from climbing onto each other (how one could end up floating on top of the other).",
                MessageType.None);
        }

        void DrawSpaceAndButtons()
        {
            GUILayout.Space(6);
            GUILayout.Label("PERSONAL SPACE", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            personalSpace = EditorGUILayout.Slider("Distance to keep (m)", personalSpace, 0f, 10f);
            separationPush = EditorGUILayout.Slider("Push apart", separationPush, 0f, 4f);
            if (EditorGUI.EndChangeCheck()) Save();

            GUILayout.Space(10);
            if (GUILayout.Button("Apply to the spider prefab (and any spider in the scene)", GUILayout.Height(26)))
                Defer(ApplyEverywhere);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Preview in the scene view")) Defer(ShowPreview);
            if (GUILayout.Button("Remove preview")) Defer(RemovePreview);
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Select the spider prefab")) Defer(SelectPrefab);

            GUILayout.Space(10);
            GUILayout.Label("REBUILD THE MESH (from the pack's FBX)", EditorStyles.boldLabel);
            legSpan = EditorGUILayout.Slider("Leg span (m)", legSpan, 0.5f, 6f);
            EditorGUILayout.HelpBox(
                "Only needed to change the SIZE baked into the mesh (the sliders above are runtime and need\n" +
                "no rebuild). The hit box is recalculated for the new size, then the sliders above are\n" +
                "re-applied. The scene keeps working - the prefab keeps its identity.",
                MessageType.None);

            if (GUILayout.Button("Rebuild the mesh at this leg span", GUILayout.Height(24)))
            {
                Defer(() =>
                {
                    if (SpiderSetup.BuildPrefab(true, legSpan) != null) ApplyEverywhere();
                });
            }
        }

        static void Defer(System.Action act)
        {
            EditorApplication.delayCall += () =>
            {
                if (act == null) return;
                try { act(); }
                catch (System.Exception e) { Debug.LogException(e); }
            };
        }

        // =====================================================================
        //  Applying
        // =====================================================================
        /// <summary>Writes every setting to the prefab and to any spider instance in the open scene.</summary>
        void ApplyEverywhere()
        {
            Save();

            string path = SpiderSetup.PrefabPath;
            int written = 0;

            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
            {
                GameObject contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    foreach (SpiderEnemy enemy in contents.GetComponentsInChildren<SpiderEnemy>(true))
                    {
                        Apply(enemy);
                        EditorUtility.SetDirty(enemy);
                        written++;
                    }

                    // Write the fit the numbers produce into the prefab itself (the pose the Scene view
                    // and the first frame of the game show), so a solved offset can still be nudged here.
                    foreach (SpiderEnemy enemy in contents.GetComponentsInChildren<SpiderEnemy>(true))
                        BakeRestingFit(enemy);

                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }

            int instances = 0;
            foreach (SpiderEnemy enemy in Object.FindObjectsByType<SpiderEnemy>(FindObjectsInactive.Include,
                                                                               FindObjectsSortMode.None))
            {
                if (enemy == null) continue;

                Apply(enemy);
                enemy.RefreshFit();          // show it straight away, without entering Play mode
                EditorUtility.SetDirty(enemy);
                instances++;
            }

            PrefabNeedsFit = false;

            AssetDatabase.SaveAssets();
            if (instances > 0) EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            SceneView.RepaintAll();

            Debug.Log("[MysticMap.Spiders] Settings applied to " + written + " prefab component(s) and " +
                      instances + " spider(s) in the scene.");
        }

        void Apply(SpiderEnemy e) => ApplyTo(e, Current());

        /// <summary>Every fit setting as one value, so it can be written anywhere (prefab, scene, rebuild).</summary>
        struct Fit
        {
            public float scale, height, forward, side;
            public SpiderEnemy.FootAlign feet;
            public SpiderEnemy.FootPart footPart;
            public float probe, gap, alignSpeed;
            public bool tuneCollider;
            public float colRadius, colHeight, colCenter, colStep;
            public float space, push;
        }

        Fit Current() => new Fit
        {
            scale = modelScale, height = modelHeight, forward = modelForward, side = modelSide,
            feet = feetAlign, footPart = footPart, probe = probeRadius, gap = footGap, alignSpeed = alignSpeed,
            tuneCollider = tuneCollider,
            colRadius = colliderRadius, colHeight = colliderHeight,
            colCenter = colliderCenterY, colStep = colliderStep,
            space = personalSpace, push = separationPush
        };

        static void ApplyTo(SpiderEnemy e, Fit fit)
        {
            if (e == null) return;

            e.modelScale = fit.scale;
            e.modelHeight = fit.height;
            e.modelForward = fit.forward;
            e.modelSide = fit.side;

            e.feetAlign = fit.feet;
            e.footPart = fit.footPart;
            e.probeRadius = fit.probe;
            e.footGap = fit.gap;
            e.alignSpeed = fit.alignSpeed;

            e.tuneCollider = fit.tuneCollider;
            e.colliderRadius = fit.colRadius;
            e.colliderHeight = fit.colHeight;
            e.colliderCenterY = fit.colCenter;
            e.colliderStepOffset = fit.colStep;

            e.personalSpace = fit.space;
            e.separationPush = fit.push;
        }

        /// <summary>
        /// Bakes the resting fit into a spider that lives in the prefab: 'Up / down' is solved from the
        /// animated idle pose, so the legs stand on y = 0 (the very spot the prefab is placed on - and the
        /// spot the CharacterController bottom sits on) even before the game runs. The runtime feet solver
        /// keeps them there afterwards, which is also what makes the slider still adjustable.
        /// </summary>
        public static void BakeRestingFit(SpiderEnemy e)
        {
            if (e == null || e.model == null) return;

            // Origin back to the prefab's own spot (the tuning window may have moved it).
            Transform root = e.transform;
            Vector3 origin = root.position;
            origin.y = 0f;
            root.position = origin;

            e.modelHeight = 0f;
            e.RefreshFit();

            float lowest = SpiderSetup.RestingLowestY(e);
            float above = SpiderSetup.OriginY(e);
            if (float.IsNaN(lowest) || float.IsNaN(above)) return;

            e.modelHeight = Mathf.Clamp(above - lowest, -2f, 2f);
            e.RefreshFit();
        }

        /// <summary>The key that says the window has been used at least once.</summary>
        const string SavedKey = "MM.SpiderScale";

        /// <summary>
        /// True while the fit saved in the window was written straight into a freshly built prefab (the
        /// builder never has a solver of its own). The prefab on disk still looks like the raw FBX pose
        /// until the window applies its numbers again, which happens by itself while the scene is open.
        /// </summary>
        public static bool PrefabNeedsFit { get; private set; }

        /// <summary>Remembers that a rebuilt prefab still has to be given the saved fit.</summary>
        public static void MarkNeedsFit(bool pending = true) => PrefabNeedsFit = pending;

        /// <summary>
        /// Re-applies the settings saved in the window to a prefab. <see cref="SpiderSetup"/> calls this
        /// after it has (re)built the prefab, so a rebuild never throws the tuning away.
        /// </summary>
        public static void ApplySavedFit(GameObject prefabAsset)
        {
            if (prefabAsset == null || !EditorPrefs.HasKey(SavedKey)) return;

            string path = AssetDatabase.GetAssetPath(prefabAsset);
            if (string.IsNullOrEmpty(path)) return;

            Fit fit = LoadSaved();

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                foreach (SpiderEnemy enemy in contents.GetComponentsInChildren<SpiderEnemy>(true))
                {
                    ApplyTo(enemy, fit);

                    // Enter Play mode with a freshly rebuilt prefab and the feet would start from the raw
                    // FBX pose (claws far below the body - a floating spider for the first frame). Standing
                    // the prefab on its feet here means it looks right from the first frame, and the
                    // numbers solved into 'Up / down' are what the tuning window edits afterwards.
                    if (enemy.builtBy != SpiderSetup.BuildVersion) BakeRestingFit(enemy);

                    EditorUtility.SetDirty(enemy);
                }

                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            PrefabNeedsFit = false;
        }

        static Fit LoadSaved() => new Fit
        {
            scale = EditorPrefs.GetFloat(SavedKey, 1f),
            height = EditorPrefs.GetFloat("MM.SpiderModelHeight", 0f),
            forward = EditorPrefs.GetFloat("MM.SpiderModelForward", 0f),
            side = EditorPrefs.GetFloat("MM.SpiderModelSide", 0f),
            feet = (SpiderEnemy.FootAlign)EditorPrefs.GetInt("MM.SpiderFeetAlign", (int)SpiderEnemy.FootAlign.Center),
            footPart = (SpiderEnemy.FootPart)EditorPrefs.GetInt("MM.SpiderFootPart", (int)SpiderEnemy.FootPart.Claws),
            probe = EditorPrefs.GetFloat("MM.SpiderProbe", 0.6f),
            gap = EditorPrefs.GetFloat("MM.SpiderFootGap", 0f),
            alignSpeed = EditorPrefs.GetFloat("MM.SpiderAlignSpeed", 14f),
            tuneCollider = EditorPrefs.GetBool("MM.SpiderTuneCollider", true),
            colRadius = EditorPrefs.GetFloat("MM.SpiderColRadius", 0.5f),
            colHeight = EditorPrefs.GetFloat("MM.SpiderColHeight", 1.7f),
            colCenter = EditorPrefs.GetFloat("MM.SpiderColCenter", 0.85f),
            colStep = EditorPrefs.GetFloat("MM.SpiderColStep", 0.3f),
            space = EditorPrefs.GetFloat("MM.SpiderSpace", 2.5f),
            push = EditorPrefs.GetFloat("MM.SpiderSpacePush", 2.5f)
        };

        // =====================================================================
        //  Preview
        // =====================================================================
        /// <summary>
        /// Drops a temporary (never saved) spider by the scene view camera, so the size, the offsets and
        /// the feet on the ground can be judged straight away.
        /// </summary>
        static void ShowPreview()
        {
            RemovePreview();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SpiderSetup.PrefabPath);
            if (prefab == null)
            {
                Debug.LogWarning("[MysticMap.Spiders] No spider prefab yet - build it first " +
                                 "(MCP > Enemies > 1.).");
                return;
            }

            SceneView view = SceneView.lastActiveSceneView;
            Vector3 pivot = view != null ? view.pivot : new Vector3(500f, 30f, 620f);

            _preview = (GameObject)PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene());
            _preview.name = "Spider Preview (temporary - not saved)";
            _preview.hideFlags = HideFlags.DontSave;
            _preview.transform.position = new Vector3(pivot.x, GroundHeightAt(pivot.x, pivot.z), pivot.z);

            Selection.activeGameObject = _preview;
            SceneView.RepaintAll();
        }

        static void RemovePreview()
        {
            if (_preview != null) DestroyImmediate(_preview);
            _preview = null;
        }

        static void SelectPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SpiderSetup.PrefabPath);
            if (prefab == null)
            {
                Debug.LogWarning("[MysticMap.Spiders] No spider prefab yet - build it first " +
                                 "(MCP > Enemies > 1.).");
                return;
            }

            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
        }

        static float GroundHeightAt(float x, float z)
        {
            var manager = Object.FindFirstObjectByType<ChunkManager>();
            if (manager != null) return manager.SampleHeight(x, z);

            if (Physics.Raycast(new Vector3(x, 600f, z), Vector3.down, out RaycastHit hit, 1200f,
                                ~0, QueryTriggerInteraction.Ignore))
                return hit.point.y;

            return 25f;
        }
    }
}
#endif
