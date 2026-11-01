#if UNITY_EDITOR
using System.Collections.Generic;
using MysticMap.World;
using UnityEditor;
using UnityEngine;

namespace MysticMap.EditorTools
{
    /// <summary>
    /// One piece of the town design: which prefab goes where, at what size, facing what way.
    /// Positions are world metres. The list of these is the whole "state of the town".
    /// </summary>
    public class TownPiece
    {
        public string prefab;
        /// <summary>Root group under FortressTown: Walls / Towers / Gates / Buildings / Houses / TownCore.</summary>
        public string group;
        /// <summary>Child name inside the group (usually the prefab name; "_Grassed" for grassed houses).</summary>
        public string name;
        public Vector3 pos;
        public float yaw;
        public float scale = 1f;
        /// <summary>Order it was placed in; re-applying keeps this order (overlap rules are order based).</summary>
        public int order;
        /// <summary>Shared prefab baked by the house-grass pass, so the design remembers it.</summary>
        public string grassPrefab;

        public TownPiece Clone()
        {
            return new TownPiece
            {
                prefab = prefab, group = group, name = name, pos = pos,
                yaw = yaw, scale = scale, order = order, grassPrefab = grassPrefab
            };
        }

        public string Key => group + "/" + name;
    }

    /// <summary>Saved town layout: the pieces plus a few facts about the town it was made for.</summary>
    public class TownDesignAsset : ScriptableObject
    {
        public string savedUtc = "";
        /// <summary>"generated" / "walled" / "captured" / "imported", for the menu header.</summary>
        public string source = "";
        public Vector2 center;
        public float fortHX;
        public float fortHZ;
        public List<TownPiece> pieces = new List<TownPiece>();
    }

    /// <summary>
    /// Stored state of the fortress town (the town DESIGN).
    ///
    /// It is saved next to the map, so the town survives a full map rebuild: the map builder
    /// only lays down land + roads, and the town builder re-applies this design on top. The
    /// design can be captured from the open scene, edited, saved/loaded as a text file, and
    /// re-applied without touching the terrain.
    /// </summary>
    public static class TownDesignState
    {
        public const string TownRootName = "FortressTown";

        const string GeneratedDir = "Assets/MCP/MysticMap/Generated";
        const string DesignPath = GeneratedDir + "/TownDesign.asset";
        const string HouseGrassDir = GeneratedDir + "/TownGrass";

        static readonly string[] GroupOrder =
            { "Walls", "Towers", "Gates", "Buildings", "Houses", "TownCore" };

        static TownDesignAsset _asset;

        /// <summary>The design asset (created on demand in Assets/MCP/MysticMap/Generated).</summary>
        public static TownDesignAsset Asset
        {
            get
            {
                if (_asset != null) return _asset;
                _asset = AssetDatabase.LoadAssetAtPath<TownDesignAsset>(DesignPath);
                if (_asset == null)
                {
                    EnsureFolder(GeneratedDir);
                    _asset = ScriptableObject.CreateInstance<TownDesignAsset>();
                    AssetDatabase.CreateAsset(_asset, DesignPath);
                    AssetDatabase.SaveAssets();
                }
                return _asset;
            }
        }

        public static string AssetPath => DesignPath;

        public static List<TownPiece> Pieces => Asset.pieces;

        /// <summary>Is there a stored town layout?</summary>
        public static bool HasDesign() => PieceCount() > 0;

        /// <summary>Number of pieces in the stored design (0 when nothing is saved).</summary>
        public static int PieceCount()
        {
            var a = AssetDatabase.LoadAssetAtPath<TownDesignAsset>(DesignPath);
            return (a != null && a.pieces != null) ? a.pieces.Count : 0;
        }

        /// <summary>When the design was last saved (for the town menu header).</summary>
        public static string LastSaved()
        {
            var a = AssetDatabase.LoadAssetAtPath<TownDesignAsset>(DesignPath);
            return (a != null && !string.IsNullOrEmpty(a.savedUtc)) ? a.savedUtc : "(never)";
        }

        /// <summary>Replaces the stored design with <paramref name="pieces"/> and saves it to disk.</summary>
        public static void Store(List<TownPiece> pieces, string source = null)
        {
            var a = Asset;
            a.pieces = new List<TownPiece>();
            if (pieces != null)
                foreach (var p in pieces)
                    if (p != null) a.pieces.Add(p.Clone());
            if (source != null) a.source = source;
            a.center = MysticMapBuilder.Village;
            a.fortHX = MysticMapBuilder.FortHX;
            a.fortHZ = MysticMapBuilder.FortHZ;
            a.savedUtc = System.DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
            EditorUtility.SetDirty(a);
            AssetDatabase.SaveAssets();
        }

        /// <summary>Removes the stored design (the next map build then grows the default town).</summary>
        public static void Clear()
        {
            var a = Asset;
            a.pieces = new List<TownPiece>();
            a.savedUtc = "";
            a.source = "";
            EditorUtility.SetDirty(a);
            AssetDatabase.SaveAssets();
        }

