using System.Collections.Generic;
using UnityEngine;

namespace MysticMap.World
{
    /// <summary>
    /// Scatters trees, ground cover and rocks over one chunk. Every candidate comes from a
    /// jittered grid whose randomness is derived only from (seed, chunk, cell), so the same
    /// spot always grows the same plants no matter when you walk past it, and nothing has to
    /// be stored in the scene.
    /// </summary>
    public class VegetationGenerator
    {
        readonly WorldSettings _s;
        readonly TerrainGenerator _terrain;
        readonly List<GameObject> _treePool = new List<GameObject>();

        const string TreeFolder = "FantasyEnvironments/Environments/Ambient-Occlusion-Trees/Prefabs";

        /// <summary>
        /// True while the trees are grown around the player instead of with the chunk. The streamer
        /// switches it on, so the chunk is built with its ground cover only and its trees are added
        /// the moment the player can see it (see ChunkManager / TreeGenerator).
        /// </summary>
        public bool TreesHandledByStreamer;

        public VegetationGenerator(WorldSettings settings, TerrainGenerator terrain)
        {
            _s = settings;
            _terrain = terrain;
        }

        /// <summary>Populates a chunk. <paramref name="densityScale"/> lets far chunks stay cheap.</summary>
        public void Populate(WorldChunk chunk, RoadField field, float densityScale)
        {
            WorldPrefabPalette p = _s.palette;
            if (p == null || p.IsEmpty || chunk.Data == null) return;

            float size = _s.chunkSize;
            float worldX = chunk.coord.x * size;
            float worldZ = chunk.coord.y * size;

            BiomeWeights w = _terrain.Biomes.Weights(worldX + size * 0.5f, worldZ + size * 0.5f);
            float moisture = _terrain.Biomes.Moisture(worldX + size * 0.5f, worldZ + size * 0.5f);

            uint salt = (uint)_s.seed * 2654435761u;
            float rockBias = Mathf.Clamp01(w.rocky + w.highlands * 0.5f);

            // ---- trees: the SAME scatter pass as the grass below (see ScatterTrees) ---
            // While the streamer grows trees around the player, the chunk is built without them and
            // they are added (or given back) as the player looks at it.
            if (!TreesHandledByStreamer) ScatterTrees(chunk, field, densityScale);

            // ---- bushes and ferns --------------------------------------------
            float bushDensity = (w.forest * 0.6f + w.meadow * 0.25f + w.ruins * 0.2f +
                                 w.labyrinth * 0.35f) * densityScale;
            Scatter(chunk, field, p.bushes, _s.groundSpacing * 1.8f, bushDensity,
                    Mathf.Max(1, _s.maxGroundPlantsPerChunk / 3),
                    new DetRandom(chunk.coord.x, chunk.coord.y, salt + 67u),
                    0.8f, 1.25f, rockBias, false);

            // ---- grass and flowers -------------------------------------------
            float grassDensity = (0.45f + moisture * 0.45f) * (1f - rockBias * 0.6f) * densityScale;
            Scatter(chunk, field, p.grass, _s.groundSpacing, grassDensity,
                    _s.maxGroundPlantsPerChunk,
                    new DetRandom(chunk.coord.x, chunk.coord.y, salt + 103u),
                    0.8f, 1.35f, rockBias, false);

            float flowerDensity = (w.meadow * 0.5f + w.ruins * 0.3f + w.forest * 0.18f +
                                   moisture * 0.2f) * densityScale;
            Scatter(chunk, field, p.flowers, _s.groundSpacing * 1.6f, flowerDensity,
                    Mathf.Max(1, _s.maxGroundPlantsPerChunk / 2),
                    new DetRandom(chunk.coord.x, chunk.coord.y, salt + 149u),
                    0.7f, 1.2f, rockBias, false);

            // ---- rocks --------------------------------------------------------
            float rockDensity = (w.rocky * 0.95f + w.highlands * 0.3f + w.ruins * 0.25f) * densityScale;
            Scatter(chunk, field, p.rocks, _s.rockSpacing, rockDensity, _s.maxRocksPerChunk,
                    new DetRandom(chunk.coord.x, chunk.coord.y, salt + 191u),
                    0.7f, 1.6f, 0f, true);

            // ---- a few stones where the ground is bare -------------------------
            float stoneDensity = (0.12f + rockBias * 0.4f) * densityScale;
            Scatter(chunk, field, p.stones, _s.groundSpacing * 2.2f, stoneDensity,
                    Mathf.Max(1, _s.maxRocksPerChunk),
                    new DetRandom(chunk.coord.x, chunk.coord.y, salt + 233u),
                    0.6f, 1.1f, 0f, false);
        }

        /// <summary>
        /// Collects the tree prefabs that may be used. The palette comes first; when it is empty (a
        /// fresh component, a re-serialised scene) the forest of the pack is loaded from its folder,
        /// so an empty palette can never turn the whole world treeless.
        /// </summary>
        void FillTreePool()
        {
            if (_treePool.Count > 0) return;

            GameObject[] paletteTrees = _s.palette != null ? _s.palette.trees : null;
            if (paletteTrees != null)
            {
                for (int i = 0; i < paletteTrees.Length; i++)
                    if (paletteTrees[i] != null) _treePool.Add(paletteTrees[i]);
            }

            if (_treePool.Count > 0) return;

            GameObject[] all = Resources.LoadAll<GameObject>(TreeFolder);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                if (all[i].name.ToLowerInvariant().Contains("stump")) continue;
                _treePool.Add(all[i]);
            }
        }

