#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MysticMap.EditorTools
{
    public static partial class MysticMapBuilder
    {
        // ---------------------------------------------------------------------
        //  Fortress town DESIGN (scalable footprint, dense layout).
        //
        //  TownAreaScale (in MysticMapBuilder.cs, next to FortHX / FortHZ) grows the town's
        //  FOOTPRINT only. Prefab scales (WallScale, house scale 1.0, ...) are untouched: a
        //  bigger town simply has more walls, towers, gates, streets and houses.
        //
        //    1.0 = original size, 2.0 = twice as wide/deep (~4x area), 3.0 = ~9x area.
        //
        //  TownHX / TownHZ below already include that scale, so everything laid out here -
        //  and the terrain, roads and texture elsewhere, which read the same two values -
        //  grows together.
        //
        //  DENSITY: houses are packed by their real footprints instead of sitting one per big
        //  grid cell. Three passes (tight grid, half-step grid, random fill) squeeze houses
        //  into every gap that is not a street, then yard props (barrels, carts, tables) are
        //  set beside the houses. The knobs are the constants just below.
        // ---------------------------------------------------------------------

        const float TowerSpacing = 46f;       // a mid-wall tower roughly every this many metres
        const float HouseFrontYawOffset = 0f; // add 90/180/270 if houses face the wrong way
        const int HouseSeed = 1337;

        // ---- density knobs ----
        const float HouseStep = 7.5f;    // grid spacing of house candidates (smaller = denser)
        const float HousePad = 0.35f;    // gap kept between two buildings (m)
        const float BlockPitch = 44f;    // side street every this many metres (bigger = bigger blocks)
        const float StreetHalf = 4.5f;   // half width of the empty strip along a side street (m)
        const float YardChance = 0.03f;  // chance a first-pass plot is left empty as a yard
        const float YardPropChance = 0.55f; // chance a house gets a barrel / cart / table beside it
        static int FillAttempts => Mathf.RoundToInt(2500f * TownAreaScale * TownAreaScale);

        // measured prefab bounds (at origin, scale 1, no rotation), cached per generation
        static readonly Dictionary<string, Bounds> _localBounds = new Dictionary<string, Bounds>();
        static readonly Dictionary<string, GameObject> _townPrefabs = new Dictionary<string, GameObject>();

        static GameObject GetTownPrefab(string prefabName)
        {
            if (!_townPrefabs.TryGetValue(prefabName, out var p))
            {
                p = AssetDatabase.LoadAssetAtPath<GameObject>(TownDir + "/" + prefabName + ".prefab");
                _townPrefabs[prefabName] = p;
            }
            return p;
        }

        /// <summary>Lays the default fortress town down in the open scene and stores it as the design.</summary>
        internal static void CreateFortressTown()
        {
            TownDesignState.GenerateDefault(true);
            TownDesignState.Apply();
            Log("Fortress town placed.");
        }

        // ---------------------------------------------------------------------
        //  Menu: build the town from the design in this file.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Designs the town (walls, towers, gates, buildings, wall posts and market clutter)
        /// and stamps it into the open scene, replacing whatever town is there.
        /// </summary>
        [MenuItem("MCP/Town/1. Build the new town design")]
        public static void BuildNewTownMenu()
        {
            var terrain = FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("[MysticMap] No Terrain in the open scene. Build (or open) the map first - " +
                                 "the town is measured against its terrain.");
                return;
            }
            UseSceneTerrain(terrain);

            if (!TownDesignState.GenerateDefault(false)) return;
            TownDesignState.ClearTownInScene();
            int placed = TownDesignState.Apply();
            Log("Town built from the new design: " + placed + " pieces (footprint " +
                Mathf.RoundToInt(TownHX * 2f) + " x " + Mathf.RoundToInt(TownHZ * 2f) + " m).");
        }

        /// <summary>Re-stamps the stored design into the scene without redesigning it.</summary>
        [MenuItem("MCP/Town/2. Re-apply the stored design")]
        public static void ReapplyTownMenu()
        {
            if (!TownDesignState.HasDesign())
            {
                Debug.LogWarning("[MysticMap] No town design stored yet - use \"1. Build the new town design\" first.");
                return;
            }
            TownDesignState.ClearTownInScene();
            int placed = TownDesignState.Apply();
            Log("Town re-applied from the stored design: " + placed + " pieces.");
        }

        /// <summary>Forgets the stored design, so the next map build grows the default town.</summary>
        [MenuItem("MCP/Town/3. Clear the town design")]
        public static void ClearTownMenu()
        {
            TownDesignState.ClearTownInScene();
            TownDesignState.Clear();
            Log("Town design cleared.");
        }

        // ---------------------------------------------------------------------
        //  Menu: the town FLOOR texture (paving slabs + cobbles + dirt kerb).
        // ---------------------------------------------------------------------

        /// <summary>
        /// Repaints the ground inside the walls of the open scene as town paving: flat slabs
        /// (layer_ground_stones) for the yards, sandy cobbles (layer_ground_road) on every street,
        /// under a colonnade round the walls and across the plaza, and a trodden dirt band
        /// (layer_ground_soil) where the wall foot meets the ground. Grass is gone inside the walls.
        /// </summary>
        [MenuItem("MCP/Town/4. Repaint town floor (paving, no grass)")]
        public static void RepaintTownFloorMenu()
        {
            var terrain = FindSceneTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("[MysticMap] No Terrain in the open scene. Build (or open) the map first - " +
                                 "the town floor is painted into its terrain.");
                return;
            }
            UseSceneTerrain(terrain);
            RepaintTownGround();
        }

        /// <summary>Designs the town again and repaints the floor underneath it.</summary>
        [MenuItem("MCP/Town/5. Rebuild town + floor")]
        public static void RebuildTownAndFloorMenu()
        {
            BuildNewTownMenu();
            RepaintTownFloorMenu();
        }

        /// <summary>
        /// The default town as design pieces: the perimeter (corner towers, wall runs, mid-wall
        /// towers, gates), then the buildings, and finally the dressing - the posts along the
        /// walls and the market clutter (both in MysticMapFortress.cs).
        /// </summary>
        internal static void BuildDefaultFortress(List<TownPiece> list, ref int order)
        {
            _localBounds.Clear();
            _townPrefabs.Clear();
            PlanCornerTowers(list, ref order);
            PlanWallRuns(list, ref order);
            PlanWallTowers(list, ref order);
            PlanGates(list, ref order);
            PlanBuildings(list, ref order);
            PlanWallPosts(list, ref order);
            PlanTownProps(list, ref order);
        }

        static bool TownPrefabExists(string prefabName) => GetTownPrefab(prefabName) != null;

        // Record one fixed piece (wall, tower, gate) on the plan.
        static void PlanPiece(List<TownPiece> list, ref int order, string group, string prefabName,
                              float x, float z, float yaw, float scale)
        {
            var prefab = GetTownPrefab(prefabName);
            if (prefab == null)
            {
                Debug.LogWarning("[MysticMap] Missing town prefab: " + prefabName);
                return;
            }

            list.Add(new TownPiece
            {
                prefab = TownDir + "/" + prefabName + ".prefab",
                group = group,
                name = prefabName,
                pos = new Vector3(x, WorldSampleHeight(x, z), z),
                yaw = yaw,
                scale = scale,
                order = order++
            });
        }

        // ---------------------------------------------------------------------
        //  Gates: where they sit decides where wall runs and towers leave gaps.
        // ---------------------------------------------------------------------

        // Offsets (along a wall, from the town centre) of the gates on that wall.
        static List<float> GateOffsets(float halfLength, bool centre, bool sides)
        {
            var g = new List<float>();
            if (sides) g.Add(-halfLength * 0.5f);
            if (centre) g.Add(0f);
            if (sides) g.Add(halfLength * 0.5f);
            return g;
        }

        // North / south walls: always a centre gate, plus two more once the town is large.
        static List<float> GatesNorthSouth() => GateOffsets(TownHX, true, TownAreaScale >= 1.5f);

        // East / west walls: no gate at 1x (like the original); a centre gate from 1.5x,
        // and extra gates on very large towns.
        static List<float> GatesEastWest() => GateOffsets(TownHZ, TownAreaScale >= 1.5f, TownAreaScale >= 2.5f);

        static float GateLen => 9.445f * WallScale;
        static float GateHalf => GateLen * 0.5f + 1.2f;   // opening each side of a gate centre
        static float GateFlankOff => GateLen * 0.5f + 3.2f;

        // ---------------------------------------------------------------------
        //  Towers
        // ---------------------------------------------------------------------

        // Four big towers cap the corners of the rectangle.
        internal static void PlanCornerTowers(List<TownPiece> list, ref int order)
        {
            var sx = new[] { 1f, 1f, -1f, -1f };
            var sz = new[] { 1f, -1f, -1f, 1f };
            for (int i = 0; i < 4; i++)
                PlanPiece(list, ref order, "Towers", "Wall_tower2",
                          Village.x + sx[i] * TownHX, Village.y + sz[i] * TownHZ, 0f, WallScale);
        }

        // Smaller towers at regular intervals along every wall (skipping anything near a gate).
        internal static void PlanWallTowers(List<TownPiece> list, ref int order)
        {
            if (!TownPrefabExists("Wall_tower1")) return;

            // North / south walls run along X.
            PlanTowerLine(list, ref order, true, TownHX, GatesNorthSouth(), new[] { Village.y + TownHZ, Village.y - TownHZ }, 0f);
            // East / west walls run along Z.
            PlanTowerLine(list, ref order, false, TownHZ, GatesEastWest(), new[] { Village.x + TownHX, Village.x - TownHX }, 90f);
        }

        static void PlanTowerLine(List<TownPiece> list, ref int order, bool alongX, float half,
                                  List<float> gates, float[] fixedCoords, float yaw)
        {
            float length = half * 2f;
            int segments = Mathf.Max(2, Mathf.RoundToInt(length / TowerSpacing));
            float centre = alongX ? Village.x : Village.y;

            for (int k = 1; k < segments; k++)
            {
                float along = -half + length * k / segments;   // offset from town centre
                bool nearGate = false;
                foreach (var g in gates)
                    if (Mathf.Abs(along - g) < GateFlankOff + 6f) { nearGate = true; break; }
                if (nearGate) continue;

                foreach (var fc in fixedCoords)
                {
                    float x = alongX ? centre + along : fc;
                    float z = alongX ? fc : centre + along;
                    PlanPiece(list, ref order, "Towers", "Wall_tower1", x, z, yaw, WallScale);
                }
            }
        }

        // ---------------------------------------------------------------------
        //  Walls
        // ---------------------------------------------------------------------

        // Straight wall runs between the corner towers, leaving an opening at every gate.
        internal static void PlanWallRuns(List<TownPiece> list, ref int order)
        {
            if (!TownPrefabExists("Wall_part2")) return;

            float len = 7.815f * WallScale;   // Wall_part2 run length (x) * scale

            // North / south: runs along X
            var gatesNS = GatesNorthSouth();
            foreach (var sz in new[] { 1f, -1f })
            {
                float wz = Village.y + sz * TownHZ;
                float start = Village.x - TownHX;
                foreach (var g in gatesNS)
                {
                    PlanRunX(list, ref order, len, start, Village.x + g - GateHalf, wz);
                    start = Village.x + g + GateHalf;
                }
                PlanRunX(list, ref order, len, start, Village.x + TownHX, wz);
            }

            // East / west: runs along Z
            var gatesEW = GatesEastWest();
            foreach (var sgn in new[] { 1f, -1f })
            {
                float wx = Village.x + sgn * TownHX;
                float start = Village.y - TownHZ;
                foreach (var g in gatesEW)
                {
                    PlanRunZ(list, ref order, len, start, Village.y + g - GateHalf, wx);
                    start = Village.y + g + GateHalf;
                }
                PlanRunZ(list, ref order, len, start, Village.y + TownHZ, wx);
            }
        }

        static void PlanRunX(List<TownPiece> list, ref int order, float len, float x0, float x1, float zW)
        {
            float range = x1 - x0;
            if (range < 0.5f) return;
            int n = Mathf.Max(1, Mathf.RoundToInt(range / len));
            float step = range / n;
            for (int i = 0; i < n; i++)
            {
                float x = x0 + step * (i + 0.5f);
                PlanPiece(list, ref order, "Walls", "Wall_part2", x, zW, 0f, WallScale);
            }
        }

        static void PlanRunZ(List<TownPiece> list, ref int order, float len, float z0, float z1, float xW)
        {
            float range = z1 - z0;
            if (range < 0.5f) return;
            int n = Mathf.Max(1, Mathf.RoundToInt(range / len));
            float step = range / n;
            for (int i = 0; i < n; i++)
            {
                float z = z0 + step * (i + 0.5f);
                PlanPiece(list, ref order, "Walls", "Wall_part2", xW, z, 90f, WallScale);
            }
        }

        // ---------------------------------------------------------------------
        //  Gates: a gatehouse flanked by a pair of smaller towers beside the road.
        // ---------------------------------------------------------------------
        internal static void PlanGates(List<TownPiece> list, ref int order)
        {
            if (!TownPrefabExists("Wall_entrance")) return;
            bool flank = TownPrefabExists("Wall_tower2");

            // North / south walls
            foreach (var sz in new[] { 1f, -1f })
            {
                float wz = Village.y + sz * TownHZ;
                foreach (var g in GatesNorthSouth())
                {
                    float gx = Village.x + g;
                    PlanPiece(list, ref order, "Gates", "Wall_entrance", gx, wz, 0f, WallScale);
                    if (!flank) continue;
                    foreach (var sd in new[] { 1f, -1f })
                        PlanPiece(list, ref order, "Gates", "Wall_tower2", gx + sd * GateFlankOff, wz, 0f, WallScale);
                }
            }

            // East / west walls (gate turned 90 degrees)
            foreach (var sgn in new[] { 1f, -1f })
            {
                float wx = Village.x + sgn * TownHX;
                foreach (var g in GatesEastWest())
                {
                    float gz = Village.y + g;
                    PlanPiece(list, ref order, "Gates", "Wall_entrance", wx, gz, 90f, WallScale);
                    if (!flank) continue;
                    foreach (var sd in new[] { 1f, -1f })
                        PlanPiece(list, ref order, "Gates", "Wall_tower2", wx, gz + sd * GateFlankOff, 0f, WallScale);
                }
            }
        }

        // ---------------------------------------------------------------------
        //  Houses + landmarks (dense).
        //
        //  Landmarks go in first, then houses are packed in three passes, then yard props.
        //  Side streets are empty strips every BlockPitch metres; everything else is filled
        //  as tightly as the real footprints allow. PlanBld rejects whatever would touch a
        //  road, the plaza, the walls or another building.
        // ---------------------------------------------------------------------
        internal static void PlanBuildings(List<TownPiece> list, ref int order)
        {
            var placed = new List<Bounds>();
            CollectStructuresFast(list, placed);   // walls, towers and gates are already down

            PlanLandmarks(list, ref order, placed);

            string[] pool =
            {
                "House1", "House2", "House3",
                "WoodenHouse_1", "House1", "WoodenHouse_2"
            };

            int houses = PlanHouseGrid(list, ref order, placed, pool);
            int props = PlanYardProps(list, ref order, placed);
            Log("Dense town: " + houses + " houses, " + props + " yard props.");
        }

        static void PlanLandmarks(List<TownPiece> list, ref int order, List<Bounds> placed)
        {
            // Taverns near the centre crossing.
            var taverns = new List<Vector2> { new Vector2(27f, 22f) };
            if (TownAreaScale >= 1.5f) taverns.Add(new Vector2(-27f, -22f));
            if (TownAreaScale >= 2.5f) { taverns.Add(new Vector2(-27f, 22f)); taverns.Add(new Vector2(27f, -22f)); }
            foreach (var t in taverns)
                PlanBld(list, ref order, placed, "Buildings", "Tavern", Village.x + t.x, Village.y + t.y, 0f, 1.0f);

            // Windmills toward the outer edge, one extra pair per scale step.
            var mills = new List<Vector2> { new Vector2(40f, -24f) };
            if (TownAreaScale >= 1.5f)
            {
                mills.Add(new Vector2(TownHX * 0.70f, -TownHZ * 0.60f));
                mills.Add(new Vector2(-TownHX * 0.70f, TownHZ * 0.60f));
            }
            if (TownAreaScale >= 2.5f)
            {
                mills.Add(new Vector2(-TownHX * 0.70f, -TownHZ * 0.60f));
                mills.Add(new Vector2(TownHX * 0.70f, TownHZ * 0.60f));
            }
            foreach (var m in mills)
                PlanBld(list, ref order, placed, "Buildings", "Windmill", Village.x + m.x, Village.y + m.y, 0f, 0.9f);
        }

        // Signed distance from d to the nearest side-street line (lines are every BlockPitch).
        static float StreetOffset(float d)
        {
            return Mathf.Repeat(d + BlockPitch * 0.5f, BlockPitch) - BlockPitch * 0.5f;
        }

        // Is this spot (offset from the town centre) inside the empty strip of a side street?
        static bool OnSideStreet(float dx, float dz)
        {
            return Mathf.Abs(StreetOffset(dx)) < StreetHalf || Mathf.Abs(StreetOffset(dz)) < StreetHalf;
        }

        // Yaw that turns a house toward the nearest side street.
        static float FaceNearestStreet(float dx, float dz)
        {
            float mx = StreetOffset(dx);
            float mz = StreetOffset(dz);
            float yaw;
            if (Mathf.Abs(mx) <= Mathf.Abs(mz))
                yaw = mx > 0f ? 270f : 90f;    // street is at -x / +x
            else
                yaw = mz > 0f ? 180f : 0f;     // street is at -z / +z
            return yaw + HouseFrontYawOffset;
        }

        static bool TryHouse(List<TownPiece> list, ref int order, List<Bounds> placed, string[] pool,
                             float dx, float dz, ref int count)
        {
            if (OnSideStreet(dx, dz)) return false;
            string nm = pool[Mathf.Abs(Mathf.RoundToInt(dx * 7f + dz * 13f) + count) % pool.Length];
            int before = list.Count;
            PlanBld(list, ref order, placed, "Buildings", nm, Village.x + dx, Village.y + dz,
                    FaceNearestStreet(dx, dz), 1.0f);
            if (list.Count == before) return false;
            count++;
            return true;
        }

        static void PlanHousePass(List<TownPiece> list, ref int order, List<Bounds> placed, string[] pool,
                                  System.Random rng, float offset, double skipChance, ref int count)
        {
            int nx = Mathf.FloorToInt(TownHX / HouseStep);
            int nz = Mathf.FloorToInt(TownHZ / HouseStep);

            // Candidate cells, nearest to the centre first so the middle fills before the edges.
            var cells = new List<Vector2Int>();
            for (int i = -nx; i <= nx; i++)
                for (int j = -nz; j <= nz; j++)
                    cells.Add(new Vector2Int(i, j));
            cells.Sort((a, b) => (a.x * a.x + a.y * a.y).CompareTo(b.x * b.x + b.y * b.y));

            foreach (var c in cells)
            {
                if (skipChance > 0.0 && rng.NextDouble() < skipChance) continue;   // the odd empty yard
                TryHouse(list, ref order, placed, pool, c.x * HouseStep + offset, c.y * HouseStep + offset, ref count);
            }
        }

        static int PlanHouseGrid(List<TownPiece> list, ref int order, List<Bounds> placed, string[] pool)
        {
            var rng = new System.Random(HouseSeed);
            int count = 0;

            // Pass 1: tight grid, centre outward.
            PlanHousePass(list, ref order, placed, pool, rng, 0f, YardChance, ref count);

            // Pass 2: the same grid shifted half a step, to catch the gaps pass 1 left.
            PlanHousePass(list, ref order, placed, pool, rng, HouseStep * 0.5f, 0.0, ref count);

            // Pass 3: random fill of whatever room is still free.
            int attempts = FillAttempts;
            for (int n = 0; n < attempts; n++)
            {
                float dx = ((float)rng.NextDouble() * 2f - 1f) * (TownHX - 5f);
                float dz = ((float)rng.NextDouble() * 2f - 1f) * (TownHZ - 5f);
                TryHouse(list, ref order, placed, pool, dx, dz, ref count);
            }
            return count;
        }

        // Barrels, carts and tables beside the houses so the yards read as lived-in.
        static int PlanYardProps(List<TownPiece> list, ref int order, List<Bounds> placed)
        {
            string[] props = { "storage_barrel", "cart1", "Furniture_table", "storage_barrel" };
            var rng = new System.Random(HouseSeed + 7);

            // Snapshot the houses first (the list grows while props are added).
            var houses = new List<Bounds>();
            foreach (var p in list)
            {
                if (p.group != "Buildings" || p.name == "Tavern" || p.name == "Windmill") continue;
                var prefab = GetTownPrefab(p.name);
                if (prefab == null) continue;
                houses.Add(PlannedBoundsFast(prefab, p.pos.x, p.pos.z, p.yaw, p.scale, p.pos.y));
            }

            int added = 0;
            foreach (var b in houses)
            {
                if (rng.NextDouble() > YardPropChance) continue;

                int side = rng.Next(4);
                Vector3 dir = side == 0 ? Vector3.right : side == 1 ? Vector3.left : side == 2 ? Vector3.forward : Vector3.back;
                float reach = (side < 2 ? b.extents.x : b.extents.z) + 1.4f;
                float x = b.center.x + dir.x * reach;
                float z = b.center.z + dir.z * reach;

                string nm = props[rng.Next(props.Length)];
                int before = list.Count;
                PlanBld(list, ref order, placed, "Props", nm, x, z, rng.Next(4) * 90f, 1.0f);
                if (list.Count > before) added++;
            }
            return added;
        }

        // Plan one building after rejecting it if it would overlap the plaza, a street,
        // the walls or a building that is already down.
        internal static void PlanBld(List<TownPiece> list, ref int order, List<Bounds> placed,
                                     string group, string prefabName, float x, float z, float yaw,
                                     float scale, bool checkRoads = true)
        {
            var prefab = GetTownPrefab(prefabName);
            if (prefab == null) return;
            if (x < 6f || x > MapSize - 6f || z < 6f || z > MapSize - 6f) return;

            float y = WorldSampleHeight(x, z);
            Bounds b = PlannedBoundsFast(prefab, x, z, yaw, scale, y);

            // 1) fully inside the walls, clear of the perimeter.
            if (b.min.x < Village.x - TownHX + 2.5f || b.max.x > Village.x + TownHX - 2.5f ||
                b.min.z < Village.y - TownHZ + 2.5f || b.max.z > Village.y + TownHZ - 2.5f)
                return;

            // 2) clear of the paved market square (small apron allowed).
            float px = Mathf.Clamp(Village.x, b.min.x, b.max.x);
            float pz = Mathf.Clamp(Village.y, b.min.z, b.max.z);
            if (Vector2.Distance(new Vector2(px, pz), Village) < PlazaRadius + 0.5f)
                return;

            // 3) clear of everything already placed (cheap, so it runs before the road test).
            foreach (var p in placed)
                if (OverlapXZ(b, p, HousePad)) return;

            // 4) clear of every street in the town (the whole road network, not just the two main
            //    avenues: the cross streets and side links carry houses on their far side).
            if (checkRoads)
            {
                float pad = RoadHalfWidth + 0.4f;
                const int probe = 3;
                for (int i = 0; i <= probe; i++)
                {
                    float sx = Mathf.Lerp(b.min.x, b.max.x, i / (float)probe);
                    for (int j = 0; j <= probe; j++)
                    {
                        float sz = Mathf.Lerp(b.min.z, b.max.z, j / (float)probe);
                        if (RoadDistance(sx, sz) < pad) return;
                    }
                }
            }

            placed.Add(b);
            list.Add(new TownPiece
            {
                prefab = TownDir + "/" + prefabName + ".prefab",
                group = group,
                name = prefabName,
                pos = new Vector3(x, y, z),
                yaw = yaw,
                scale = scale,
                order = order++
            });
        }

        // ---------------------------------------------------------------------
        //  Bounds helpers. Each prefab is measured ONCE (cached), then moved / rotated /
        //  scaled mathematically, so thousands of candidates stay fast.
        // ---------------------------------------------------------------------

        /// <summary>World bounds a piece would have at that spot (measured on the real prefab).</summary>
        internal static Bounds PlannedBoundsFast(GameObject prefab, float x, float z, float yaw, float scale, float y)
        {
            string key = AssetDatabase.GetAssetPath(prefab);
            if (!_localBounds.TryGetValue(key, out var local))
            {
                local = MeasurePrefab(prefab);
                _localBounds[key] = local;
            }

            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
            Vector3 c = local.center * scale;
            Vector3 e = local.extents * scale;

            bool first = true;
            Bounds result = new Bounds(new Vector3(x, y, z), Vector3.zero);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 corner = c + new Vector3(e.x * sx, e.y * sy, e.z * sz);
                        Vector3 w = new Vector3(x, y, z) + rot * corner;
                        if (first) { result = new Bounds(w, Vector3.zero); first = false; }
                        else result.Encapsulate(w);
                    }
            return result;
        }

        // Bounds of the prefab at the origin, scale 1, no rotation.
        static Bounds MeasurePrefab(GameObject prefab)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (go == null) go = (GameObject)Object.Instantiate(prefab);
            if (go == null) return new Bounds(Vector3.zero, Vector3.one);

            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            Bounds b = CombinedBounds(go);
            Object.DestroyImmediate(go);
            return b;
        }

        /// <summary>Bounds of the wall / tower / gate pieces already on the plan.</summary>
        internal static void CollectStructuresFast(List<TownPiece> list, List<Bounds> placed)
        {
            foreach (var p in list)
            {
                if (p.group != "Walls" && p.group != "Towers" && p.group != "Gates") continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p.prefab);
                if (prefab == null) continue;
                placed.Add(PlannedBoundsFast(prefab, p.pos.x, p.pos.z, p.yaw, p.scale, p.pos.y));
            }
        }

        /// <summary>Union of the renderer bounds of a hierarchy (so multi-part prefabs measure right).</summary>
        internal static Bounds CombinedBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.one);

            bool has = false;
            Bounds b = default;
            foreach (var r in rs)
            {
                if (r is ParticleSystemRenderer) continue;
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
            return has ? b : new Bounds(go.transform.position, Vector3.one);
        }

        /// <summary>Do two laid-out footprints on the ground plane overlap (with a little slack)?</summary>
        internal static bool OverlapXZ(Bounds a, Bounds b, float pad)
        {
            return a.min.x - pad < b.max.x && a.max.x + pad > b.min.x &&
                   a.min.z - pad < b.max.z && a.max.z + pad > b.min.z;
        }
    }
}
#endif