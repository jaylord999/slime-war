using UnityEngine;

namespace MysticMap.World
{
    /// <summary>
    /// The one place the town's real size lives at runtime.
    ///
    /// The town footprint is authored in the editor (<c>MysticMapBuilder.FortHX / FortHZ</c> x
    /// <c>TownAreaScale</c>). Runtime scripts cannot read those constants: the builder is
    /// editor-only, and anything that copied the numbers by hand drifted out of date when the
    /// town was grown - which made roadside props and glow flowers spawn all over the outer
    /// town and left the middle looking like the whole place.
    ///
    /// So the editor stamps the real numbers here once (<see cref="Set"/>), the values survive
    /// in the scene, and every runtime script reads <see cref="HalfExtents"/> instead of
    /// keeping its own copy.
    /// </summary>
    public static class TownBounds
    {
        public static Vector2 Center { get; private set; } = new Vector2(500f, 520f);
        public static float HalfX { get; private set; } = 0f;
        public static float HalfZ { get; private set; } = 0f;

        /// <summary>True once the editor has stamped the real town size.</summary>
        public static bool Known => HalfX > 1f && HalfZ > 1f;

        /// <summary>Half width / half depth of the town, measured to the wall centre line.</summary>
        public static Vector2 HalfExtents => new Vector2(HalfX, HalfZ);

        /// <summary>Stamped by the editor when the town is built / captured.</summary>
        public static void Set(Vector2 center, float halfX, float halfZ)
        {
            Center = center;
            HalfX = halfX;
            HalfZ = halfZ;
        }

        /// <summary>Drops back to "unknown" (used before the town is rebuilt).</summary>
        public static void Clear()
        {
            HalfX = 0f;
            HalfZ = 0f;
        }

        /// <summary>
        /// Is this point inside the town rectangle, expanded by <paramref name="pad"/> metres?
        /// Uses the real size when it is known, otherwise falls back to the supplied default so
        /// a scene that was never stamped still behaves sensibly.
        /// </summary>
        public static bool Contains(float x, float z, float pad,
                                    float fallbackHalfX = 78f, float fallbackHalfZ = 64f)
        {
            float hx = Known ? HalfX : fallbackHalfX;
            float hz = Known ? HalfZ : fallbackHalfZ;
            return Mathf.Abs(x - Center.x) < hx + pad && Mathf.Abs(z - Center.y) < hz + pad;
        }

        /// <summary>
        /// <see cref="Contains(float,float,float,float,float)"/> with the per-axis padding that
        /// always goes with the town (the scripts here pad by 2 m).
        /// </summary>
        public static bool ContainsPadded(float x, float z, float pad = 2f)
            => Contains(x, z, pad);
    }
}
