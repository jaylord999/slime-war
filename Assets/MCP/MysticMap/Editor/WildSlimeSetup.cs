#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MysticMap.EditorTools
{
    /// <summary>
    /// Puts the wild slimes into the open scene.
    ///
    /// The slime art is one model with five colour variants (<c>anime_slime.fbx</c> plus
    /// <c>anime_slime (1..4).fbx</c> in <c>Assets/3dcharacters</c>), so this creates a
    /// <see cref="WildSlimeSpawner"/> on a "Wild Slimes" object, fills its list with every
    /// variant it finds and lets the spawner drop them around the player at random - each one
    /// gets a <see cref="SlimeWanderer"/> and walks / idles on its own.
    ///
    /// It runs by itself when the Mystic Map scene is opened and after you leave Play mode (so
    /// the spawner is never lost), and it can be re-run any time from
    /// MCP -> Slimes -> Wild slime spawner. Re-running keeps your tuned settings.
    /// </summary>
    [InitializeOnLoad]
    public static class WildSlimeSetup
    {
        /// <summary>Folder holding the slime models (all colour variants live together).</summary>
        public const string SlimeFolder = "Assets/3dcharacters";

        const string SpawnerName = "Wild Slimes";

        static WildSlimeSetup()
        {
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.delayCall += AutoInstall;
        }

        // =====================================================================
        //  Entry points
        // =====================================================================
        [MenuItem("MCP/Slimes/Wild slime spawner")]
        public static void InstallFromMenu()
        {
            Install(true);
        }

        static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (mode == OpenSceneMode.Single)
                EditorApplication.delayCall += AutoInstall;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += AutoInstall;
        }

        /// <summary>Adds the spawner only when the open scene is missing one.</summary>
        static void AutoInstall()
        {
            if (!CanRun()) return;

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return;

            foreach (var s in Object.FindObjectsByType<WildSlimeSpawner>(FindObjectsSortMode.None))
                if (s != null && s.gameObject.scene == scene) return;   // already there

            Install(false);
        }

        static bool CanRun()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode
                && !EditorApplication.isCompiling
                && !EditorApplication.isUpdating
                && !BuildPipeline.isBuildingPlayer;
        }

        // =====================================================================
        //  The install
        // =====================================================================
        /// <param name="saveScene">Save the scene when it changed (the menu item does, the
        /// automatic run does not, so opening a scene never writes to disk unexpectedly).</param>
        public static bool Install(bool saveScene)
        {
            if (!CanRun())
            {
                Debug.LogWarning("[MysticMap] The wild slimes can only be added in edit mode " +
                                 "(stop Play mode or wait for the recompile to finish).");
                return false;
            }

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return false;

            var models = FindSlimeModels();
            if (models.Count == 0)
            {
                Debug.LogError("[MysticMap] No slime models found in " + SlimeFolder +
                               " - the wild slimes were not added.");
                return false;
            }

            var changes = new StringBuilder();

            // ---- 1) The spawner object ---------------------------------------
            WildSlimeSpawner spawner = FindSpawner(scene);
            if (spawner == null)
            {
                var go = new GameObject(SpawnerName);
                Undo.RegisterCreatedObjectUndo(go, "Create the wild slime spawner");
                spawner = go.AddComponent<WildSlimeSpawner>();
                changes.Append("created '" + SpawnerName + "', ");
            }

            Undo.RegisterFullObjectHierarchyUndo(spawner.gameObject, "Set up the wild slimes");

            // ---- 2) Every colour variant, so the spawner can pick at random --
            // Always rewritten (not only when empty), so replacing or adding slime FBX files is
            // picked up by simply running this again.
            int before = spawner.slimePrefabs != null ? spawner.slimePrefabs.Length : 0;
            spawner.slimePrefabs = BuildPrefabList(models.ToArray(), spawner.slimePrefabs);
            changes.Append(spawner.slimePrefabs.Length + " slime variants");
            if (before != spawner.slimePrefabs.Length)
                changes.Append(" (was " + before + ")");
            changes.Append(", ");

            // ---- 3) The player is what they are spread around -----------------
            var player = Object.FindFirstObjectByType<SlimePlayer>(FindObjectsInactive.Exclude);
            if (player != null)
            {
                spawner.target = player.transform;
                spawner.autoFindPlayer = true;

                // Park the spawner at the player so it is easy to find in the hierarchy.
                if (spawner.transform.parent == null)
                    spawner.transform.position = player.transform.position;
            }

            // ---- 4) Never spawn the player's own slime as a wild one ----------
            // The player slime lives on the Player object (or is an instance of one of these
            // models), and a wandering copy of it would be confusing.
            if (player != null)
            {
                var filtered = new List<GameObject>();
                foreach (GameObject prefab in spawner.slimePrefabs)
                {
                    if (prefab == null) continue;
                    if (IsThePlayerModel(prefab, player.gameObject)) continue;
                    if (IsTheBossModel(prefab, scene)) continue;      // never a giant boss as a wild slime
                    filtered.Add(prefab);
                }

                if (filtered.Count > 0 && filtered.Count != spawner.slimePrefabs.Length)
                {
                    spawner.slimePrefabs = filtered.ToArray();
                    changes.Append("non-slime models excluded, ");
                }
            }

            // ---- 5) Fill in the rest of the tunables on a brand new spawner ---
            ApplyDefaults(spawner);

            EditorUtility.SetDirty(spawner);

            EditorSceneManager.MarkSceneDirty(scene);
            if (saveScene) EditorSceneManager.SaveScene(scene);

            Selection.activeGameObject = spawner.gameObject;
            SceneView.RepaintAll();

            Debug.Log("[MysticMap] Wild slimes ready on '" + spawner.name + "': " +
                      changes.ToString().TrimEnd(' ', ',') +
                      ". They spawn at random around the player, hop to random spots and idle.");
            return true;
        }

        // =====================================================================
        //  Finding things
        // =====================================================================
        /// <summary>
        /// The spawner's list, rebuilt so it always matches what is on disk. A slot the user
        /// filled by hand is kept as long as the array is not being resized (Unity throws when a
        /// serialised array is written at a new length through the inspector stream).
        /// </summary>
        static GameObject[] BuildPrefabList(GameObject[] found, GameObject[] current)
        {
            if (current == null || current.Length != found.Length) return found;

            var result = new GameObject[found.Length];
            for (int i = 0; i < found.Length; i++)
                result[i] = current[i] != null ? current[i] : found[i];
            return result;
        }

        /// <summary>Only fills a spawner that still has the stock values (a fresh object).</summary>
        static void ApplyDefaults(WildSlimeSpawner spawner)
        {
            if (spawner == null) return;

            if (spawner.maxSlimes <= 0) spawner.maxSlimes = 14;
            if (spawner.spawnInterval <= 0f) spawner.spawnInterval = 0.45f;
            if (spawner.maxRadius <= spawner.minRadius) spawner.maxRadius = spawner.minRadius + 25f;
            if (spawner.despawnRadius <= spawner.maxRadius) spawner.despawnRadius = spawner.maxRadius + 36f;
        }

        /// <summary>
        /// True when a slime prefab is what the player is made of.
        /// </summary>
        static bool IsThePlayerModel(GameObject prefab, GameObject player)
        {
            if (prefab == null || player == null) return false;

            // The player object itself is the model (the FBX usually sits on a child of it).
            if (prefab == player) return true;

            string prefabName = prefab.name;
            foreach (Transform child in player.GetComponentsInChildren<Transform>(true))
            {
                if (child == null || child == player.transform) continue;
                if (child.name == prefabName) return true;   // e.g. the "anime_slime" child
            }
            return false;
        }

        /// <summary>
        /// True when a slime prefab is the boss's own model. A boss is a slime model instance with a
        /// <see cref="SlimeBoss"/> on it, scaled up - summoning that again would spawn a second boss
        /// sized slime, which is exactly what a "massive minion" looks like.
        /// </summary>
        static bool IsTheBossModel(GameObject prefab, Scene scene)
        {
            if (prefab == null) return true;

            // The boss object itself (or anything that carries the boss script).
            if (prefab.GetComponent<SlimeBoss>() != null) return true;
            if (prefab.name == SlimeBossSetup.BossName) return true;

            // The boss that lives in this scene, and the model it is built from.
            SlimeBoss boss = FindBossIn(scene);
            if (boss == null) return false;

            if (prefab == boss.gameObject) return true;

            string bossModel = boss.model != null ? boss.model.name : null;
            return !string.IsNullOrEmpty(bossModel) && prefab.name == bossModel;
        }

        /// <summary>The slime boss of a scene, if it has one.</summary>
        static SlimeBoss FindBossIn(Scene scene)
        {
            foreach (var b in Object.FindObjectsByType<SlimeBoss>(FindObjectsSortMode.None))
                if (b != null && b.gameObject.scene == scene) return b;
            return null;
        }

        static WildSlimeSpawner FindSpawner(Scene scene)
        {
            foreach (var s in Object.FindObjectsByType<WildSlimeSpawner>(FindObjectsSortMode.None))
                if (s != null && s.gameObject.scene == scene) return s;
            return null;
        }

        /// <summary>Every slime model in <see cref="SlimeFolder"/>, in a stable order.</summary>
        static List<GameObject> FindSlimeModels()
        {
            var models = new List<GameObject>();

            if (!AssetDatabase.IsValidFolder(SlimeFolder)) return models;

            string[] guids = AssetDatabase.FindAssets("anime_slime t:GameObject", new[] { SlimeFolder });
            var paths = new List<string>();
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // The importer exposes the model as a GameObject whose path ends in ".fbx".
                if (!string.IsNullOrEmpty(path) && path.Contains("anime_slime")) paths.Add(path);
            }

            // AssetDatabase order is not guaranteed, so sort it: anime_slime, (1), (2), ...
            paths.Sort(System.StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model != null && !models.Contains(model)) models.Add(model);
            }

            return models;
        }
    }
}
#endif