        // ---- Generating a design -------------------------------------------------

        /// <summary>
        /// Designs the classic fortress town (walls, towers, gates, landmarks + house rings)
        /// without touching the open scene: the pieces are measured against the terrain's
        /// height map and stored as the design.
        /// <paramref name="onlyIfEmpty"/> keeps an existing design.
        /// </summary>
        public static bool GenerateDefault(bool onlyIfEmpty = false)
        {
            if (onlyIfEmpty && HasDesign()) return false;
            return DesignAndStore("generated", list =>
            {
                int order = 0;
                MysticMapBuilder.BuildDefaultFortress(list, ref order);
            });
        }

        /// <summary>
        /// Designs the "walled town" variant: the same walls, towers and gates but the inside
        /// is packed with houses hugging the walls plus the big central landmarks.
        /// </summary>
        public static bool GenerateWalledTown(bool onlyIfEmpty = false)
        {
            if (onlyIfEmpty && HasDesign()) return false;
            return DesignAndStore("walled", list =>
            {
                int order = 0;
                MysticMapBuilder.BuildWalledTownPlan(list, ref order);
            });
        }

        /// <summary>Runs a plan builder and stores what it planned.</summary>
        static bool DesignAndStore(string source, System.Action<List<TownPiece>> build)
        {
            var terrain = MysticMapBuilder.FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("[MysticMap] Open (or build) the map first - the town is measured on its terrain.");
                return false;
            }
            MysticMapBuilder.UseSceneTerrain(terrain);

            var list = new List<TownPiece>();
            build(list);
            if (list.Count == 0)
            {
                Debug.LogWarning("[MysticMap] The town plan came out empty (missing town prefabs?).");
                return false;
            }

            Store(list, source);
            MysticMapBuilder.Log("Town designed (" + source + "): " + list.Count + " pieces.");
            return true;
        }

        // ---- Capture / apply -----------------------------------------------------

        /// <summary>
        /// Reads every child of the FortressTown root (in the open scene) back into a design.
        /// Group order Walls -> Towers -> Gates -> Buildings -> Houses -> TownCore; children
        /// keep the order they have in the scene.
        /// </summary>
        public static List<TownPiece> Capture(Transform townRoot, bool store)
        {
            var list = new List<TownPiece>();
            if (townRoot != null)
            {
                foreach (var g in GroupOrder)
                {
                    var grp = townRoot.Find(g);
                    if (grp == null) continue;
                    foreach (Transform child in grp)
                    {
                        var p = CaptureOne(child, g);
                        p.order = list.Count;   // keep the scene order as the placement order
                        list.Add(p);
                    }
                }
            }
            if (store) Store(list, "captured");
            return list;
        }

        /// <summary>
        /// Prefab of a placed object. Prefab instances (the normal case) report their asset;
        /// anything that lost its prefab link falls back to the TownGrass folder ("_Grassed"
        /// houses) and then to the town prefab whose name the object carries.
        /// </summary>
        public static string PrefabOf(GameObject go)
        {
            if (go == null) return null;

            var src = PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (src != null)
            {
                string p = AssetDatabase.GetAssetPath(src);
                if (!string.IsNullOrEmpty(p)) return p;
            }

            for (var t = go.transform.parent; t != null; t = t.parent)
            {
                var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(t.gameObject);
                if (outer != null)
                {
                    string p = AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(outer));
                    if (!string.IsNullOrEmpty(p)) return p;
                }
            }

            string path = AssetDatabase.GetAssetPath(go);
            if (!string.IsNullOrEmpty(path)) return path;

