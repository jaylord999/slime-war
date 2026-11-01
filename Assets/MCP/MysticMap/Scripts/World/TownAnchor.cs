using System.Collections.Generic;
using UnityEngine;

namespace MysticMap.World
{
    /// <summary>How much of the hand-built map the procedural world leaves alone.</summary>
    public enum LegacyArea
    {
        /// <summary>The whole authored map keeps its own terrain, trees, grass and props.</summary>
        None,
        /// <summary>Only the town square (see <see cref="TownAnchor.townCenter"/> / townRadius) is kept.</summary>
        Town
    }

    /// <summary>
    /// Describes the hand-built part of the map (the 1 km terrain with the fortress town)
    /// so the procedural world can treat it as a fixed landmark:
    ///
    ///  * chunks overlapping the rectangle are never generated (the town keeps its own
    ///    terrain, trees, grass and roads untouched),
    ///  * the procedural ground/roads blend up to the rectangle's edge height, so the two
    ///    meshes meet without a step,
    ///  * the road network is anchored on the road exits (front and back of the town plus
    ///    the side roads) so the roads continue into the endless world.
    ///
    /// Put it on the same GameObject as the world streamer (the setup tool does it), or
    /// leave the streamer's reference empty and it will find the scene's "Terrain".
    /// </summary>
    [DisallowMultipleComponent]
    public class TownAnchor : MonoBehaviour, ILegacyMap
    {
        [Tooltip("The hand-built terrain. Auto-filled from the scene when empty.")]
        public Terrain legacyTerrain;

        [Tooltip("Road exit at the FRONT of the town (north gate). Procedural roads start here.")]
        public Vector2 exitNorth = new Vector2(500f, 1000f);

        [Tooltip("Road exit at the BACK of the town (south gate). Procedural roads start here.")]
        public Vector2 exitSouth = new Vector2(500f, 0f);

        [Tooltip("Further road exits (the countryside roads that reach the other two edges).")]
        public Vector2[] sideExits = new Vector2[0];

        [Tooltip("Log the road exits that could not be snapped onto the chunk grid.")]
        public bool logAlignment = true;

        [Tooltip("None = the whole hand-built map is kept as it was authored: its terrain, its stored " +
                 "trees, its grass and everything placed on it. One town area = only the fortress town " +
                 "itself (your trees / grass / plants around it are removed once and grown procedurally " +
                 "instead, by the streamed chunks).")]
        public LegacyArea keepArea = LegacyArea.None;

        [Tooltip("Middle of the town (used by 'One town area').")] 
        public Vector2 townCenter = new Vector2(500f, 520f);

        [Tooltip("How far (m) around the town centre stays hand-built: plaza, houses, walls and a little " +
                 "green belt around them. Everything past this grows procedurally.")]
        public float townRadius = 150f;

        // Cached rectangle (world space).
        Vector2 _origin;
        Vector2 _size;
        bool _ready;

        /// <summary>Bottom-left corner of the hand-built rectangle in world space.</summary>
        public Vector2 Origin { get { EnsureReady(); return _origin; } }

        /// <summary>Size of the hand-built rectangle (x = X extent, y = Z extent).</summary>
        public Vector2 Size { get { EnsureReady(); return _size; } }

        public bool HasLegacy { get { EnsureReady(); return legacyTerrain != null && legacyTerrain.terrainData != null; } }

        /// <summary>See <see cref="ILegacyMap.LogAlignment"/>.</summary>
        public bool LogAlignment => logAlignment;

        void Awake() => EnsureReady();