        /// <summary>Number of tree prefabs the forest can use (0 = neither palette nor folder has any).</summary>
        public int TreePoolCount
        {
            get { FillTreePool(); return _treePool.Count; }
        }

        /// <summary>
        /// The FOREST: exactly the same scatter pass as the grass, the flowers and the rocks - a
        /// jittered grid, one roll per cell, the same road/height rules - only with the tree spacing,
        /// the tree density and the tree cap. Trees are ordinary props of the chunk, so they stream
        /// in and out with it and nothing about them is stored.
        /// </summary>
        public void ScatterTrees(WorldChunk chunk, RoadField field, float densityScale)
        {
            if (chunk == null || chunk.Data == null || !_s.generateEnvironment) return;

            FillTreePool();
            if (_treePool.Count == 0) return;

            // Canopy density: the same "one roll per cell" as the grass, but the trees are scaled to
            // the spacing so a 6 m grid is a real forest and a dense one fills up to the chunk cap.
            float treeDensity = (0.55f + _terrain.Biomes.Moisture(chunk.coord.x * _s.chunkSize,
                                                                  chunk.coord.y * _s.chunkSize) * 0.4f) *
                                densityScale;

            int before = chunk.PropCount;
            Scatter(chunk, field, _treePool.ToArray(), Mathf.Max(4f, _s.treeSpacing), treeDensity,
                    _s.maxTreesPerChunk,
                    new DetRandom(chunk.coord.x, chunk.coord.y, (uint)_s.seed * 2654435761u + 3011u),
                    0.85f, 1.3f, 0f, true);

            // Name the new props so the magic (and ClearTreeProps) can tell a tree from grass.
            Transform root = chunk.PropsRoot;
            if (root != null)
            {
                for (int i = Mathf.Max(0, root.childCount - (chunk.PropCount - before)); i < root.childCount; i++)
                {
                    Transform child = root.GetChild(i);
                    if (child == null || child.name.StartsWith("Tree_")) continue;
                    child.name = "Tree_" + child.name.Replace("(Clone)", string.Empty);
                }
            }

            chunk.SetTreeCount(chunk.PropCount - before);
        }

        /// <summary>
        /// One jittered-grid pass. The grid guarantees a minimum spacing; the hash decides
        /// whether each cell actually holds something - which is what keeps a forest a
        /// forest and a meadow open, without any neighbour lookups or stored data.
        /// </summary>
        void Scatter(WorldChunk chunk, RoadField field, GameObject[] pool, float spacing,
                     float density, int maxCount, DetRandom rng, float minScale, float maxScale,
                     float rockBias, bool trees)
        {
            if (pool == null || pool.Length == 0 || density <= 0.001f || maxCount <= 0) return;

            float size = _s.chunkSize;
            int perSide = Mathf.Max(1, Mathf.RoundToInt(size / Mathf.Max(1f, spacing)));
            float cell = size / perSide;
            float maxSlope = trees ? _s.maxVegetationSlope : _s.maxVegetationSlope + 12f;
            int placed = 0;

            for (int j = 0; j < perSide && placed < maxCount; j++)
            {
                for (int i = 0; i < perSide && placed < maxCount; i++)
                {
                    // Fixed draw order keeps the whole chunk deterministic.
                    float jx = rng.Next01();
                    float jz = rng.Next01();
                    float pick = rng.Next01();
                    float scaleT = rng.Next01();
                    float yaw = rng.Next01() * 360f;
                    float roll = rng.Next01();

                    float chance = density;
                    if (trees && rockBias > 0.45f) chance *= 0.55f;   // rocky ground keeps few trees
                    if (roll >= chance) continue;

                    float localX = (i + 0.15f + jx * 0.7f) * cell;
                    float localZ = (j + 0.15f + jz * 0.7f) * cell;

                    if (!Allowed(chunk, field, localX, localZ, maxSlope, trees)) continue;

                    GameObject prefab = pool[Mathf.Min(pool.Length - 1, (int)(pick * pool.Length))];
                    float h = chunk.HeightAtLocal(localX, localZ);
                    var world = new Vector3(chunk.coord.x * size + localX, h,
                                            chunk.coord.y * size + localZ);

                    if (WorldSpawn.Spawn(prefab, chunk.PropsRoot, world, yaw,
                                         Mathf.Lerp(minScale, maxScale, scaleT)) != null)
                    {
                        chunk.AddProp();
                        placed++;
                    }
                }
            }
        }

        bool Allowed(WorldChunk chunk, RoadField field, float localX, float localZ,
                     float maxSlope, bool trees)
        {
            TerrainGenerator.ChunkMesh d = chunk.Data;
            float size = _s.chunkSize;

            // Never in the middle of a road (kept clear for walking).
            if (field != null && !field.IsEmpty)
            {
                RoadHit hit = field.Query(chunk.coord.x * size + localX, chunk.coord.y * size + localZ);
                float allowFrom = _s.roadClearance + (trees ? 1.5f : 0f);
                if (hit.valid && hit.distance < allowFrom + hit.halfWidth) return false;
            }

            // Not on cliffs.
            float hL = d.SampleHeight(localX - 2f, localZ);
            float hR = d.SampleHeight(localX + 2f, localZ);
            float hD = d.SampleHeight(localX, localZ - 2f);
            float hU = d.SampleHeight(localX, localZ + 2f);
            float rise = Mathf.Max(Mathf.Abs(hR - hL), Mathf.Abs(hU - hD));
            float slope = Mathf.Atan2(rise, 4f) * Mathf.Rad2Deg;
            return slope <= maxSlope;
        }
    }
}
