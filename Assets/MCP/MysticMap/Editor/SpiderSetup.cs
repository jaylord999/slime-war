#if UNITY_EDITOR
using System.Collections.Generic;
using MysticMap;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MysticMap.EditorTools
{
    /// <summary>
    /// Puts the "Assets/fantasySpider" spider into the Mystic Map as a hostile creature.
    ///
    /// It does the two halves of the job:
    ///   1. Builds a ready-to-use spider prefab from the pack:
    ///        * the animated FBX, scaled to a real-world size and stood on the ground,
    ///        * its own LEGACY animation clips (idle / walk / run / attack1 / attack2 / death2)
    ///          wired to the Animation component <see cref="SpiderEnemy"/> plays them through
    ///          (the pack's clips are legacy clips, so they cannot go into an AnimatorController),
    ///        * a CharacterController so it walks the streamed terrain,
    ///        * a Health so the slime's magic can kill it,
    ///        * a <see cref="SpiderEnemy"/> with its long-range spell filled from the Hovl
    ///          "Magic effects pack" (a venom slash that flies at the player).
    ///   2. Adds the spawner to the scene:
    ///        * a "Spider Director" that keeps two spiders alive around the player,
    ///        * a <see cref="PlayerVitals"/> on the slime so the spiders can actually hurt it.
    ///
    /// It runs by itself when the Mystic Map scene is opened (like the slime / magic setup tools)
    /// and can be re-run any time from MCP -> Enemies.
    /// </summary>
    [InitializeOnLoad]
    public static class SpiderSetup
    {
        /// <summary>The imported spider model from the pack.</summary>
        public const string ModelPath = "Assets/fantasySpider/spider_myOldOne.FBX";

        // ---- Where the generated assets live --------------------------------
        const string GeneratedDir = "Assets/MCP/MysticMap/Generated";
        const string SpiderDir = GeneratedDir + "/Spider";

        /// <summary>The spider prefab this tool builds (used by the scene installer and the tuning window).</summary>
        public const string PrefabPath = SpiderDir + "/FantasySpider.prefab";

        // ---- Effect pack ----------------------------------------------------
        const string HovlRoot = "Assets/Hovl Studio/Magic effects pack/Prefabs";
        const string PropsDir = "Assets/FantasyEnvironments/Environments/Prefabs";

        // ---- Scene ----------------------------------------------------------
        const string SceneName = "MysticMap";
        const string DirectorName = "Spider Director";

        /// <summary>
        /// Bumped whenever the prefab builder starts writing something new into the spider. A prefab
        /// built by an older version is rebuilt once (its settings are regenerated, and the fit
        /// tuning in MCP > Enemies > Spiders is re-applied from the saved preferences).
        /// </summary>
        public const int BuildVersion = 3;

        // ---- Sizing ---------------------------------------------------------
        const float TargetSpan = 2.3f;      // metres across the legs
        const float TargetHeight = 1.1f;    // metres from the ground to the top of the body

        static SpiderSetup()
        {
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.delayCall += AutoInstall;
        }

        // Called once more by the initialized hook so a prefab the builder has just rebuilt is given the
        // saved fit (the window which owns those numbers may not be open yet). Nothing happens unless the
        // builder had to rebuild without any fit to apply.
        [InitializeOnLoadMethod]
        static void ApplyPendingFit()
        {
            if (!SpiderSettingsWindow.PrefabNeedsFit) return;

            EditorApplication.delayCall += () =>
            {
                if (!SpiderSettingsWindow.PrefabNeedsFit) return;

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                if (prefab != null) SpiderSettingsWindow.ApplySavedFit(prefab);

                SpiderSettingsWindow.MarkNeedsFit(false);
                AssetDatabase.SaveAssets();
            };
        }

        // =====================================================================
        //  Menu
        // =====================================================================
        [MenuItem("MCP/Enemies/1. Build the spider prefab (from fantasySpider)")]
        public static void BuildPrefabMenu()
        {
            GameObject prefab = BuildPrefab(true);
            if (prefab != null) Selection.activeObject = prefab;
        }

        [MenuItem("MCP/Enemies/2. Add spiders to the scene")]
        public static void AddToSceneMenu()
        {
            OpenGameScene();       // make sure we are editing the game scene, not some other one
            Install(true);
        }

        /// <summary>Opens the Mystic Map scene when it is not the active one.</summary>
        static void OpenGameScene()
        {
            const string scenePath = "Assets/Scenes/MysticMap.unity";

            if (SceneManager.GetActiveScene().name == SceneName) return;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null) return;

            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        }

        [MenuItem("MCP/Enemies/Spiders: size, offset & hit box...")]
        public static void OpenTuningWindow() => SpiderSettingsWindow.Open();

        [MenuItem("MCP/Enemies/Remove spiders from the scene")]
        public static void RemoveFromSceneMenu()
        {
            var director = GameObject.Find(DirectorName);
            if (director != null) Undo.DestroyObjectImmediate(director);

            var vitals = Object.FindObjectsByType<PlayerVitals>();
            foreach (PlayerVitals v in vitals)
            {
                if (v == null) continue;
                Health health = v.GetComponent<Health>();
                Undo.DestroyObjectImmediate(v);
                if (health != null) Undo.DestroyObjectImmediate(health);
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log("[MysticMap.Spiders] Removed the spider director and the player health.");
        }

        static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (mode == OpenSceneMode.Single) EditorApplication.delayCall += AutoInstall;
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode) EditorApplication.delayCall += AutoInstall;
        }

        static void AutoInstall()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != SceneName) return;

            Install(false);
        }

        // =====================================================================
        //  The spider prefab
        // =====================================================================
        /// <summary>Builds (or rebuilds) the spider prefab from the pack at the default size.</summary>
        public static GameObject BuildPrefab(bool report) => BuildPrefab(report, 0f);

        /// <summary>Builds (or rebuilds) the spider prefab from the pack.</summary>
        /// <param name="legSpan">Metres across the legs (0 = the default size from the tuning window).</param>
        public static GameObject BuildPrefab(bool report, float legSpan)
        {
            EnsureFolders();

            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (modelAsset == null)
            {
                if (report) Debug.LogWarning("[MysticMap.Spiders] No model at " + ModelPath + ".");
                return null;
            }

            // Scratch objects: built, measured, saved as a prefab, then thrown away.
            var root = new GameObject("FantasySpider");
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var model = (GameObject)Object.Instantiate(modelAsset, root.transform);
            model.name = "Spider Model";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;

            StripBakedHelpers(model);

            float targetSpan = legSpan > 0.05f ? legSpan : TargetSpan;
            Vector3 size = FitModel(model.transform, targetSpan);

            Animation animation = BuildAnimation(model);

            BuildCharacterController(root, size);
            BuildHealth(root);
            BuildEnemy(root, animation, model.transform, size);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();

            if (report)
            {
                Debug.Log(saved != null
                    ? "[MysticMap.Spiders] Built " + PrefabPath + " (spider " + size.x.ToString("0.0") + " x " +
                      size.z.ToString("0.0") + " m across, " + size.y.ToString("0.0") + " m tall, with the " +
                      "pack's legacy Animation clips: idle / walk / run / attack1 / attack2 / death2)."
                    : "[MysticMap.Spiders] Could not save the spider prefab.");
            }

            return saved;
        }

        /// <summary>
        /// Wires the pack's LEGACY clips to a legacy Animation component on the model and drops any
        /// Animator (legacy clips cannot live in an AnimatorController, which is exactly how the
        /// spider pack ships).
        /// </summary>
        static Animation BuildAnimation(GameObject model)
        {
            foreach (Animator animator in model.GetComponentsInChildren<Animator>(true))
                if (animator != null) Object.DestroyImmediate(animator);

            var animation = model.GetComponent<Animation>();
            if (animation == null) animation = model.AddComponent<Animation>();

            animation.playAutomatically = false;
            animation.cullingType = AnimationCullingType.BasedOnRenderers;

            // The FBX import already puts its clips on this component (importAnimation is on for the
            // spider pack), so the prefab keeps them. The AssetDatabase only hands the clip list over
            // while the model happens to be fully loaded, so this top-up is best-effort; SpiderEnemy
            // plays the clips by name and copes with whatever is really there.
            foreach (AnimationClip clip in LoadClips())
            {
                if (clip == null) continue;
                if (animation.GetClip(clip.name) != null) continue;

                animation.AddClip(clip, clip.name);
            }

            return animation;
        }

        /// <summary>Scales the model to a real-world size and stands it on its feet (y = 0).</summary>
        static Vector3 FitModel(Transform model, float targetSpan)
        {
            Vector3 size = Vector3.one * TargetHeight;

            if (!TryBounds(model.gameObject, out Bounds bounds)) return size;

            float span = Mathf.Max(bounds.size.x, bounds.size.z);
            float scale = span > 0.001f ? Mathf.Clamp(targetSpan / span, 0.0001f, 10000f) : 1f;
            model.localScale = Vector3.one * scale;

            if (!TryBounds(model.gameObject, out Bounds scaled)) return bounds.size * scale;

            Vector3 p = model.localPosition;
            p.y -= scaled.min.y;
            model.localPosition = p;

            return scaled.size;
        }

        /// <summary>The world-space bounds of everything the object draws (identity parents assumed).</summary>
        static bool TryBounds(GameObject go, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;

            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;

                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }

            return any;
        }

        /// <summary>Drops any camera / light child the FBX import may have brought along.</summary>
        static void StripBakedHelpers(GameObject model)
        {
            var doomed = new List<GameObject>();

            foreach (Transform child in model.GetComponentsInChildren<Transform>(true))
            {
                if (child == null || child.gameObject == model) continue;
                if (child.GetComponent<Renderer>() != null) continue;
                if (child.GetComponent<Camera>() == null && child.GetComponent<Light>() == null) continue;

                doomed.Add(child.gameObject);
            }

            foreach (GameObject go in doomed) Object.DestroyImmediate(go);
        }

        /// <summary>
        /// The hit box a spider of this size gets: a slim capsule that follows the BODY, not the leg
        /// span (the legs stick out 3-4x further than the body, and a collider that wide would make
        /// the spider shove the whole world around).
        /// </summary>
        static void ColliderFit(Vector3 size, out float radius, out float height, out float centerY)
        {
            float span = Mathf.Max(size.x, size.z);
            radius = Mathf.Clamp(span * 0.22f, 0.25f, 0.9f);
            height = Mathf.Max(Mathf.Max(size.y * 0.9f, TargetHeight * 0.7f), radius * 2.05f);
            centerY = height * 0.5f;
        }

        static void BuildCharacterController(GameObject root, Vector3 size)
        {
            var cc = root.AddComponent<CharacterController>();

            ColliderFit(size, out float radius, out float height, out float centerY);

            cc.radius = radius;
            cc.height = height;
            cc.center = new Vector3(0f, centerY, 0f);
            cc.slopeLimit = 55f;
            // A LOW step: enough for a pebble, too low for a spider to climb onto another spider.
            cc.stepOffset = 0.3f;
            cc.skinWidth = 0.05f;
            cc.minMoveDistance = 0f;
        }

        static void BuildHealth(GameObject root)
        {
            var health = root.AddComponent<Health>();
            health.maxHealth = 140f;
            health.destroyOnDeath = true;
            health.destroyDelay = 1.5f;
            health.hitEffect = LoadEffect("Hits and explosions", "Green hit");
            health.deathEffect = LoadEffect("Hits and explosions", "Explosion");
            health.flashColor = new Color(0.7f, 1f, 0.75f, 1f);
            health.stirEnvironment = true;
            health.environmentRadius = 3.5f;
            health.debrisOnDeath = 10;
            health.debrisPrefabs = LoadDebris();
        }

        static void BuildEnemy(GameObject root, Animation animation, Transform model, Vector3 size)
        {
            var enemy = root.AddComponent<SpiderEnemy>();
            enemy.spiderAnimation = animation;
            enemy.hitMask = ~0;
            enemy.biteImpactEffect = LoadEffect("Hits and explosions", "Green hit");
            enemy.castChargeEffect = LoadEffect("Sparks", "Sparks flashing green");
            enemy.spell = BuildSpell();

            // Remember where this build put the mesh: the "Mesh (size and offset)" sliders in the
            // Inspector are always applied on top of this, so they can never drift.
            enemy.model = model;
            if (model != null)
            {
                enemy.modelBasePosition = model.localPosition;
                enemy.modelBaseScale = model.localScale;
            }
            enemy.modelBaseSet = true;

            // Write the hit box this build chose into the component, so the Inspector shows the real
            // numbers and they can be tuned from there (see MCP > Enemies > Spiders: size & hit box).
            ColliderFit(size, out float radius, out float height, out float centerY);
            enemy.tuneCollider = true;
            enemy.colliderRadius = radius;
            enemy.colliderHeight = height;
            enemy.colliderCenterY = centerY;
            enemy.colliderStepOffset = 0.3f;

            enemy.builtBy = BuildVersion;
        }

        /// <summary>
        /// The long-range spell: a green venom slash that flies at the player.
        /// It uses the same pack prefabs the slime's own travelling spells use ("AoE slash green"),
        /// so it is a proven projectile and behaves exactly like the player's blade.
        /// </summary>
        static MagicSpellLevel BuildSpell()
        {
            return new MagicSpellLevel
            {
                name = "Venom Slash",
                school = "Venom",
                delivery = SpellDelivery.Projectile,
                holdToReach = 0f,
                cooldown = 5f,
                damage = 8f,
                castDistance = 3f,
                radius = 0.6f,
                speed = 19f,
                hugGround = true,
                hugHeight = 0.8f,
                pierce = false,
                splash = 0f,
                splashRadius = 1.5f,
                castScale = 0.55f,
                shake = 0f,
                knockback = 2f,
                environmentScale = 0.7f,
                castEffect = LoadEffect("AoE effects", "AoE slash green"),
                impactEffect = LoadEffect("Hits and explosions", "Green hit"),
                maxChargeRadius = 0f
            };
        }

        // =====================================================================
        //  Asset helpers
        // =====================================================================
        /// <summary>Every animation clip that lives inside the spider FBX (idle, walk, run, ...).</summary>
        static AnimationClip[] LoadClips()
        {
            var clips = new List<AnimationClip>();

            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(ModelPath))
            {
                var clip = asset as AnimationClip;
                if (clip != null) clips.Add(clip);
            }

            return clips.ToArray();
        }

        static GameObject LoadEffect(string folder, string name) =>
            AssetDatabase.LoadAssetAtPath<GameObject>(HovlRoot + "/" + folder + "/" + name + ".prefab");

        /// <summary>A few small stones the spider throws when it dies.</summary>
        static GameObject[] LoadDebris()
        {
            string[] names = { "Stone1", "Stone2", "Rock1", "Rock2" };
            var list = new List<GameObject>();

            foreach (string n in names)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(PropsDir + "/" + n + ".prefab");
                if (go != null) list.Add(go);
            }

            return list.ToArray();
        }

        // =====================================================================
        //  Resting fit (what the prefab looks like while it stands still)
        // =====================================================================
        /// <summary>
        /// The world height of the lowest thing the model draws with the clip it stands in (idle), measured
        /// from the live skinned vertices. It is exactly what the runtime feet solver measures, using the
        /// spider's own <see cref="SpiderEnemy.footPart"/>, so the baked prefab and the game agree.
        /// </summary>
        public static float RestingLowestY(SpiderEnemy enemy)
        {
            if (enemy == null || enemy.model == null) return float.NaN;

            EnemyPose.Sample(enemy, enemy.idleClip);
            EnemyPose.Release();

            return EnemyPose.Lowest(enemy, enemy.footPart);
        }

        /// <summary>The world height of the spider's own origin (the object the CharacterController sits on).</summary>
        public static float OriginY(SpiderEnemy enemy) => enemy == null ? float.NaN : enemy.transform.position.y;

        /// <summary>
        /// Poses the pack's spider in one of its clips off the clock, so the fit can be measured (and
        /// baked) without Play mode, and reads the lowest animated vertex the same way a skin shader
        /// deforms it.
        /// </summary>
        static class EnemyPose
        {
            public static void Sample(SpiderEnemy enemy, string clip)
            {
                Animation animation = enemy != null ? enemy.spiderAnimation : null;
                if (animation == null) return;

                animation.Stop();                       // leave every clip on its first frame

                if (!string.IsNullOrEmpty(clip) && animation.GetClip(clip) != null) animation.Play(clip);

                Physics.SyncTransforms();
                animation.Sample();
                Physics.SyncTransforms();
            }

            /// <summary>Leaves the model posed (the prefab is saved like that, so the Scene view matches).</summary>
            public static void Release() => Physics.SyncTransforms();

            public static float Lowest(SpiderEnemy enemy, SpiderEnemy.FootPart part)
            {
                SpiderEnemy.FootPart before = enemy.footPart;
                enemy.footPart = part;

                try
                {
                    return LowestVertex(enemy.model);
                }
                finally
                {
                    enemy.footPart = before;
                }
            }

            /// <summary>Lowest animated vertex of everything under the model.</summary>
            static float LowestVertex(Transform model)
            {
                float lowest = float.MaxValue;
                bool any = false;

                foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;

                    Mesh mesh;
                    Transform[] bones = null;

                    if (r is SkinnedMeshRenderer skinned)
                    {
                        mesh = skinned.sharedMesh;
                        bones = skinned.bones;
                    }
                    else
                    {
                        var filter = r.GetComponent<MeshFilter>();
                        mesh = filter != null ? filter.sharedMesh : null;
                    }

                    if (mesh == null || !mesh.isReadable) continue;

                    Vector3[] positions = mesh.vertices;
                    if (positions == null) continue;

                    BoneWeight[] weights = bones != null ? mesh.boneWeights : null;
                    Matrix4x4[] bindposes = bones != null ? mesh.bindposes : null;

                    if (weights != null && weights.Length != positions.Length) weights = null;

                    for (int v = 0; v < positions.Length; v++)
                    {
                        float y = bones != null && weights != null && bindposes != null && v < weights.Length
                            ? SkinnedY(positions[v], weights[v], bones, bindposes)
                            : r.transform.TransformPoint(positions[v]).y;

                        if (!any || y < lowest) { lowest = y; any = true; }
                    }
                }

                return any ? lowest : float.NaN;
            }

            static float SkinnedY(Vector3 position, BoneWeight w, Transform[] bones, Matrix4x4[] bindposes)
            {
                Vector4 p = position;

                return BoneY(p, w.weight0, w.boneIndex0, bones, bindposes) +
                       BoneY(p, w.weight1, w.boneIndex1, bones, bindposes) +
                       BoneY(p, w.weight2, w.boneIndex2, bones, bindposes) +
                       BoneY(p, w.weight3, w.boneIndex3, bones, bindposes);
            }

            static float BoneY(Vector4 p, float weight, int bone, Transform[] bones, Matrix4x4[] bindposes)
            {
                if (weight <= 0f || bone < 0 || bone >= bones.Length || bone >= bindposes.Length || bones[bone] == null)
                    return 0f;

                Vector3 local = bindposes[bone].MultiplyPoint3x4(p);
                return (bones[bone].TransformPoint(local) * weight).y;
            }
        }

        static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder(GeneratedDir))
                AssetDatabase.CreateFolder("Assets/MCP/MysticMap", "Generated");

            if (!AssetDatabase.IsValidFolder(SpiderDir))
                AssetDatabase.CreateFolder(GeneratedDir, "Spider");
        }

        // =====================================================================
        //  Installing
        // =====================================================================
        static void Install(bool report)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath) == null)
            {
                if (report)
                    Debug.LogWarning("[MysticMap.Spiders] The spider model is missing at " + ModelPath + ".");
                return;
            }

            var players = Object.FindObjectsByType<SlimePlayer>();
            if (players == null || players.Length == 0)
            {
                if (report)
                    Debug.LogWarning("[MysticMap.Spiders] No SlimePlayer in the scene - " +
                                     "spiders need someone to hunt.");
                return;
            }

            GameObject prefab = EnsureCurrentPrefab();

            if (prefab == null) return;

            bool changed = EnsurePlayerVitals(players[0].gameObject);

            var director = GameObject.Find(DirectorName);
            if (director == null)
            {
                director = new GameObject(DirectorName);
                Undo.RegisterCreatedObjectUndo(director, "Add the spider director");
                changed = true;
            }

            var spawner = director.GetComponent<SpiderDirector>();
            if (spawner == null) spawner = director.AddComponent<SpiderDirector>();

            spawner.spiders = new[] { prefab };
            spawner.target = players[0].transform;

            EditorUtility.SetDirty(director);
            EditorUtility.SetDirty(spawner);

            if (changed)
            {
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            }

            if (report)
                Debug.Log("[MysticMap.Spiders] Ready: '" + DirectorName + "' streams up to " +
                          spawner.maxAlive + " spiders around '" + players[0].name + "' using " +
                          PrefabPath + ".");
        }

        /// <summary>True when this prefab was made before the current builder (or by hand).</summary>
        static bool NeedsRebuild(GameObject prefab)
        {
            var enemy = prefab.GetComponentInChildren<SpiderEnemy>(true);
            return enemy == null || enemy.builtBy != BuildVersion || !enemy.modelBaseSet;
        }

        /// <summary>
        /// Delivers a fresh spider prefab after a builder change: a prefab from an older builder is rebuilt
        /// and then the fit saved in the tuning window is put back on it, so the legs-on-the-ground fix
        /// arrives without the user having to press anything.
        /// </summary>
        public static GameObject EnsureCurrentPrefab()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab != null && !NeedsRebuild(prefab)) return prefab;

            prefab = BuildPrefab(false);
            if (prefab == null) return null;

            // A rebuild normally ends with the tuning window's own fit on top of it (which is also what
            // stands the spider on its feet). Without any saved fit the builder's numbers are all there is,
            // so the window is asked to apply them by itself.
            if (EditorPrefs.HasKey("MM.SpiderScale")) SpiderSettingsWindow.ApplySavedFit(prefab);
            else SpiderSettingsWindow.MarkNeedsFit();

            return prefab;
        }

        /// <summary>Makes the slime hurtable (Health) and adds the feedback component.</summary>
        static bool EnsurePlayerVitals(GameObject player)
        {
            bool changed = false;

            var health = player.GetComponent<Health>();
            if (health == null)
            {
                health = Undo.AddComponent<Health>(player);
                changed = true;
            }

            var vitals = player.GetComponent<PlayerVitals>();
            if (vitals == null)
            {
                vitals = Undo.AddComponent<PlayerVitals>(player);
                changed = true;
            }

            health.maxHealth = Mathf.Max(1f, vitals.maxHealth);
            health.destroyOnDeath = false;
            health.reviveAfter = 0f;
            health.deathEffect = null;
            health.stirEnvironment = false;     // the slime is knocked out, not blown to pieces
            vitals.health = health;

            EditorUtility.SetDirty(health);
            EditorUtility.SetDirty(vitals);

            return changed;
        }
    }
}
#endif
