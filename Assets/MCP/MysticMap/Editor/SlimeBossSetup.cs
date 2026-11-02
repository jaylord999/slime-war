#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MysticMap.EditorTools
{
    /// <summary>
    /// Adds the Slime King to the open scene: a very large slime that never walks, faces the
    /// player, casts a kit of Hovl Studio spells on a short cooldown and summons minions out of
    /// magic circles on the ground.
    ///
    /// The tool does the boring wiring: it picks the slime model, gives the boss its own copy of
    /// the slime (so scaling it does not touch the wild ones), loads the spell prefabs out of the
    /// magic pack, sets the magic circle effect from Resources and fills the minion list from the
    /// wild slimes of the scene.
    ///
    /// Run it from MCP -> Slimes -> Slime boss, or let it add the boss the first time the Mystic
    /// Map scene is opened. Running it again keeps the settings you tuned - it only fills in what
    /// is empty.
    /// </summary>
    [InitializeOnLoad]
    public static class SlimeBossSetup
    {
        public const string SlimeFolder = "Assets/3dcharacters";
        public const string PackRoot = "Assets/Hovl Studio/Magic effects pack/Prefabs";
        public const string ResourcesRoot = "Assets/Resources/ThirdPersonMagicFX";

        public const string BossName = "Slime King";

        static SlimeBossSetup()
        {
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorApplication.delayCall += AutoInstall;
        }

        [MenuItem("MCP/Slimes/Slime boss")]
        public static void InstallFromMenu()
        {
            Install(true);
        }

        static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (mode == OpenSceneMode.Single) EditorApplication.delayCall += AutoInstall;
        }

        static bool CanRun()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode
                && !EditorApplication.isCompiling
                && !EditorApplication.isUpdating
                && !BuildPipeline.isBuildingPlayer;
        }

        static void AutoInstall()
        {
            if (!CanRun()) return;

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return;

            foreach (var boss in Object.FindObjectsByType<SlimeBoss>(FindObjectsSortMode.None))
                if (boss != null && boss.gameObject.scene == scene) return;   // already there

            Install(false);
        }

        // =====================================================================
        //  The install
        // =====================================================================
        public static bool Install(bool saveScene)
        {
            if (!CanRun())
            {
                Debug.LogWarning("[MysticMap] The slime boss can only be added in edit mode.");
                return false;
            }

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return false;

            var player = Object.FindFirstObjectByType<SlimePlayer>(FindObjectsInactive.Exclude);
            Vector3 spot = FindSpot(player);

            // ---- 1) The boss object (its own instance of the slime) --------------
            SlimeBoss boss = FindBoss(scene);
            if (boss == null)
            {
                GameObject model = LoadSlimeModel();
                if (model == null)
                {
                    Debug.LogError("[MysticMap] No slime model found in " + SlimeFolder +
                                   " - the boss was not added.");
                    return false;
                }

                GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(model);
                if (go == null) go = Object.Instantiate(model);
                go.name = BossName;
                go.transform.position = spot;
                go.transform.rotation = Quaternion.identity;

                Undo.RegisterCreatedObjectUndo(go, "Create the slime boss");
                boss = go.AddComponent<SlimeBoss>();
            }

            Undo.RegisterFullObjectHierarchyUndo(boss.gameObject, "Set up the slime boss");

            // ---- 2) Who it fights ----------------------------------------------
            if (player != null)
            {
                boss.target = player.transform;
                boss.autoFindPlayer = true;
            }

            // ---- 3) The spell kit + the effects ---------------------------------
            if (boss.spells == null || boss.spells.Length == 0) boss.spells = BuildSpellKit();
            if (boss.circleEffect == null) boss.circleEffect = FindCircle();
            if (boss.summonBurst == null) boss.summonBurst = FindPrefab(ResourcesRoot, "AttackBurst");
            if (boss.impactEffect == null) boss.impactEffect = FindPrefab(ResourcesRoot, "AttackBurst");

            // ---- 4) Its minions (the slime models of the folder, never the boss itself) --------
            var minions = new List<GameObject>();
            foreach (GameObject model in LoadSlimeModels())
            {
                if (model == null) continue;
                if (IsTheBossModel(model, scene)) continue;
                minions.Add(model);
            }

            // Nothing usable on disk: fall back to the wild slimes of the scene.
            if (minions.Count == 0)
            {
                var spawner = Object.FindFirstObjectByType<WildSlimeSpawner>(FindObjectsInactive.Exclude);
                if (spawner != null && spawner.slimePrefabs != null)
                    foreach (GameObject prefab in spawner.slimePrefabs)
                        if (prefab != null && !IsTheBossModel(prefab, scene)) minions.Add(prefab);
            }

            if (minions.Count > 0 && (boss.minionPrefabs == null || boss.minionPrefabs.Length != minions.Count))
                boss.minionPrefabs = minions.ToArray();

            EditorUtility.SetDirty(boss);

            EditorSceneManager.MarkSceneDirty(scene);
            if (saveScene) EditorSceneManager.SaveScene(scene);

            Selection.activeGameObject = boss.gameObject;
            SceneView.RepaintAll();

            Debug.Log("[MysticMap] The slime boss is in the scene on '" + boss.name + "': " +
                      (boss.spells != null ? boss.spells.Length : 0) + " spells, summons " +
                      boss.summonCount + " slimes out of magic circles.");
            return true;
        }

        // =====================================================================
        //  Finding things
        // =====================================================================
        static SlimeBoss FindBoss(Scene scene)
        {
            foreach (var b in Object.FindObjectsByType<SlimeBoss>(FindObjectsSortMode.None))
                if (b != null && b.gameObject.scene == scene) return b;
            return null;
        }

        /// <summary>
        /// True for the boss's own model. The boss is a slime model with the boss script on it and a
        /// size multiplier, so it must never end up in a summon list - that is what produced
        /// gigantic "minions".
        /// </summary>
        static bool IsTheBossModel(GameObject prefab, Scene scene)
        {
            if (prefab == null) return true;
            if (prefab.GetComponent<SlimeBoss>() != null) return true;
            if (prefab.name == BossName) return true;

            SlimeBoss boss = FindBoss(scene);
            if (boss == null) return false;

            if (prefab == boss.gameObject) return true;

            string bossModel = boss.model != null ? boss.model.name : null;
            return !string.IsNullOrEmpty(bossModel) && prefab.name == bossModel;
        }

        /// <summary>A clear spot a little way in front of the player, on the ground.</summary>
        static Vector3 FindSpot(SlimePlayer player)
        {
            if (player == null) return new Vector3(500f, 45f, 520f);

            Vector3 forward = player.FacingDirection;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;

            Vector3 spot = player.transform.position + forward * 14f;

            var chunks = Object.FindFirstObjectByType<MysticMap.World.ChunkManager>(FindObjectsInactive.Exclude);
            if (chunks != null)
            {
                try { spot.y = chunks.SampleHeight(spot.x, spot.z); }
                catch { /* keep the player's height */ }
            }
            return spot;
        }

        /// <summary>The slime models of the folder, in a stable order.</summary>
        static List<GameObject> LoadSlimeModels()
        {
            var models = new List<GameObject>();
            if (!AssetDatabase.IsValidFolder(SlimeFolder)) return models;

            string[] guids = AssetDatabase.FindAssets("anime_slime t:GameObject", new[] { SlimeFolder });
            var paths = new List<string>();
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && path.Contains("anime_slime")) paths.Add(path);
            }

            paths.Sort(System.StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model != null && !models.Contains(model)) models.Add(model);
            }
            return models;
        }

        /// <summary>The first slime model in the folder (the boss scales it up itself).</summary>
        static GameObject LoadSlimeModel()
        {
            List<GameObject> models = LoadSlimeModels();
            return models.Count > 0 ? models[0] : null;
        }

        static GameObject FindPrefab(string folder, string name)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return null;

            string[] guids = AssetDatabase.FindAssets(name + " t:GameObject", new[] { folder });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                string file = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!string.Equals(file, name, System.StringComparison.OrdinalIgnoreCase)) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null) return prefab;
            }
            return null;
        }

        /// <summary>The magic circle laid on the ground where the minions come out.</summary>
        static GameObject FindCircle()
        {
            GameObject circle = FindPrefab(ResourcesRoot, "MagicCircle");
            if (circle != null) return circle;

            circle = FindPrefab(PackRoot + "/Magic circles", "Magic circle");
            if (circle != null) return circle;

            return FindPrefab(PackRoot + "/Magic circles", "Magic circle 2");
        }

        // =====================================================================
        //  The spell kit
        // =====================================================================
        /// <summary>
        /// Five spells, each with its own distance band so the boss mixes it up: a meteor storm
        /// from far away, a slash for mid range, a nova for when the player hugs it, crystals that
        /// erupt under them and a shockwave that tears up the ground.
        /// </summary>
        static SlimeBoss.BossSpell[] BuildSpellKit()
        {
            return new[]
            {
                // 1) Meteor storm dropped on the player from anywhere in range.
                new SlimeBoss.BossSpell
                {
                    name = "Meteors",
                    kind = SlimeBoss.BossSpellKind.GroundCast,
                    effect = FindPrefab(PackRoot + "/AoE effects", "Meteors AOE"),
                    minRange = 10f, maxRange = 40f, weight = 1.2f,
                    damage = 26f, radius = 4.2f, environmentScale = 1.1f,
                    effectScale = 0.9f
                },

                // 2) A crescent of magic that travels to the player.
                new SlimeBoss.BossSpell
                {
                    name = "Slash",
                    kind = SlimeBoss.BossSpellKind.Projectile,
                    effect = FindPrefab(PackRoot + "/Slash effects", "Charge slash purple"),
                    minRange = 5f, maxRange = 32f, weight = 1.4f,
                    damage = 16f, radius = 1.6f, speed = 19f, hugGround = true, hugHeight = 0.9f,
                    effectScale = 1.1f
                },

                // 3) Everything close to the boss is blown away.
                new SlimeBoss.BossSpell
                {
                    name = "Nova",
                    kind = SlimeBoss.BossSpellKind.Nova,
                    effect = FindPrefab(PackRoot + "/AoE effects", "Plexus AoE"),
                    minRange = 0f, maxRange = 8f, weight = 1.6f,
                    damage = 22f, radius = 6.5f, knockback = 5f,
                    effectScale = 1f
                },

                // 4) Crystals erupt under the player at close / mid range.
                new SlimeBoss.BossSpell
                {
                    name = "Crystals",
                    kind = SlimeBoss.BossSpellKind.GroundCast,
                    effect = FindPrefab(PackRoot + "/AoE effects", "Crystals front attack"),
                    minRange = 0f, maxRange = 18f, weight = 0.9f,
                    damage = 20f, radius = 3.6f,
                    effectScale = 0.8f
                },

                // 5) The ground itself is torn up around the boss.
                new SlimeBoss.BossSpell
                {
                    name = "Shockwave",
                    kind = SlimeBoss.BossSpellKind.Shockwave,
                    effect = FindPrefab(PackRoot + "/AoE effects", "Ground AOE explosion"),
                    minRange = 0f, maxRange = 26f, weight = 0.7f,
                    damage = 10f, radius = 10f, environmentScale = 1.5f,
                    effectScale = 1f
                }
            };
        }
    }
}
#endif