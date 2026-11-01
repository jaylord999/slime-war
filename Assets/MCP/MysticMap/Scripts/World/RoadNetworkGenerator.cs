using System.Collections.Generic;
using UnityEngine;

namespace MysticMap.World
{
    /// <summary>Identifies one lattice edge. Shared by both chunks that touch it.</summary>
    public struct RoadEdgeKey : System.IEquatable<RoadEdgeKey>
    {
        public readonly int i, j;
        public readonly bool horizontal;

        public RoadEdgeKey(int i, int j, bool horizontal)
        {
            this.i = i; this.j = j; this.horizontal = horizontal;
        }

        public bool Equals(RoadEdgeKey o) => i == o.i && j == o.j && horizontal == o.horizontal;

        public override bool Equals(object o) => o is RoadEdgeKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = i * 73856093 ^ j * 19349663 ^ (horizontal ? 0x51ED : 0x270B);
                return h;
            }
        }
    }

    /// <summary>One road segment between two lattice nodes, with its sampled centre line.</summary>
    public sealed class RoadEdge
    {
        public RoadEdgeKey key;
        public Vector2 a, b;              // lattice end points (world XZ)
        public Vector3[] points;          // sampled centre line (y unused = 0)
        public Vector3[] normals;         // per point, side direction (XZ) for placing props
        public Bounds bounds;
        public float halfWidth;
        public float curl;                // 0 = still a plain Bezier, 1 = at its curliest
        public float wander;               // how far (m) the curl reaches sideways off a-b

        public float Length
        {
            get
            {
                if (points == null || points.Length < 2) return 0f;
                float len = 0f;
                for (int i = 0; i < points.Length - 1; i++) len += Vector3.Distance(points[i], points[i + 1]);
                return len;
            }
        }
    }

    /// <summary>
    /// Builds and queries the procedural road network.
    ///
    /// The network is a deterministic graph on the chunk lattice: lattice nodes sit on
    /// multiples of <c>chunkSize</c>, and an edge between two nodes either exists or not
    /// depending only on (seed, edge). Because both chunks that share an edge evaluate the
    /// exact same canonical edge, roads cross chunk borders as one continuous curve, and
    /// no chunk ever has to talk to another one.
    ///
    /// The curve between two nodes is a cubic Bezier whose end tangents are the node
    /// tangents; a node tangent is derived from the (shared) set of open edges at that
    /// node plus a deterministic wander, so several roads meeting at a node form one
    /// smooth continuation instead of a kink.
    ///
    /// On top of that the centre line is curled (<c>roadCurly</c>): a deterministic spiral
    /// of 1.5 turns is offset sideways from the Bezier, its radius growing with the distance
    /// from the town and shrinking to zero at both nodes. Every node therefore keeps its
    /// shared end point and end tangent (roads still join smoothly), while the middle of an
    /// edge loops over itself several times. How far a curl may stray is capped
    /// (<c>roadCurlyFieldMargin</c>) and written into the edge bounds, so every chunk that
    /// could see the curl - and every caller that sizes its road field from the road bounds -
    /// still gets the whole loop instead of a piece of it.
    /// </summary>
    public class RoadNetworkGenerator
    {
        readonly WorldSettings _s;
        readonly ILegacyMap _town;
        readonly float _size;

        readonly Dictionary<RoadEdgeKey, RoadEdge> _cache = new Dictionary<RoadEdgeKey, RoadEdge>();
        readonly Dictionary<RoadEdgeKey, float> _openCache = new Dictionary<RoadEdgeKey, float>();
        readonly Dictionary<long, Vector2> _tangentCache = new Dictionary<long, Vector2>();

        HashSet<RoadEdgeKey> _forcedOpen;
        Dictionary<long, Vector2> _forcedDir;

        const int ForcedChainNodes = 5;      // how many nodes the town approach stays straight
        const int ForcedChainNodesCurly = 2; // ... and how many when the roads curl
        const float CurlTurns = 1.5f;        // turns of the spiral a fully curled road does

        public RoadNetworkGenerator(WorldSettings settings, ILegacyMap town)
        {
            _s = settings;
            _town = town;
            _size = Mathf.Max(10f, settings.chunkSize);
        }

        // =====================================================================
        //  Curls
        // =====================================================================
        /// <summary>How curly every road is right now (0 = plain Bezier, 1 = very curly).</summary>
        public float Curliness => Mathf.Clamp01(_s.roadCurly);

        /// <summary>
        /// True when the roads curl enough that anything straight has to be curled too (the
        /// hand-built town roads running through the generated country).
        /// </summary>
        public bool CurlyRoads => Curliness > 0.001f && CurlMaxWander > 0.5f;

        /// <summary>
        /// Longest stretch (metres) a road may stay uncurled. It is what the hand-built town
        /// roads inside the generated country are cut into, so they curl as well.
        /// </summary>
        public float CurlSpan => Mathf.Min(60f, _s.roadCurlyTownSpan);

        /// <summary>How far (m) a curved centre line may stray from the straight line a-b.</summary>
        public float CurlMaxWander
        {
            get
            {
                float room = Mathf.Min(0.3f * _size, Mathf.Max(0f, _s.roadMaxApron - _s.roadCurlyFieldMargin));
                return Mathf.Max(0f, Mathf.Min(Curliness * room, _s.roadCurlyMaxWander));
            }
        }

        /// <summary>
        /// How curly a road is where it stands. Roads stay nearly straight at the town gate and
        /// unroll into full curls as they get further away.
        /// </summary>
        public float CurlWeight(Vector2 p)
        {
            return Curliness * ProcNoise.Smoothstep(_s.roadStraightRange, _s.roadWindRange,
                                                    Vector2.Distance(p, TownCenter));
        }

        /// <summary>Sideways bend (m) a curled road actually reaches: the envelope peaks at 2/3.</summary>
        public float CurlAmount => CurlMaxWander * 1.5f;

        /// <summary>
        /// Curly road: bend a point sideways off the straight a -> b line. <paramref name="arc"/> is
        /// how far along the road the point is (metres). The bend fades to zero at both ends, so the
        /// road still starts and ends exactly on its lattice nodes, and winds back and forth in
        /// between - that is what makes it wavy instead of straight.
        /// </summary>
        public static Vector2 CurlOffset(RoadEdgeKey key, Vector2 a, Vector2 b, float arc, float amount, Vector2 tangent)
        {
            if (amount <= 0.0001f) return Vector2.zero;

            Vector2 straight = b - a;
            float len = straight.magnitude;
            if (len <= 0.0001f) return Vector2.zero;

            Vector2 dir = tangent.sqrMagnitude > 0.0001f ? tangent.normalized : straight / len;
            var side = new Vector2(-dir.y, dir.x);

            // 0 at both ends, 1 in the middle: the road stays pinned to its nodes and never kinks.
            float t = Mathf.Clamp01(arc / len);
            float envelope = Mathf.Sin(t * Mathf.PI);
            envelope *= envelope;

            // Wiggle every ~35 m and ~20 m, a per-road offset so no two roads look alike. Both are
            // slow enough that ~3 m spacing between the road's points keeps the curves smooth.
            uint h = DetHash.Hash(key.i, key.j, 0x71C3, 7u);
            float phase = DetHash.Float01(h) * Mathf.PI * 2f;
            float wave = Mathf.Sin(phase + t * len * 0.18f) * 0.65f + Mathf.Sin(phase * 1.7f + t * len * 0.31f) * 0.35f;
            return side * (wave * amount * envelope);
        }

        // =====================================================================
        //  Lattice helpers
        // =====================================================================

        int NodeI(float x) => Mathf.RoundToInt(x / _size);
        int NodeJ(float z) => Mathf.RoundToInt(z / _size);

        public Vector2 NodePos(int i, int j) => new Vector2(i * _size, j * _size);

        static long NodeKey(int i, int j) => ((long)i << 32) ^ (uint)j;

        public static readonly Vector2 TownCenter = new Vector2(500f, 520f);

        uint Seed => (uint)_s.seed;

        // =====================================================================
        //  Forced connections (town approaches)
        // =====================================================================
        void EnsureForced()
        {
            if (_forcedOpen != null) return;

            _forcedOpen = new HashSet<RoadEdgeKey>();
            _forcedDir = new Dictionary<long, Vector2>();

            if (_town == null) return;
            _town.EnsureReady();
            if (!_town.HasLegacy) return;

            var exits = _town.AllExits();
            for (int e = 0; e < exits.Length; e++)
            {
                Vector2 exit = exits[e];
                Vector2 n = _town.OutsideNormal(exit);

                int i = NodeI(exit.x);
                int j = NodeJ(exit.y);

                // Does the lattice actually pass through this exit? If not the road would
                // meet the hand-built map slightly off centre, so let the user know.
                float off = Vector2.Distance(NodePos(i, j), exit);
                if (off > 0.5f && _town.LogAlignment)
                    Debug.LogWarning("[MysticMap] Road exit " + exit.ToString("0") +
                                     " is " + off.ToString("0.0") + " m off the chunk grid (" +
                                     _size.ToString("0") + " m). Move the road or change the chunk " +
                                     "size so the connection is exact.", _town as Object);

                int di = Mathf.Abs(n.x) > Mathf.Abs(n.y) ? (int)Mathf.Sign(n.x) : 0;
                int dj = di == 0 ? (int)Mathf.Sign(n.y) : 0;
                if (di == 0 && dj == 0) dj = 1;

                int ci = i, cj = j;
                var axis = new Vector2(di, dj);
                // Curly roads give up most of the straight gate corridor: only the two edges
                // that actually leave the hand-built map stay axis aligned, the rest curl.
                int chain = System.Math.Max(1, Mathf.RoundToInt(
                    Mathf.Lerp(ForcedChainNodes, ForcedChainNodesCurly, Curliness)));
                for (int k = 0; k < chain; k++)
                {
                    // Every node of the approach keeps the exact axis tangent, so the short
                    // corridor out of the town gate stays one straight, predictable road.
                    _forcedDir[NodeKey(ci, cj)] = axis;

                    int ni = ci + di, nj = cj + dj;
                    var key = new RoadEdgeKey(System.Math.Min(ci, ni), System.Math.Min(cj, nj), di != 0);
                    if (!ClosedByLegacy(key)) _forcedOpen.Add(key);

                    ci = ni; cj = nj;
                    _forcedDir[NodeKey(ci, cj)] = axis;
                }
            }
        }

        // =====================================================================
        //  Edge existence
        // =====================================================================
        /// <summary>
        /// True when the edge sits inside the hand-built map. Those edges are skipped
        /// because the town's own terrain and roads own that ground.
        /// </summary>
        bool ClosedByLegacy(RoadEdgeKey key)
        {
            if (_town == null || !_town.HasLegacy) return false;

            Vector2 ma = EdgeMidpoint(key);
            return _town.OutsideDistance(ma.x, ma.y) <= 0.5f;
        }

        Vector2 EdgeMidpoint(RoadEdgeKey key)
        {
            Vector2 a = NodePos(key.i, key.j);
            Vector2 b = key.horizontal ? NodePos(key.i + 1, key.j) : NodePos(key.i, key.j + 1);
            return (a + b) * 0.5f;
        }

        /// <summary>Is this lattice edge a road? Deterministic for the whole world.</summary>
        public bool EdgeOpen(RoadEdgeKey key)
        {
            if (_openCache.TryGetValue(key, out float cached))
                return cached > 0.5f;

            EnsureForced();

            bool open;
            if (_forcedOpen != null && _forcedOpen.Contains(key)) open = true;
            else if (ClosedByLegacy(key)) open = false;
            else
            {
                Vector2 mid = EdgeMidpoint(key);
                float dTown = Vector2.Distance(mid, TownCenter);
                // Near the town most edges exist (a settled road network); far away it thins
                // out into a windier web, but it always stays above the percolation
                // threshold of the square lattice so the network never falls apart.
                float p = Mathf.Lerp(0.88f, 0.60f,
                                     ProcNoise.Smoothstep(_s.roadStraightRange, _s.roadWindRange, dTown));
                uint h = DetHash.Hash(key.i, key.j, key.horizontal ? 0x1234 : 0x4321, Seed);
                open = DetHash.Chance(h, p);
            }

            _openCache[key] = open ? 1f : 0f;
            return open;
        }

        public bool EdgeOpen(int i, int j, bool horizontal) => EdgeOpen(new RoadEdgeKey(i, j, horizontal));

        // =====================================================================
        //  Geometry
        // =====================================================================
        /// <summary>
        /// Direction a road leaves a node in. It is the circular average of the open edges
        /// at that node (so all roads meeting there join smoothly) plus a deterministic
        /// wander that grows with the distance from the town - the approach stays straight
        /// and predictable, the far country winds.
        /// </summary>
        public Vector2 NodeTangent(int i, int j)
        {
            long key = NodeKey(i, j);
            if (_tangentCache.TryGetValue(key, out Vector2 cached)) return cached;

            EnsureForced();

            // A town approach is exactly axis aligned: the gate roads never bend until the
            // forced corridor ends, whatever other roads happen to meet them.
            if (_forcedDir != null && _forcedDir.TryGetValue(key, out Vector2 forced))
            {
                _tangentCache[key] = forced;
                return forced;
            }

            float sx = 0f, sy = 0f;
            int count = 0;
            AccumulateNodeDir(i, j, true, 1, ref sx, ref sy, ref count);
            AccumulateNodeDir(i, j, true, -1, ref sx, ref sy, ref count);
            AccumulateNodeDir(i, j, false, 1, ref sx, ref sy, ref count);
            AccumulateNodeDir(i, j, false, -1, ref sx, ref sy, ref count);

            Vector2 dir;
            if (sx * sx + sy * sy > 0.0001f) dir = new Vector2(sx, sy).normalized;
            else
            {
                // Crossing / lone node: pick a deterministic diagonal so the roads bend
                // gently instead of snapping to an axis.
                float a = (DetHash.Float01(DetHash.Hash(i, j, 0x0D1A, Seed)) * 2f - 1f) * 0.78f;
                dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            }

            // Far from the town the direction wanders, which is what makes the country roads
            // kink and turn instead of running dead straight for kilometres. When the roads curl
            // the spiral owns most of the turning, so this stays gentler to keep the whole road
            // inside the apron the terrain grades for it.
            float dTown = Vector2.Distance(NodePos(i, j), TownCenter);
            float turnScale = Mathf.Lerp(1f, 0.3f, Curliness);
            float wander = ProcNoise.Smoothstep(_s.roadStraightRange, _s.roadWindRange, dTown)
                           * _s.roadMaxTurnDegrees * Mathf.Deg2Rad * turnScale;
            dir = Rotate(dir, DetHash.Angle(DetHash.Hash(i, j, 0xA11E, Seed)) * wander);

            _tangentCache[key] = dir;
            return dir;
        }

        void AccumulateNodeDir(int i, int j, bool horizontal, int sign,
                               ref float sx, ref float sy, ref int count)
        {
            int ei = i, ej = j;
            if (sign < 0)
            {
                if (horizontal) ei = i - 1; else ej = j - 1;
            }

            if (!EdgeOpen(new RoadEdgeKey(ei, ej, horizontal))) return;

            sx += horizontal ? sign : 0f;
            sy += horizontal ? 0f : sign;
            count++;
        }

        static Vector2 Rotate(Vector2 v, float radians)
        {
            float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        /// <summary>The full, sampled centre line of one edge (built once per edge).</summary>
        public RoadEdge GetEdge(RoadEdgeKey key)
        {
            if (_cache.TryGetValue(key, out RoadEdge cached)) return cached;

            Vector2 a = NodePos(key.i, key.j);
            Vector2 b = key.horizontal ? NodePos(key.i + 1, key.j) : NodePos(key.i, key.j + 1);
            Vector2 ta = NodeTangent(key.i, key.j);
            Vector2 tb = key.horizontal ? NodeTangent(key.i + 1, key.j) : NodeTangent(key.i, key.j + 1);

            uint h1 = DetHash.Hash(key.i, key.j, key.horizontal ? 0xB0B0 : 0xC0C0, Seed);
            uint h2 = DetHash.Mix(h1 ^ 0x5BD1E995u);

            float len = _size;

            // The town approach is meant to stay straight, so it is not curled at all.
            bool pinned = _forcedDir != null && (_forcedDir.ContainsKey(NodeKey(key.i, key.j)) ||
                                                 _forcedDir.ContainsKey(NodeKey(
                                                     key.horizontal ? key.i + 1 : key.i,
                                                     key.horizontal ? key.j : key.j + 1)));
            float curl = pinned ? 0f : CurlWeight((a + b) * 0.5f);

            // A curled edge gets its straight control offsets spread along the spiral's growing
            // segments (see CurlPolyline): a long straight arm next to a tight loop would read as
            // a jump in the descent above the ground, not as a road.
            float k1, k2;
            if (curl > 0.001f)
            {
                k1 = Mathf.Clamp((0.72f - 0.06f * CurlTurns) / (Mathf.PI * 2f * CurlTurns * _size), 0.05f, 0.2f);
                k2 = k1 * 2f;
            }
            else
            {
                k1 = 0.28f + 0.18f * DetHash.Float01(h1);
                k2 = 0.28f + 0.18f * DetHash.Float01(h2);
            }

            Vector2 c1 = a + Vector2.ClampMagnitude(ta * (len * k1), 0.6f * len);
            Vector2 c2 = b + Vector2.ClampMagnitude(-tb * (len * k2), 0.6f * len);

            float distFactor = ProcNoise.Smoothstep(_s.roadStraightRange, _s.roadWindRange,
                                                    Vector2.Distance((a + b) * 0.5f, TownCenter));
            float half = Mathf.Lerp(_s.roadHalfWidthNear, _s.roadHalfWidthFar,
                                    Mathf.Max(distFactor, DetHash.Float01(DetHash.Mix(h1 ^ 0x7777u)) * 0.5f));

            // Curly roads get more samples so the wiggles stay smooth.
            int count = curl > 0.001f ? 64 : 24;

            // Arc length of the base curve: the Bezier's control points sit inside its own box, so
            // "len" (the lattice edge) is not the length of the road - wiggles in t would alias.
            float arc = 0f;
            float peak = 0f;
            if (curl > 0.001f)
            {
                Vector2 prev = a;
                for (int s = 1; s <= 16; s++)
                {
                    Vector2 q = Bezier(a, c1, c2, b, s / 16f);
                    arc += Vector2.Distance(prev, q);
                    prev = q;
                }

                // The tangent shrinks (and doubles back) where the control points cross, so the
                // wiggle is driven by the straight progress instead - always forward, no spikes.
                for (int s = 0; s <= 16; s++)
                {
                    float u = s / 16f;
                    peak = Mathf.Max(peak, Vector2.Distance(Bezier(a, c1, c2, b, u), a + (b - a) * u));
                }
            }

            var raw = new Vector2[count];
            for (int s = 0; s < count; s++)
            {
                float t = s / (float)(count - 1);
                Vector2 p = Bezier(a, c1, c2, b, t);
                if (curl <= 0.001f)
                {
                    raw[s] = p;
                    continue;
                }

                // The Bezier base already bows a little, so only the room that is left over is
                // spent on the wiggles. The wiggle runs in t (always forward along the road) across
                // the chord direction, and the road is finally pulled back inside the corridor the
                // terrain grades for it - never out of the apron.
                float room = Mathf.Max(0f, CurlMaxWander * curl - peak);
                Vector2 bent = Vector2.Lerp(a, b, t) + CurlOffset(key, a, b, t * len, room * 1.5f, Vector2.zero);
                Vector2 beyond = bent - Vector2.Lerp(a, b, t);
                float limit = CurlMaxWander * curl;
                raw[s] = beyond.magnitude > limit && beyond.magnitude > 0.0001f
                    ? Vector2.Lerp(a, b, t) + beyond * (limit / beyond.magnitude)
                    : bent;
            }

            // Two samples can land on each other on a very tight wiggle: drop the duplicates so
            // the side normal never divides by zero.
            var kept = new List<Vector2>(count);
            for (int s = 0; s < count; s++)
            {
                if (kept.Count == 0 || (raw[s] - kept[kept.Count - 1]).sqrMagnitude > 1e-6f) kept.Add(raw[s]);
                else if (s == count - 1) kept[kept.Count - 1] = raw[s]; // keep the exact end point
            }

            int n = kept.Count;
            var pts = new Vector3[n];
            var nrm = new Vector3[n];

            for (int s = 0; s < n; s++)
            {
                // Central difference on the curled line, so the side normal stays correct even
                // where two loops touch and the road doubles back on itself.
                Vector2 prev = kept[System.Math.Max(0, s - 1)];
                Vector2 next = kept[System.Math.Min(n - 1, s + 1)];
                Vector2 d = next - prev;
                Vector2 fallback = b - a;
                d = d.sqrMagnitude > 1e-9f ? d.normalized
                    : (fallback.sqrMagnitude > 1e-9f ? fallback.normalized : Vector2.up);

                pts[s] = new Vector3(kept[s].x, 0f, kept[s].y);
                nrm[s] = new Vector3(-d.y, 0f, d.x);
            }

            Vector2 lo = Vector2.Min(a, b);
            Vector2 hi = Vector2.Max(a, b);
            for (int s = 0; s < n; s++)
            {
                lo = Vector2.Min(lo, new Vector2(pts[s].x, pts[s].z));
                hi = Vector2.Max(hi, new Vector2(pts[s].x, pts[s].z));
            }

            // The bounds are the real extent of the curled line padded by half the road, so any
            // chunk the road (or a loop of it) passes through gets the edge.
            Vector3 pad = new Vector3(half, 0f, half);
            var bnds = new Bounds();
            bnds.SetMinMax(new Vector3(lo.x, 0f, lo.y) - pad, new Vector3(hi.x, 0f, hi.y) + pad);

            var edge = new RoadEdge
            {
                key = key,
                a = a,
                b = b,
                points = pts,
                normals = nrm,
                bounds = bnds,
                halfWidth = half,
                curl = curl,
                wander = CurlMaxWander * curl,
            };
            _cache[key] = edge;
            return edge;
        }

        static Vector2 Bezier(Vector2 a, Vector2 c1, Vector2 c2, Vector2 b, float t)
        {
            float u = 1f - t;
            float uu = u * u, tt = t * t;
            return uu * u * a + 3f * uu * t * c1 + 3f * u * tt * c2 + tt * t * b;
        }

        /// <summary>
        /// Turns one straight stretch into the same kind of curl the country roads use (the
        /// hand-built town roads that run through the generated country). The stretch is cut into
        /// pieces no longer than <see cref="CurlSpan"/>, the curl is strongest in the pieces that
        /// are furthest from the town and zero at both ends, so the road still meets the authored
        /// strips of the hand-built map exactly where it is supposed to.
        /// </summary>
        public void CurlPolyline(List<Vector2> line)
        {
            if (line == null || line.Count < 2) return;
            if (!CurlyRoads) return;

            float span = CurlSpan;
            var src = line.ToArray();

            // Cut the road into short pieces, then curl each piece independently.
            var cuts = new List<Vector2>(src.Length * 4) { src[0] };
            for (int i = 0; i < src.Length - 1; i++)
            {
                Vector2 p0 = src[i], p1 = src[i + 1];
                int pieces = System.Math.Max(1, Mathf.CeilToInt(Vector2.Distance(p0, p1) / span));
                for (int k = 1; k <= pieces; k++) cuts.Add(Vector2.Lerp(p0, p1, k / (float)pieces));
            }

            var pts = new List<Vector2>(cuts.Count * 24) { cuts[0] };
            for (int i = 0; i < cuts.Count - 1; i++)
            {
                Vector2 p0 = cuts[i], p1 = cuts[i + 1];
                var key = new RoadEdgeKey(i, i, false);
                Vector2 dir = p1 - p0;
                float segLen = dir.magnitude;
                if (segLen <= 0.0001f) continue;
                dir /= segLen;

                int steps = System.Math.Clamp(Mathf.CeilToInt(segLen / 3f), 6, 64);
                for (int k = 1; k <= steps; k++)
                {
                    float t = k / (float)steps;
                    Vector2 p = Vector2.Lerp(p0, p1, t);
                    pts.Add(p + CurlOffset(key, p0, p1, t * segLen, CurlWeight(p) * CurlAmount, dir));
                }
            }

            line.Clear();
            line.AddRange(pts);
        }

        static Vector2 BezierTangent(Vector2 a, Vector2 c1, Vector2 c2, Vector2 b, float t)
        {
            float u = 1f - t;
            return 3f * u * u * (c1 - a) + 6f * u * t * (c2 - c1) + 3f * t * t * (b - c2);
        }

        /// <summary>
        /// Every road edge that can possibly reach the given rectangle. Built once per chunk
        /// so the terrain mesh, the vegetation pass and the road-side props all reuse it.
        /// </summary>
        public RoadField BuildField(float x0, float z0, float x1, float z1, float pad)
        {
            var field = new RoadField();
            // Every edge whose box overlaps this rectangle is considered. A curled road's box is
            // its whole loop (wander wide), so its box can swallow a chunk the centre line never
            // touches - the window is therefore taken from the ring of edge boxes around this
            // rectangle, not from the edge cells alone.
            var rect = new Bounds();
            rect.SetMinMax(new Vector3(x0 - pad, -1f, z0 - pad), new Vector3(x1 + pad, 1f, z1 + pad));

            float margin = _size + CurlMaxWander;
            int i0 = Mathf.FloorToInt((x0 - margin) / _size) - 1;
            int i1 = Mathf.CeilToInt((x1 + margin) / _size) + 1;
            int j0 = Mathf.FloorToInt((z0 - margin) / _size) - 1;
            int j1 = Mathf.CeilToInt((z1 + margin) / _size) + 1;

            for (int i = i0; i <= i1; i++)
            {
                for (int j = j0; j <= j1; j++)
                {
                    var h = new RoadEdgeKey(i, j, true);
                    if (EdgeOpen(h))
                    {
                        var e = GetEdge(h);
                        if (Reaches(e, rect)) field.Edges.Add(e);
                    }

                    var v = new RoadEdgeKey(i, j, false);
                    if (EdgeOpen(v))
                    {
                        var e = GetEdge(v);
                        if (Reaches(e, rect)) field.Edges.Add(e);
                    }
                }
            }

            field.Prepare();
            return field;
        }

        /// <summary>
        /// Does this road come anywhere near the rectangle? A curled road loops sideways past the
        /// chunk that owns it, so an edge whose bounds merely contain the rectangle is kept too:
        /// the loop can cut straight through it while its own box never intersects the rectangle.
        /// </summary>
        static bool Reaches(RoadEdge e, Bounds rect)
        {
            return e.bounds.Intersects(rect) || rect.Contains(e.bounds.center) || e.bounds.Contains(rect.center);
        }

        /// <summary>Distance from a point to the nearest road centre line (metres; large when none).</summary>
        public float DistanceToRoad(float x, float z, out float halfWidth)
        {
            var field = BuildField(x, z, x, z, _s.roadMaxApron);
            RoadHit hit = field.Query(x, z);
            halfWidth = hit.halfWidth;
            return hit.distance;
        }

        /// <summary>
        /// Height the roads are graded to. It varies very slowly so the roads keep a
        /// walkable grade no matter what the surrounding biome does.
        /// </summary>
        public float RoadElevation(float x, float z)
        {
            float big = ProcNoise.Fbm(x * 0.0016f, z * 0.0016f, Seed + 991u, 3) - 0.5f;
            float detail = ProcNoise.Fbm(x * 0.013f, z * 0.013f, Seed + 997u, 2) - 0.5f;
            float h = _s.baseHeight + 4f + big * 2f * _s.roadRelief + detail * 1.4f;
            return Mathf.Clamp(h, 6f, _s.maxHeight - 20f);
        }
    }
}

