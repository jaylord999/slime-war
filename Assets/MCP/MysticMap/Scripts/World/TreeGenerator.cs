using UnityEngine;

namespace MysticMap.World
{
    /// <summary>
    /// The forest of the endless world, seen from the PLAYER's side.
    ///
    /// There is only ONE tree algorithm and it is the same scatter pass the grass, the flowers and
    /// the rocks use (<see cref="VegetationGenerator.ScatterTrees"/>: a jittered grid, one roll per
    /// cell, the same road / height rules). This class adds nothing to it. It only answers two
    /// questions the streamer asks about the player, live, every time:
    ///
    ///   * is this spot in front of the player (or close enough to be noticed)? -> <see cref="IsSeen"/>
    ///   * how dense should the trees of a chunk be, given how far the player is? -> <see cref="DensityScale"/>
    ///
    /// Nothing is stored here: no positions, no list of trees, no copy of the player. Press Play,
    /// walk or turn and the forest that the streamer grows changes with you, chunk by chunk, from
    /// the same deterministic scatter the rest of the vegetation uses.
    /// </summary>
    public class TreeGenerator
    {
        readonly WorldSettings _s;

        public TreeGenerator(WorldSettings settings)
        {
            _s = settings;
        }

        /// <summary>Who the world is grown around. Read live, never copied.</summary>
        public Transform Player { get; set; }

        /// <summary>Where the player looks. When empty the player's own forward is used.</summary>
        public Transform View { get; set; }

        public int NearChunks { get; set; } = 1;

        /// <summary>Every tree this close to the player (m) stays, even when it is behind them.</summary>
        public float KeepRadius { get; set; } = 45f;

        /// <summary>Full view cone (degrees) a tree has to be inside to be worth growing.</summary>
        public float ViewAngle { get; set; } = 140f;

        /// <summary>Off = grow the whole forest everywhere (no player needed).</summary>
        public bool GrowOnlyInView { get; set; } = true;

        /// <summary>Diagnostics: chunks dressed with trees and trees placed since <see cref="ResetStats"/>.</summary>
        public int ChunksGrown { get; private set; }
        public int TreesGrown { get; private set; }

        public void ResetStats()
        {
            ChunksGrown = 0;
            TreesGrown = 0;
        }

        /// <summary>Records one chunk that was dressed (called by the streamer).</summary>
        public void NoteChunk(int trees)
        {
            ChunksGrown++;
            TreesGrown += Mathf.Max(0, trees);
        }

        /// <summary>Records trees that were given back when the chunk left the view.</summary>
        public void NoteRemoved(int trees)
        {
            TreesGrown = Mathf.Max(0, TreesGrown - Mathf.Max(0, trees));
        }

        /// <summary>
        /// True when a tree at this world position is worth growing right now: it is close to the
        /// player, or it is in front of them. Computed from where the player stands this instant.
        /// </summary>
        public bool IsSeen(float x, float z)
        {
            if (!GrowOnlyInView || Player == null) return true;

            Vector3 p = Player.position;
            float dx = x - p.x;
            float dz = z - p.z;
            float distSq = dx * dx + dz * dz;

            if (distSq <= KeepRadius * KeepRadius) return true;

            Transform view = View != null ? View : Player;
            Vector3 f = view.forward;
            f.y = 0f;
            float fl = f.magnitude;
            if (fl < 0.0001f) return true;              // looking straight up / down

            float cosHalf = Mathf.Cos(ViewAngle * 0.5f * Mathf.Deg2Rad);
            float dot = (dx * f.x + dz * f.z) / fl;
            return dot >= cosHalf * Mathf.Sqrt(distSq);
        }

        /// <summary>
        /// 1 for a chunk the player is in or right next to: its trees grow once and then stay,
        /// because nobody notices an unseen trunk behind them. Further out the forest thins, so
        /// turning around never reveals a solid wall of trees that were not there before.
        /// </summary>
        public float DensityScale(Vector2Int coord)
        {
            if (!GrowOnlyInView || Player == null) return 1f;

            float size = _s.chunkSize;
            Vector3 p = Player.position;
            int dx = coord.x - Mathf.FloorToInt(p.x / size);
            int dz = coord.y - Mathf.FloorToInt(p.z / size);
            int ring = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz));

            if (ring <= NearChunks) return 1f;
            return Mathf.Lerp(1f, 0.35f, Mathf.Clamp01((ring - NearChunks) / 2f));
        }
    }
}