        public void EnsureReady()
        {
            if (_ready) return;

            if (legacyTerrain == null)
            {
                var terrains = FindObjectsByType<Terrain>();
                foreach (var t in terrains)
                {
                    if (t == null) continue;
                    if (t.gameObject.name == "Terrain") { legacyTerrain = t; break; }
                    if (legacyTerrain == null) legacyTerrain = t;   // fall back to any terrain
                }
            }

            if (legacyTerrain != null && legacyTerrain.terrainData != null)
            {
                Vector3 p = legacyTerrain.transform.position;
                Vector3 s = legacyTerrain.terrainData.size;

                if (keepArea == LegacyArea.Town)
                {
                    float r = Mathf.Clamp(townRadius, 20f, Mathf.Min(s.x, s.z) * 0.5f);
                    townRadius = r;
                    _origin = new Vector2(townCenter.x - r, townCenter.y - r);
                    _size = new Vector2(r * 2f, r * 2f);
                }
                else
                {
                    _origin = new Vector2(p.x, p.z);
                    _size = new Vector2(s.x, s.z);
                }
            }
            else
            {
                // Sensible default for this project (MysticMapBuilder uses a 1 km map at 0,0).
                if (keepArea == LegacyArea.Town)
                {
                    float r = Mathf.Clamp(townRadius, 20f, 500f);
                    _origin = new Vector2(townCenter.x - r, townCenter.y - r);
                    _size = new Vector2(r * 2f, r * 2f);
                }
                else
                {
                    _origin = Vector2.zero;
                    _size = new Vector2(1000f, 1000f);
                }
            }

            _ready = true;
        }

        /// <summary>Forces a re-read of the terrain (after building/replacing the map).</summary>
        public void Refresh()
        {
            _ready = false;
            EnsureReady();
        }


        public bool InsideLegacy(float x, float z)
        {
            EnsureReady();
            if (!HasLegacy) return false;
            return x >= _origin.x && x <= _origin.x + _size.x &&
                   z >= _origin.y && z <= _origin.y + _size.y;
        }

