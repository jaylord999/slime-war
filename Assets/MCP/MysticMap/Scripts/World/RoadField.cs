using System.Collections.Generic;
using UnityEngine;

namespace MysticMap.World
{
    /// <summary>Result of asking the road network where the nearest road is.</summary>
    public struct RoadHit
    {
        public float distance;      // to the road centre line (large when there is none)
        public float halfWidth;     // half width of that road
        public Vector3 closest;     // closest point on the centre line
        public Vector3 side;        // unit side direction there (points to one verge)
        public Vector3 forward;     // unit direction the road runs in
        public RoadEdge edge;       // null when no road was found

        public bool valid => edge != null;

        public static RoadHit None => new RoadHit
        {
            distance = 1e9f,
            halfWidth = 0f,
            closest = Vector3.zero,
            side = Vector3.right,
            forward = Vector3.forward,
            edge = null,
        };
    }

    /// <summary>
    /// The roads that can reach one region (usually a chunk). Built once per chunk from
    /// <see cref="RoadNetworkGenerator.BuildField"/> and then reused for every terrain
    /// vertex, every vegetation candidate and every road-side prop, which keeps the
    /// streaming cost predictable.
    /// </summary>
    public sealed class RoadField
    {
        public readonly List<RoadEdge> Edges = new List<RoadEdge>();

        public int Count => Edges.Count;

        public bool IsEmpty => Edges.Count == 0;

        /// <summary>
        /// Adds one straight road (a hand-built road of the town) so the streamed country around the
        /// town keeps it clear of trees and draws it as a road. Returns the edge that was added.
        /// </summary>
        public RoadEdge AddStraight(Vector2 a, Vector2 b, float halfWidth)
        {
            var line = new List<Vector2> { a, b };
            return AddPolyline(line, halfWidth);
        }

        /// <summary>
        /// Adds one already-shaped road (a hand-built road of the town that has been curled, see
        /// <see cref="RoadNetworkGenerator.CurlPolyline"/>). The shape only decides the centre line:
        /// the edge keeps the straight a-b line between its two ends for its lattice identity, so
        /// the props and the distance queries treat it exactly like a straight one.
        /// </summary>
        public RoadEdge AddPolyline(List<Vector2> line, float halfWidth)
        {
            int n = line != null ? line.Count : 0;
            if (n < 2) return null;

            Vector2 a = line[0];
            Vector2 b = line[n - 1];

            // One sample every ~6 m, like the procedural edges, so a tight curl stays round.
            int samples = System.Math.Max(n - 1, Mathf.CeilToInt(PolylineLength(line) / 6f));
            var points = new Vector3[samples + 1];
            var normals = new Vector3[samples + 1];

            Vector3 min = new Vector3(float.MaxValue, 0f, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, 0f, float.MinValue);

            for (int i = 0; i <= samples; i++)
            {
                Vector2 p = SamplePolyline(line, i / (float)samples);
                Vector2 tan = PolylineTangent(line, i / (float)samples);
                if (tan.sqrMagnitude < 0.000001f) tan = b - a;
                tan = tan.sqrMagnitude > 0.000001f ? tan.normalized : Vector2.up;

                points[i] = new Vector3(p.x, 0f, p.y);
                normals[i] = new Vector3(-tan.y, 0f, tan.x);

                min = Vector3.Min(min, points[i]);
                max = Vector3.Max(max, points[i]);
            }

            var pad = new Vector3(Mathf.Max(0.5f, halfWidth), 0f, Mathf.Max(0.5f, halfWidth));
            var bounds = new Bounds();
            bounds.SetMinMax(min - pad, max + pad);

            var edge = new RoadEdge
            {
                key = default,
                a = a,
                b = b,
                points = points,
                normals = normals,
                bounds = bounds,
                halfWidth = Mathf.Max(0.5f, halfWidth),
            };

            Edges.Add(edge);
            Prepare();
            return edge;
        }

        static float PolylineLength(List<Vector2> line)
        {
            float len = 0f;
            for (int i = 0; i < line.Count - 1; i++) len += Vector2.Distance(line[i], line[i + 1]);
            return len;
        }

        /// <summary>Point at parameter t (0 at the first point, 1 at the last).</summary>
        static Vector2 SamplePolyline(List<Vector2> line, float t)
        {
            float total = PolylineLength(line);
            if (total <= 0.0001f) return line[0];

            float want = Mathf.Clamp01(t) * total;
            float walked = 0f;
            for (int i = 0; i < line.Count - 1; i++)
            {
                float seg = Vector2.Distance(line[i], line[i + 1]);
                if (walked + seg >= want && seg > 0.0001f)
                    return Vector2.Lerp(line[i], line[i + 1], (want - walked) / seg);
                walked += seg;
            }

            return line[line.Count - 1];
        }

        /// <summary>Direction of the polyline at parameter t (central difference, so it never flips).</summary>
        static Vector2 PolylineTangent(List<Vector2> line, float t)
        {
            const float e = 0.002f;
            Vector2 before = SamplePolyline(line, Mathf.Max(0f, t - e));
            Vector2 after = SamplePolyline(line, Mathf.Min(1f, t + e));
            return after - before;
        }

        /// <summary>Reserved so a spatial index could be added later without touching callers.</summary>
        public void Prepare()
        {
            // Edges are already bounds-filtered by the builder; nothing to do for now.
        }

        public bool TryQuery(float x, float z, float maxDistance, out RoadHit hit)
        {
            hit = Query(x, z);
            return hit.valid && hit.distance <= maxDistance;
        }

        public RoadHit Query(float x, float z)
        {
            var best = RoadHit.None;

            if (Edges.Count == 0) return best;

            var p = new Vector2(x, z);
            for (int e = 0; e < Edges.Count; e++)
            {
                RoadEdge edge = Edges[e];
                if (edge.points == null || edge.points.Length < 2) continue;

                // Bounds early-out: the centre line can never be closer than this.
                if (edge.bounds.SqrDistance(new Vector3(x, 0f, z)) > best.distance * best.distance) continue;

                Vector3[] pts = edge.points;
                for (int s = 0; s < pts.Length - 1; s++)
                {
                    Vector2 a = new Vector2(pts[s].x, pts[s].z);
                    Vector2 b = new Vector2(pts[s + 1].x, pts[s + 1].z);

                    float d = DistToSegment(a, b, p, out float t);
                    if (d >= best.distance) continue;

                    best.distance = d;
                    best.halfWidth = edge.halfWidth;
                    best.edge = edge;

                    Vector3 pa = pts[s], pb = pts[s + 1];
                    Vector3 na = edge.normals[s], nb = edge.normals[s + 1];

                    best.closest = Vector3.Lerp(pa, pb, t);
                    best.side = Vector3.Slerp(na, nb, t).normalized;
                    if (best.side.sqrMagnitude < 0.5f) best.side = Vector3.right;
                    Vector3 fwd = pb - pa;
                    best.forward = fwd.sqrMagnitude > 0.0001f ? fwd.normalized : Vector3.forward;
                }
            }

            return best;
        }

        static float DistToSegment(Vector2 a, Vector2 b, Vector2 p, out float t)
        {
            Vector2 ab = b - a;
            float len = ab.sqrMagnitude;
            t = len > 0.0001f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len) : 0f;
            return Vector2.Distance(a + ab * t, p);
        }
    }
}