            string nm = go.name;
            if (nm.EndsWith("_Grassed"))
            {
                path = HouseGrassDir + "/" + nm + ".prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return path;
                path = HouseGrassDir + "/" + nm.Substring(0, nm.Length - 8) + "_Grassed.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return path;
            }
            path = MysticMapBuilder.TownPrefabFolder + "/" + nm + ".prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return path;
            return null;
        }

        static TownPiece CaptureOne(Transform child, string group)
        {
            string prefabPath = PrefabOf(child.gameObject);
            string nm = child.name;
            if (!string.IsNullOrEmpty(prefabPath) && prefabPath.EndsWith(".prefab"))
                nm = System.IO.Path.GetFileNameWithoutExtension(prefabPath);

            return new TownPiece
            {
                prefab = prefabPath,
                group = group,
                name = nm,
                pos = child.position,
                yaw = child.eulerAngles.y,
                scale = child.localScale.x,
                grassPrefab = (prefabPath != null && prefabPath.StartsWith(HouseGrassDir)) ? prefabPath : null
            };
        }

        /// <summary>
        /// Points every house of the stored design at its baked "…_Grassed" prefab (after the
        /// house-grass pass re-baked the skirt at a new amount), so a later rebuild restores
        /// the grassed houses.
        /// </summary>
        public static void RememberHouseGrass(string bakedDir)
        {
            if (string.IsNullOrEmpty(bakedDir) || !HasDesign()) return;
            int touched = 0;
            foreach (var p in Asset.pieces)
            {
                if (string.IsNullOrEmpty(p.prefab) || p.prefab.StartsWith(HouseGrassDir)) continue;
                string nm = System.IO.Path.GetFileNameWithoutExtension(p.prefab);
                string grassed = bakedDir + "/" + nm + "_Grassed.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(grassed) == null) continue;
                p.grassPrefab = grassed;
                touched++;
            }
            if (touched == 0) return;

            EditorUtility.SetDirty(Asset);
            AssetDatabase.SaveAssets();
            MysticMapBuilder.Log("Town design remembers " + touched + " grassed houses.");
        }

        /// <summary>
        /// Removes whatever town is in the open scene so a design (or a fresh default town) can
        /// be laid down without leftovers. Leaves the terrain, roads and props alone.
        /// </summary>
        public static void ClearTownInScene()
        {
            var old = GameObject.Find(TownRootName);
            if (old != null) Object.DestroyImmediate(old);
        }

        /// <summary>Lays the stored design down in the open scene (replacing any existing town).</summary>
        public static int Apply() => Apply(Pieces);

        /// <summary>
        /// Lays <paramref name="pieces"/> down in the open scene, replacing any existing town.
        /// Returns how many pieces were placed, or -1 when there is no terrain / no design.
        /// </summary>
        public static int Apply(List<TownPiece> pieces)
        {
            var terrain = MysticMapBuilder.FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("[MysticMap] No Terrain in the open scene. Build (or open) the map first.");
                return -1;
            }
            if (pieces == null || pieces.Count == 0)
            {
                Debug.LogWarning("[MysticMap] No town design stored. Use Town menu > Capture the town design first.");
                return -1;
            }

            MysticMapBuilder.UseSceneTerrain(terrain);
            ClearTownInScene();

            var root = new GameObject(TownRootName);
            root.transform.position = Vector3.zero;

            var ordered = new List<TownPiece>(pieces);
            ordered.Sort((a, b) => a.order.CompareTo(b.order));

            int placed = 0, missing = 0, grassed = 0;
            foreach (var p in ordered)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p.prefab);
                if (prefab == null)
                {
                    missing++;
                    Debug.LogWarning("[MysticMap] Town piece skipped, prefab not found: " + p.prefab);
                    continue;
                }

                // Houses that were baked with an edge-of-house grass skirt keep that look:
                // the design remembers the baked "…_Grassed" prefab, so re-applying the town
                // restores it instead of the bare house.
                bool useGrass = false;
                if (!string.IsNullOrEmpty(p.grassPrefab))
                {
                    var gp = AssetDatabase.LoadAssetAtPath<GameObject>(p.grassPrefab);
                    if (gp != null) { prefab = gp; useGrass = true; }
                }

                var grp = root.transform.Find(p.group);
                if (grp == null)
                {
                    var grpGo = new GameObject(p.group);
                    grpGo.transform.SetParent(root.transform, false);
                    grp = grpGo.transform;
                }

                var go = MysticMapBuilder.SpawnPiece(grp, prefab, p.pos, p.yaw, p.scale);
                if (go != null)
                {
                    go.name = string.IsNullOrEmpty(p.name) ? prefab.name : p.name;
                    placed++;
                    if (useGrass) grassed++;
                }
            }

            MysticMapBuilder.Log("Town design applied: " + placed + " pieces" +
                                 (grassed > 0 ? " (" + grassed + " with house grass)" : "") +
                                 (missing > 0 ? " (" + missing + " missing prefabs)" : "") + ".");
            return placed;
        }

        // ---- Import / export ----------------------------------------------------

        public static bool ExportTo(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                System.IO.File.WriteAllText(path, JsonUtility.ToJson(Asset, true));
                AssetDatabase.Refresh();
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[MysticMap] Could not export town design: " + e.Message);
                return false;
            }
        }

        public static bool ImportFrom(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return false;
            try
            {
                var imported = JsonUtility.FromJson<TownDesignAsset>(System.IO.File.ReadAllText(path));
                if (imported == null || imported.pieces == null) return false;
                Store(imported.pieces, "imported");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[MysticMap] Could not import town design: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Copies the stored design onto the scene's TownAnchor so runtime code knows where the
        /// town is (the procedural world uses it for its "never build here" zones).
        /// </summary>
        public static void StampAnchor()
        {
            var anchor = Object.FindFirstObjectByType<TownAnchor>();
            if (anchor == null) return;
            var a = Asset;
            anchor.townCenter = MysticMapBuilder.Village;
            EditorUtility.SetDirty(anchor);
        }

        static void EnsureFolder(string dir)
        {
            if (AssetDatabase.IsValidFolder(dir)) return;
            string parent = System.IO.Path.GetDirectoryName(dir).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(dir));
        }
    }
}
#endif