        /// <summary>Distance from the rectangle: 0 inside, positive outside.</summary>
        public float OutsideDistance(float x, float z)
        {
            EnsureReady();
            if (!HasLegacy) return 1e6f;    // no hand-built map: nothing to blend to
            float dx = Mathf.Max(_origin.x - x, x - (_origin.x + _size.x), 0f);
            float dz = Mathf.Max(_origin.y - z, z - (_origin.y + _size.y), 0f);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Closest point on (or in) the rectangle - used to sample the edge height.</summary>
        public Vector2 ClosestPoint(float x, float z)
        {
            EnsureReady();
            return new Vector2(Mathf.Clamp(x, _origin.x, _origin.x + _size.x),
                               Mathf.Clamp(z, _origin.y, _origin.y + _size.y));
        }

        /// <summary>
        /// Height of the hand-built terrain at the nearest edge point. Outside the map the
        /// terrain cannot be sampled, so the edge value is projected outward - which is
        /// exactly what makes the two meshes join.
        /// </summary>
        public float EdgeHeight(float x, float z)
        {
            EnsureReady();
            if (legacyTerrain == null || legacyTerrain.terrainData == null)
                return 0f;

            Vector2 c = ClosestPoint(x, z);
            // Stay 1 cm inside so we never fall off the very last heightmap sample.
            c.x = Mathf.Clamp(c.x, _origin.x + 0.01f, _origin.x + _size.x - 0.01f);
            c.y = Mathf.Clamp(c.y, _origin.y + 0.01f, _origin.y + _size.y - 0.01f);
            return legacyTerrain.SampleHeight(new Vector3(c.x, 0f, c.y));
        }

        /// <summary>True when the chunk rectangle [x0,z0]-[x1,z1] touches the hand-built map.</summary>
        public bool OverlapsLegacy(float x0, float z0, float x1, float z1, float margin)
        {
            EnsureReady();
            if (!HasLegacy) return false;
            return x0 <= _origin.x + _size.x + margin && x1 >= _origin.x - margin &&
                   z0 <= _origin.y + _size.y + margin && z1 >= _origin.y - margin;
        }

        /// <summary>
        /// True when this chunk belongs to the town and must stay hand-built. With
        /// <see cref="LegacyArea.Town"/> only the square drawn around the town counts, so the country
        /// around it is generated (trees included) even though it sits on the hand-built terrain.
        /// </summary>
        public bool KeepTown(float x0, float z0, float x1, float z1)
        {
            EnsureReady();
            if (!HasLegacy || keepArea != LegacyArea.Town) return false;
            return OverlapsLegacy(x0, z0, x1, z1, -1f);
        }

        /// <summary>Distance (m) from a point to the town area: 0 inside it, growing outwards.</summary>
        public float DistanceToTown(float x, float z)
        {
            EnsureReady();
            if (keepArea != LegacyArea.Town) return float.MaxValue;

            float dx = Mathf.Max(Mathf.Abs(x - townCenter.x) - townRadius, 0f);
            float dz = Mathf.Max(Mathf.Abs(z - townCenter.y) - townRadius, 0f);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Outward direction (unit, XZ) pointing away from the town at a road exit.</summary>
        public Vector2 OutsideNormal(Vector2 exit)
        {
            EnsureReady();
            float dLeft = Mathf.Abs(exit.x - _origin.x);
            float dRight = Mathf.Abs(exit.x - (_origin.x + _size.x));
            float dBottom = Mathf.Abs(exit.y - _origin.y);
            float dTop = Mathf.Abs(exit.y - (_origin.y + _size.y));

            float best = Mathf.Min(Mathf.Min(dLeft, dRight), Mathf.Min(dBottom, dTop));
            if (best == dLeft) return new Vector2(-1f, 0f);
            if (best == dRight) return new Vector2(1f, 0f);
            if (best == dBottom) return new Vector2(0f, -1f);
            return new Vector2(0f, 1f);
        }

        /// <summary>Every road exit that must connect to the procedural network.</summary>
        public Vector2[] AllExits()
        {
            int extra = sideExits != null ? sideExits.Length : 0;
            var all = new Vector2[2 + extra];
            all[0] = exitNorth;
            all[1] = exitSouth;
            for (int i = 0; i < extra; i++) all[2 + i] = sideExits[i];
            return all;
        }

        /// <summary>
        /// The town's own roads as straight strips between its road exits and the centre: the country
        /// around the town (generated in town-only mode) must keep them clear of trees and draw them
        /// as a road instead of a meadow.
        ///
        /// A strip only runs through the hand-built map for the first part of its length; the rest is
        /// generated country, and those parts follow the road curls too (<paramref name="curler"/>),
        /// so a gate road does not turn into a ruler-straight line the moment it leaves the town.
        /// Both ends stay pinned: the town centre end and the exit end where it meets the map edge.
        /// </summary>
        public int AddRoadsTo(RoadField field, float halfWidth, RoadNetworkGenerator curler = null)
        {
            if (field == null) return 0;

            int added = 0;
            Vector2[] exits = AllExits();

            for (int i = 0; i < exits.Length; i++)
            {
                if (Vector2.Distance(exits[i], townCenter) < 1f) continue;

                if (curler != null && curler.CurlyRoads)
                {
                    // The part that runs inside the hand-built map keeps the authored straight look;
                    // the strip picks up curls over its last stretch, out in the generated country.
                    var line = new List<Vector2>(16) { townCenter, exits[i] };
                    curler.CurlPolyline(line);
                    field.AddPolyline(line, halfWidth);
                }
                else
                {
                    field.AddStraight(townCenter, exits[i], halfWidth);
                }

                added++;
            }

            return added;
        }

        void OnDrawGizmosSelected()
        {
            Refresh();
            Gizmos.color = new Color(0.4f, 0.9f, 1f, 0.9f);
            Vector3 c = new Vector3(_origin.x + _size.x * 0.5f, 45f, _origin.y + _size.y * 0.5f);
            Gizmos.DrawWireCube(c, new Vector3(_size.x, 2f, _size.y));

            Gizmos.color = new Color(1f, 0.75f, 0.2f, 1f);
            var exits = AllExits();
            for (int i = 0; i < exits.Length; i++)
            {
                Vector2 e = exits[i];
                Vector2 n = OutsideNormal(e);
                Vector3 p = new Vector3(e.x, EdgeHeight(e.x, e.y) + 1f, e.y);
                Gizmos.DrawSphere(p, 6f);
                Gizmos.DrawLine(p, p + new Vector3(n.x, 0f, n.y) * 60f);
            }
        }
    }
}
