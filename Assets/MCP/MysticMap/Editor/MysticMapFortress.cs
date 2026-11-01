#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MysticMap.EditorTools
{
    public static partial class MysticMapBuilder
    {
        // ---------------------------------------------------------------------
        //  Town DRESSING: the things that live inside the town but are not part of its
        //  structure - the posts along the walls, and the market clutter (carts, barrels,
        //  baskets, food) scattered over the square and the streets.
        //
        //  The town LAYOUT itself (footprint, gates, wall runs, towers, landmarks, house
        //  grid) is designed in MysticMapTownBuilder.cs, which is the authority. This file
        //  only ADDS to that plan and reuses its helpers - it repeats none of it.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Posts / braziers set along the inside of every wall at a regular spacing, so the
        /// perimeter reads as a maintained wall instead of a bare line of stone. The gates
        /// keep a clear landing on either side.
        /// </summary>
        internal static void PlanWallPosts(List<TownPiece> list, ref int order)
        {
            if (!TownPrefabExists("Wall_props")) return;

            const float Inset = 3.2f;   // distance in from the wall centre line
            const float Step = 11f;     // spacing along a wall

            float x0 = Village.x - TownHX + Inset, x1 = Village.x + TownHX - Inset;
            float z0 = Village.y - TownHZ + Inset, z1 = Village.y + TownHZ - Inset;

            // North / south walls: posts face into the town.
            foreach (var sz in new[] { 1f, -1f })
            {
                float wz = Village.y + sz * (TownHZ - Inset);
                var gates = GatesNorthSouth();
                for (float x = x0; x <= x1 + 0.01f; x += Step)
                {
                    if (!ClearOfGates(x, Village.x, gates)) continue;
                    PlanPiece(list, ref order, "Props", "Wall_props", x, wz,
                              sz > 0f ? 180f : 0f, WallScale);
                }
            }

            // East / west walls.
            foreach (var sgn in new[] { 1f, -1f })
            {
                float wx = Village.x + sgn * (TownHX - Inset);
                var gates = GatesEastWest();
                for (float z = z0; z <= z1 + 0.01f; z += Step)
                {
                    if (!ClearOfGates(z, Village.y, gates)) continue;
                    PlanPiece(list, ref order, "Props", "Wall_props", wx, z,
                              sgn > 0f ? 270f : 90f, WallScale);
                }
            }
        }

        /// <summary>
        /// Is this spot along a wall far enough from every gate opening?
        /// <paramref name="along"/> is a world coordinate on the axis the wall runs along;
        /// gate offsets are relative to the town centre on that same axis.
        /// </summary>
        static bool ClearOfGates(float along, float centre, List<float> gateOffsets)
        {
            foreach (var g in gateOffsets)
                if (Mathf.Abs(along - (centre + g)) < GateHalf)
                    return false;
            return true;
        }

        /// <summary>
        /// Random market clutter (carts, barrels, baskets, sacks, food, a little furniture)
        /// scattered over the open ground of the town: the square, the streets and the gaps
        /// between the houses.
        ///
        /// The scatter is SEEDED, so a given town always gets the same clutter - a map
        /// rebuild hands back exactly the town that was saved, clutter included.
        /// </summary>
        internal static void PlanTownProps(List<TownPiece> list, ref int order)
        {
            // Everything already on the plan is an obstacle, whatever group it is in.
            var placed = new List<Bounds>();
            foreach (var p in list)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p.prefab);
                if (prefab != null)
                    placed.Add(PlannedBoundsFast(prefab, p.pos.x, p.pos.z, p.yaw, p.scale, p.pos.y));
            }

            string[] pool =
            {
                "cart1", "cart2", "cart3", "cart4",
                "storage_barrel", "storage_barrel_small", "storage_barrel_drink",
                "storage_barrel_fish", "storage_basket", "storage_basket_small",
                "storage_bag", "storage_bag_beet", "storage_bag_carrot",
                "storage_bag_potato", "storage_bag_seeds",
                "storage_jug", "storage_jug_small",
                "Food_apple", "Food_bread_1", "Food_bread_2", "Food_corn",
                "Food_fish", "Food_pie", "Food_bottle1", "Food_bottle2", "Food_bottle3",
                "Furniture_chair1", "Furniture_chair2", "Furniture_table_small",
                "Furniture_cupboard1", "Furniture_book1", "Furniture_scrolls",
                "Furniture_candlestick1", "Furniture_elixir1", "Furniture_bed",
            };

            var rnd = new System.Random(PropSeed);
            float halfX = TownHX - 8f, halfZ = TownHZ - 8f;
            int want = Mathf.Clamp(Mathf.RoundToInt(TownHX * TownHZ / 42f), 60, 260);
            int added = 0, tries = 0;

            while (added < want && tries++ < want * 40)
            {
                string nm = pool[rnd.Next(pool.Length)];
                float x = Village.x + (float)(rnd.NextDouble() * 2.0 - 1.0) * halfX;
                float z = Village.y + (float)(rnd.NextDouble() * 2.0 - 1.0) * halfZ;

                // The two main avenues stay clear so the streets remain walkable.
                if (Mathf.Abs(x - Village.x) < RoadHalfWidth + 1.5f) continue;
                if (Mathf.Abs(z - Village.y) < RoadHalfWidth + 1.5f) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TownDir + "/" + nm + ".prefab");
                if (prefab == null) continue;

                float yaw = rnd.Next(0, 24) * 15f;
                float scale = 0.9f + (float)rnd.NextDouble() * 0.45f;
                float y = WorldSampleHeight(x, z);
                Bounds b = PlannedBoundsFast(prefab, x, z, yaw, scale, y);

                bool blocked = false;
                foreach (var o in placed)
                    if (OverlapXZ(b, o, 0.4f)) { blocked = true; break; }
                if (blocked) continue;

                placed.Add(b);
                int before = list.Count;
                PlanBld(list, ref order, placed, "Props", nm, x, z, yaw, scale, false);
                if (list.Count > before) added++;
            }

            Log("Town clutter scattered: " + added + " props.");
        }

        const int PropSeed = 20261030;
    }
}
#endif
