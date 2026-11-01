using UnityEngine;

namespace MysticMap.World
{
    /// <summary>
    /// Owns the procedural passes for one world and produces a chunk on demand. Everything
    /// that decides what a chunk looks like lives behind this class, so the streaming
    /// manager only has to ask for a coordinate.
    /// </summary>
    public class WorldGenerator
    {
        public readonly WorldSettings Settings;
        public readonly TownAnchor Town;
        public readonly TerrainGenerator Terrain;
        public readonly VegetationGenerator Vegetation;
        public readonly TreeGenerator Trees;
        public readonly EnvironmentGenerator Environment;

        public WorldGenerator(WorldSettings settings, TownAnchor town)
        {
            Settings = settings;
            Town = town;
            Settings.Validate();
            Terrain = new TerrainGenerator(Settings, town);
            Trees = new TreeGenerator(Settings);
            Vegetation = new VegetationGenerator(Settings, Terrain);
            Environment = new EnvironmentGenerator(Settings, Terrain);
        }

        public float ChunkSize => Settings.chunkSize;

        /// <summary>
        /// Points the forest at the player: trees are only grown where the player can see them, and
        /// the answer is worked out live - nothing about the player or the trees is stored.
        /// </summary>
        public void SetObserver(Transform player, Transform view, bool growOnlyInView = true)
        {
            if (Trees == null) return;
            Trees.Player = player;
            Trees.View = view;
            Trees.GrowOnlyInView = growOnlyInView;
        }

        // =====================================================================
        //  Chunk placement
        // =====================================================================
        /// <summary>World position of a chunk's corner.</summary>
        public Vector3 OriginOf(int cx, int cz) => new Vector3(cx * ChunkSize, 0f, cz * ChunkSize);

        /// <summary>
        /// True when this chunk belongs to the hand-built town and must not be generated. With the
        /// default "keep the whole map" that is every chunk over the authored 1 km; with the town-only
        /// mode it is just the square drawn around the town, so the country around it streams normally
        /// (terrain, trees and all).
        /// </summary>
        public bool IsLegacy(Vector2Int coord)
        {
            if (Town == null) return false;
            if (Town.keepArea != LegacyArea.Town) return IsTownChunk(coord, -Settings.townSkipMargin);

            // Town-only mode: frame the town by the chunks that really hold it (the plaza, the houses
            // and the walls), plus a small green belt, and leave every other chunk to the streamer.
            return Town.KeepTown(coord.x * ChunkSize, coord.y * ChunkSize,
                                 (coord.x + 1) * ChunkSize, (coord.y + 1) * ChunkSize);
        }

        /// <summary>True when this chunk overlaps the hand-built map rectangle (any mode).</summary>
        public bool IsTownChunk(Vector2Int coord, float margin)
        {
            if (Town == null) return false;
            float s = ChunkSize;
            float x0 = coord.x * s, z0 = coord.y * s;
            return Town.OverlapsLegacy(x0, z0, x0 + s, z0 + s, margin);
        }

        /// <summary>
        /// True for a chunk that lies on the hand-built map but is still generated (the country around
        /// the town in town-only mode). The authored terrain draws its ground; the streamer only grows
        /// the vegetation there, and the trees keep clear of the roads that lead out of the town.
        /// </summary>
        public bool IsBlendChunk(Vector2Int coord) => IsTownChunk(coord, 1f) && !IsLegacy(coord);

        public Vector2Int ChunkOf(Vector3 worldPos) => new Vector2Int(
            Mathf.FloorToInt(worldPos.x / ChunkSize),
            Mathf.FloorToInt(worldPos.z / ChunkSize));

        // =====================================================================
        //  Chunk content
        // =====================================================================
        /// <summary>
        /// Builds (or rebuilds) the terrain mesh of a chunk and returns it. The road field is
        /// handed back so the vegetation pass can reuse it instead of rebuilding it.
        /// </summary>
        public RoadField BuildTerrain(WorldChunk chunk, bool highLod)
        {
            float s = ChunkSize;
            float x0 = chunk.coord.x * s, z0 = chunk.coord.y * s;

            // A chunk that already has its trees/grass keeps them: only the terrain mesh changes,
            // so re-decorating it would delete and re-spawn dozens of objects for nothing (a
            // guaranteed hitch every time the player crosses a LOD line).
            bool hadProps = chunk.PropCount > 0;
            if (hadProps) chunk.ClearProps();

            RoadField field = BuildChunkField(chunk);

            // Country that sits on the hand-built map (town-only mode): the authored terrain already
            // draws that ground, so only the trees/rocks are generated here. A second, procedural
            // mesh on top of it would need to match the authored height to the last centimetre -
            // this way there is nothing to match.
            bool groundFromTown = IsTownChunk(chunk.coord, 1f);

            TerrainGenerator.ChunkMesh data = groundFromTown
                ? Terrain.MakeProbeMesh(chunk.coord.x, chunk.coord.y)
                : Terrain.BuildMesh(chunk.coord.x, chunk.coord.y, highLod, field);

            chunk.transform.position = new Vector3(x0, 0f, z0);
            chunk.Setup(chunk.coord, data, ChunkMaterials.For(data.dominant), highLod,
                        !groundFromTown && Settings.generateColliders && highLod && data.mesh != null);

            if (hadProps) Populate(chunk, field, 1f);

            return field;
        }

        /// <summary>
        /// The roads that can reach one chunk: the generated network, plus the town's own roads when
        /// the chunk lies on the hand-built map (so trees keep those clear too).
        /// </summary>
        public RoadField BuildChunkField(WorldChunk chunk)
        {
            float s = ChunkSize;
            float x0 = chunk.coord.x * s, z0 = chunk.coord.y * s;

            RoadField field = Terrain.Roads.BuildField(x0, z0, x0 + s, z0 + s, Settings.roadMaxApron);

            if (Town != null && IsTownChunk(chunk.coord, 1f))
                Town.AddRoadsTo(field, Settings.roadHalfWidthNear);

            return field;
        }

        /// <summary>
        /// Grows the TREES of one chunk only (the grass, bushes, flowers, rocks and landmarks are
        /// left alone). Used by the streamer to dress a chunk the moment the player can see it.
        /// Returns false when the chunk cannot take trees.
        /// </summary>
        public bool PopulateTrees(WorldChunk chunk)
        {
            if (Trees == null || chunk == null || chunk.Data == null) return false;
            if (!Settings.generateEnvironment) return false;

            // The very same scatter pass the grass, flowers and rocks use (jittered grid + one roll
            // per cell), just with the tree spacing/density/cap - see VegetationGenerator.ScatterTrees.
            int before = chunk.PropCount;
            Vegetation.ScatterTrees(chunk, BuildChunkField(chunk), Trees.DensityScale(chunk.coord));
            return chunk.PropCount > before;
        }

        /// <summary>
        /// True when the player would see this chunk right now. A chunk counts as seen as soon as any
        /// part of it is inside the view cone or inside the always-kept circle, so the forest is
        /// grown from the player outwards.
        /// </summary>
        public bool CanSee(WorldChunk chunk)
        {
            if (chunk == null) return false;
            return CanSee(new Vector3(chunk.transform.position.x + ChunkSize * 0.5f, 0f,
                                      chunk.transform.position.z + ChunkSize * 0.5f), ChunkSize * 0.5f);
        }

        /// <summary>True when a square of half-size <paramref name="halfExtent"/> around a point is in view.</summary>
        public bool CanSee(Vector3 center, float halfExtent)
        {
            if (Trees == null || Trees.Player == null || !Trees.GrowOnlyInView) return true;
            return Trees.IsSeen(center.x, center.z) ||
                   Trees.IsSeen(center.x - halfExtent, center.z) ||
                   Trees.IsSeen(center.x + halfExtent, center.z) ||
                   Trees.IsSeen(center.x, center.z - halfExtent) ||
                   Trees.IsSeen(center.x, center.z + halfExtent);
        }

        /// <summary>Scatters vegetation and landmarks. Only called for chunks near the player.</summary>
        public void Populate(WorldChunk chunk, RoadField field, float densityScale)
        {
            if (!Settings.generateEnvironment || densityScale <= 0.01f) return;

            Vegetation.Populate(chunk, field, densityScale);
            Environment.Populate(chunk, field, densityScale);
        }

        /// <summary>Full creation of a chunk object (terrain + content).</summary>
        public WorldChunk CreateChunk(Transform parent, Vector2Int coord, bool highLod, bool detail,
                                      float densityScale)
        {
            var go = new GameObject("Chunk_" + coord.x + "_" + coord.y);
            go.transform.SetParent(parent, false);

            var chunk = go.AddComponent<WorldChunk>();
            RoadField field = BuildTerrain(chunk, highLod);
            if (detail) Populate(chunk, field, densityScale);
            return chunk;
        }

        // =====================================================================
        //  Height / helpers used by the player and the tools
        // =====================================================================
        /// <summary>
        /// Ground height anywhere: the hand-built terrain inside the town area, a live chunk when one
        /// exists, otherwise the procedural field.
        /// </summary>
        public float SampleHeight(float x, float z, WorldChunk liveChunk)
        {
            if (Terrain == null) return Settings.baseHeight;

            if (Town != null && Town.InsideLegacy(x, z) && Town.legacyTerrain != null)
                return Town.legacyTerrain.SampleHeight(new Vector3(x, 0f, z));

            // In town-only mode the hand-built map ends at the town and the country around it is
            // generated, so that country must follow the generated field - also for trees and rocks,
            // whose height comes from here.
            if (liveChunk != null && liveChunk.Data != null)
            {
                Vector3 o = liveChunk.transform.position;
                return liveChunk.Data.SampleHeight(x - o.x, z - o.z);
            }

            return Terrain.Height(x, z);
        }

        /// <summary>Type of country at a world position (used by the tools and gizmos).</summary>
        public BiomeType BiomeAt(float x, float z) => Terrain.Biomes.Weights(x, z).Dominant;

        /// <summary>Distance to the nearest procedural road (metres).</summary>
        public float RoadDistance(float x, float z, out float halfWidth) =>
            Terrain.Roads.DistanceToRoad(x, z, out halfWidth);

        public float RoadElevation(float x, float z) => Terrain.Roads.RoadElevation(x, z);
    }
}
