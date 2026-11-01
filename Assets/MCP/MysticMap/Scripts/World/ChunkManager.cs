using System.Collections.Generic;
using UnityEngine;

namespace MysticMap.World
{
    /// <summary>
    /// Streams the procedural world around the player.
    ///
    /// Every frame (throttled) it works out which chunk the player is in, keeps a list of
    /// the chunks that should exist, and creates a small, fixed number of them per frame so
    /// the frame time never spikes. Chunks that fall far enough behind are released - mesh,
    /// collider and all props - which keeps the scene at a constant size no matter how far
    /// you walk.
    ///
    /// The hand-built town is never touched: chunks that overlap it are simply skipped, and
    /// the procedural ground/roads blend into its edges.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MysticMap/World/Chunk Manager")]
    public class ChunkManager : MonoBehaviour
    {
        [Tooltip("Every tunable of the generated world.")]
        public WorldSettings settings = new WorldSettings();

        [Tooltip("Describes the hand-built town map. Auto-created on this object when empty.")]
        public TownAnchor townAnchor;

        [Tooltip("Who the world is streamed around (usually the player). Auto-filled in play mode.")]
        public Transform target;

        [Tooltip("Look for the player / main camera automatically.")]
        public bool autoFindPlayer = true;

        [Tooltip("Chunks are generated and released while playing.")]
        public bool streamInPlayMode = true;

        [Tooltip("Also stream while the editor is not in Play mode (used by the preview tool).")]
        public bool buildInEditor = false;

        [Tooltip("Release chunks when the component is disabled.")]
        public bool releaseOnDisable = true;

        [Tooltip("Draw the streaming area and the frozen town rectangle in the scene view.")]
        public bool drawGizmos = true;

        [Tooltip("Log how many chunks are live every so often.")]
        public bool logStats = false;

        [Header("Budget (keeps the camera smooth while the world streams)")]
        [Tooltip("Hard cap: never generate more than this many chunks in one frame, whatever the budget allows.")]
        [Range(1, 4)] public int maxChunksPerFrame = 1;
        [Tooltip("Stop generating once this much of the frame is already used up. " +
                 "0.45 means 'leave 55% of the frame for drawing and gameplay'.")]
        [Range(0.1f, 0.9f)] public float frameBudget = 0.45f;
        [Tooltip("Spread the world outwards a little wider while the camera is being swung around, so nothing pops in late.")]
        public bool extraRadiusWhileTurning = true;
        [Tooltip("Seconds between checks for 'is the view still moving?'. Bigger = cheaper.")]
        public float radiusCheckInterval = 0.2f;

        [Header("Trees (grown around the player, never stored)")]
        [Tooltip("Only grow a tree where the player can see it (in front, or close by). Off = the whole " +
                 "forest is grown everywhere. Nothing is written to the scene either way.")]
        public bool treesOnlyInPlayerView = true;
        [Tooltip("Full view cone (degrees) a tree must be inside to be worth growing.")]
        [Range(40f, 360f)] public float treeViewAngle = 140f;
        [Tooltip("Every tree this close (m) to the player stays, even when it is behind them.")]
        public float treeKeepRadius = 45f;
        [Tooltip("Chunks this close (in chunks) always grow their full tree density.")]
        [Range(0, 6)] public int treeFullDensityChunks = 1;
        [Tooltip("Seconds between 'what can the player see now?' checks. 0.15 = smooth, 0.5 = cheapest.")]
        public float treeRefreshInterval = 0.15f;
        [Tooltip("Trees grown or given back per check. 1-2 keeps the frame time flat while turning.")]
        [Range(1, 8)] public int treeRebuildsPerTick = 2;

        readonly Dictionary<Vector2Int, WorldChunk> _live = new Dictionary<Vector2Int, WorldChunk>();
        readonly List<Vector2Int> _queue = new List<Vector2Int>();
        readonly List<Vector2Int> _stale = new List<Vector2Int>();
        readonly List<float> _queueCost = new List<float>();
        readonly List<Vector2Int> _rebuild = new List<Vector2Int>();
        readonly List<bool> _rebuildDetail = new List<bool>();

        WorldGenerator _generator;
        Transform _root;
        Vector2Int _center = new Vector2Int(int.MinValue, int.MinValue);
        bool _built;
        float _logTimer;
        int _legacySkipped;
        float _treeTimer;
        readonly List<Vector2Int> _treeChecked = new List<Vector2Int>();
        int _treeCheck;

        // While frames are slow the queue is processed less often instead of more: only one in
        // every few frames, which is what keeps a weak machine from spiralling.
        int _slowSkip;
        float _radiusTimer;
        int _radiusNow = -1;

        public WorldGenerator Generator => _generator;
        public int LiveChunks => _live.Count;
        public int QueuedChunks => _queue.Count;
        public int LegacyChunksSkipped => _legacySkipped;
        public Vector2Int CenterChunk => _center;

        // =====================================================================
        //  Lifecycle
        // =====================================================================
        void Awake()
        {
            EnsureBuilt();
            StutterMonitor.EnsureExists();
        }

        void OnEnable()
        {
            EnsureBuilt();
            if (Application.isPlaying || buildInEditor) Refresh(true);
        }

        void Start()
        {
            EnsureBuilt();
            ResolveTarget();
            Refresh(true);
            // A small warm-up only: everything else streams in over the next few frames inside
            // the frame budget, so the very first frame does not do the work of four.
            ProcessQueue(Mathf.Max(1, settings.chunksPerFrame) * 2);
        }

        void OnDisable()
        {
            if (releaseOnDisable && Application.isPlaying) ClearAll();
        }

        void Update()
        {
            if (!streamInPlayMode) return;
            if (!Application.isPlaying && !buildInEditor) return;

            EnsureBuilt();
            ResolveTarget();

            Refresh(false);

            // Trees are the one thing that follows the player instead of the chunk grid: whatever
            // comes into view is grown on the spot (and nothing about it is remembered).
            GrowTreesInView();

            if (logStats)
            {
                _logTimer -= Time.deltaTime;
                if (_logTimer <= 0f)
                {
                    _logTimer = 5f;
                    Debug.Log("[MysticMap.World] chunks=" + _live.Count + " queued=" + _queue.Count +
                              " center=" + _center.x + "," + _center.y + " skipped(town)=" + _legacySkipped +
                              " trees=" + TreesLive + (TreePoolSize == 0 ? " (NO TREE PREFABS!)" : "") +
                              " fps=" + FrameStats.Fps.ToString("0"));
                }
            }
        }

        /// <summary>Trees currently standing in the streamed chunks.</summary>
        public int TreesLive => _generator != null && _generator.Trees != null ? _generator.Trees.TreesGrown : 0;

        /// <summary>Tree prefabs the forest can use (0 = palette and folder are both empty).</summary>
        public int TreePoolSize => _generator != null && _generator.Vegetation != null
            ? _generator.Vegetation.TreePoolCount
            : 0;

        /// <summary>
        /// How wide the streaming window should be right now, in chunks. The view radius plus,
        /// while the camera is being swung around quickly, a little extra so late pop-in cannot
        /// happen behind a fast turn. Checked on a timer rather than every frame, and it grows at
        /// once but only shrinks once the view has settled - otherwise a quick flick would keep
        /// adding and removing the same outer ring.
        /// </summary>
        int StreamingRadius()
        {
            int r = settings.viewRadiusChunks;
            if (!extraRadiusWhileTurning || r >= 12) return r;

            _radiusTimer -= Time.deltaTime;
            if (_radiusTimer > 0f) return _radiusNow;

            _radiusTimer = Mathf.Max(0.05f, radiusCheckInterval);

            bool turning = CameraMotion.IsTurning;
            bool settled = CameraMotion.Settled;

            if (turning) _radiusNow = Mathf.Min(r + 1, 12);
            else if (settled || _radiusNow < r) _radiusNow = r;

            return _radiusNow;
        }

        // =====================================================================
        //  Trees follow the player
        // =====================================================================
        /// <summary>
        /// Grows the trees of the chunks the player can see into, and gives back the trees of the
        /// chunks they have turned away from or left behind.
        ///
        /// This runs on a timer because walking and turning only reveal a few trees per moment. A
        /// chunk that is already in front is left alone, so swinging the camera never rebuilds the
        /// forest the player is looking at, and the number of trees stays the size of what is
        /// visible instead of growing with the distance walked. Nothing is stored anywhere.
        /// </summary>
        void GrowTreesInView()
        {
            if (!treesOnlyInPlayerView || !Application.isPlaying) return;
            if (_generator == null || _generator.Trees == null || target == null) return;
            if (_live.Count == 0) return;

            TreeGenerator trees = _generator.Trees;
            trees.NearChunks = treeFullDensityChunks;
            trees.KeepRadius = treeKeepRadius;
            trees.ViewAngle = treeViewAngle;

            _treeTimer -= Time.deltaTime;
            if (_treeTimer > 0f) return;
            _treeTimer = Mathf.Max(0.05f, treeRefreshInterval);

            int ring = treeFullDensityChunks;
            int budget = Mathf.Max(1, treeRebuildsPerTick);
            int done = 0;

            // ---- give back the trees of the chunks that left the view ----------
            if (_treeCheck >= _treeChecked.Count)
            {
                _treeChecked.Clear();
                _treeChecked.AddRange(_live.Keys);
                _treeCheck = 0;
            }

            while (_treeCheck < _treeChecked.Count && done < budget)
            {
                Vector2Int key = _treeChecked[_treeCheck++];
                if (!_live.TryGetValue(key, out WorldChunk old) || old == null || old.PropCount == 0) continue;
                if (!IsOutOfSight(key, ring)) continue;

                int removed = old.TreeCount;
                old.ClearTreeProps();
                trees.NoteRemoved(removed);
                done++;
            }

            // ---- grow the nearest chunks that are in view -----------------------
            // The chunks the player stands in are dressed first (full density, no cone test), so a
            // grove is always there right where you are; after that the nearest one in front.
            int picked = int.MaxValue;
            for (int dz = -ring; dz <= ring && done < budget; dz++)
            {
                for (int dx = -ring; dx <= ring && done < budget; dx++)
                {
                    var coord = new Vector2Int(_center.x + dx, _center.y + dz);
                    if (!_live.TryGetValue(coord, out WorldChunk chunk) || chunk == null) continue;
                    if (chunk.PropCount != 0) continue;
                    if (!chunk.NeedsTrees) continue;

                    int cost = dx * dx + dz * dz;
                    if (cost >= picked) continue;

                    // The chunk the player stands in is always dressed; anything further has to be
                    // inside the view cone (or the keep radius) to be worth the trees.
                    bool standingIn = cost == 0;
                    if (!standingIn && !_generator.CanSee(chunk)) continue;

                    int before = chunk.PropCount;
                    picked = cost;
                    if (!_generator.PopulateTrees(chunk)) continue;

                    trees.NoteChunk(chunk.PropCount - before);
                    done++;
                }
            }
        }

        /// <summary>True when a chunk holds no tree the player can see any more.</summary>
        bool IsOutOfSight(Vector2Int coord, int ring)
        {
            Vector3 p = target.position;
            float size = settings.chunkSize;
            float x0 = coord.x * size, z0 = coord.y * size;

            // Overlap of the chunk with the always-kept circle around the player.
            float nx = Mathf.Clamp(p.x, x0, x0 + size);
            float nz = Mathf.Clamp(p.z, z0, z0 + size);
            float ndx = nx - p.x, ndz = nz - p.z;
            if (ndx * ndx + ndz * ndz <= treeKeepRadius * treeKeepRadius) return false;

            int dxc = coord.x - _center.x;
            int dzc = coord.y - _center.y;
            if (Mathf.Max(Mathf.Abs(dxc), Mathf.Abs(dzc)) <= ring) return false;
            if (Mathf.Sqrt(dxc * dxc + dzc * dzc) > settings.viewRadiusChunks + 0.35f) return true;

            return !_generator.CanSee(new Vector3(x0 + size * 0.5f, 0f, z0 + size * 0.5f), size * 0.5f);
        }

        public void ResetTreeStats()
        {
            if (_generator != null && _generator.Trees != null) _generator.Trees.ResetStats();
        }

        /// <summary>Creates the generator (safe to call from the editor tools too).</summary>
        public void EnsureBuilt()
        {
            if (_built && _generator != null && _generator.Settings == settings) return;

            if (settings == null) settings = new WorldSettings();
            settings.Validate();

            if (townAnchor == null) townAnchor = GetComponent<TownAnchor>();
            if (townAnchor == null) townAnchor = gameObject.AddComponent<TownAnchor>();
            townAnchor.EnsureReady();

            _generator = new WorldGenerator(settings, townAnchor);

            _built = true;

            SyncTreeObserver();
        }

        /// <summary>
        /// Hands the player (and the camera) to the tree pass. The trees read them live, so this is
        /// only a pointer refresh - turning this off or on never needs the chunks to be rebuilt.
        /// </summary>
        void SyncTreeObserver()
        {
            if (_generator == null || _generator.Trees == null) return;

            Transform player = target;
            if (player == null && Application.isPlaying)
            {
                var found = FindObjectsByType<SlimePlayer>();
                if (found != null && found.Length > 0) player = found[0].transform;
            }

            Transform view = Camera.main != null ? Camera.main.transform : null;

            bool aroundPlayer = treesOnlyInPlayerView && Application.isPlaying;
            _generator.SetObserver(player, view, treesOnlyInPlayerView);

            // While the trees follow the player, a chunk is built with its ground cover only - the
            // trees are grown later, the moment the player can see that chunk.
            if (_generator.Vegetation != null) _generator.Vegetation.TreesHandledByStreamer = aroundPlayer;
        }

        /// <summary>
        /// Container for the streamed chunks. Created on demand so loading the scene in the
        /// editor never adds an empty object to it.
        /// </summary>
        Transform Root
        {
            get
            {
                if (_root == null)
                {
                    var go = new GameObject("Chunks");
                    go.transform.SetParent(transform, false);
                    if (!Application.isPlaying) go.hideFlags = HideFlags.DontSave;
                    _root = go.transform;
                }
                return _root;
            }
        }

        void ResolveTarget()
        {
            if (target != null || !autoFindPlayer) return;

            var player = FindObjectsByType<SlimePlayer>();
            if (player != null && player.Length > 0)
            {
                target = player[0].transform;
                SyncTreeObserver();
                return;
            }

            var cam = Camera.main;
            if (cam != null) target = cam.transform;
        }

        // =====================================================================
        //  Streaming
        // =====================================================================
        /// <summary>Recomputes what should exist around the target.</summary>
        public void Refresh(bool force)
        {
            if (_generator == null) return;

            if (target == null)
            {
                ProcessQueue(settings.chunksPerFrame);
                return;
            }

            Vector2Int center = _generator.ChunkOf(target.position);
            int radius = StreamingRadius();

            // Rebuilding the queue is only needed when the player changed chunk (or the window
            // grew/shrunk - but see AddRing: a growing window only ever ADDS chunks, so the queue is
            // never rebuilt for nothing).
            if (!force && center == _center && radius == _centerRadius) return;

            bool sameCenter = center == _center && _centerRadius >= 0;

            if (sameCenter && radius > _centerRadius)
            {
                // The camera was swung around (or the window grew): add only the new outer ring. The
                // chunks already waiting keep their place, so the nearest one is still created first.
                AddRing(center, radius);
            }
            else
            {
                _queue.Clear();
                _legacySkipped = 0;
                BuildQueue(center, radius, true);
            }

            _center = center;
            _centerRadius = radius;

            UpdateLiveChunks();
            ProcessQueue(settings.chunksPerFrame);
        }

        /// <summary>
        /// Adds just the chunks of the outer ring <paramref name="radius"/> that are not known yet.
        /// Used when the streaming window only grew; it keeps the existing queue (nearest first) intact
        /// instead of throwing it away and sorting it again.
        /// </summary>
        void AddRing(Vector2Int center, int radius)
        {
            float inner = radius - 1f;

            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    float dsq = dx * dx + dz * dz;
                    if (dsq > (radius + 0.35f) * (radius + 0.35f)) continue;
                    if (dsq <= inner * inner) continue;                  // already in the window below

                    var coord = new Vector2Int(center.x + dx, center.y + dz);
                    if (_live.ContainsKey(coord) || _queue.Contains(coord)) continue;
                    if (_generator.IsLegacy(coord)) { _legacySkipped++; continue; }

                    _queue.Add(coord);
                    _queueCost.Add(Mathf.Sqrt(dsq));
                }
            }

            SortByCost();
        }

        /// <summary>Nearest first, without allocating (insertion sort - the list is almost sorted).</summary>
        void SortByCost()
        {
            for (int i = 1; i < _queue.Count; i++)
            {
                Vector2Int coord = _queue[i];
                float cost = _queueCost[i];
                int j = i - 1;
                while (j >= 0 && _queueCost[j] > cost)
                {
                    _queue[j + 1] = _queue[j];
                    _queueCost[j + 1] = _queueCost[j];
                    j--;
                }
                _queue[j + 1] = coord;
                _queueCost[j + 1] = cost;
            }
        }

        int _centerRadius = -1;

        /// <summary>
        /// Fills the queue with the chunks that should exist but do not yet, sorted nearest
        /// first. When the window only grew (a fast turn, detail switched on) the chunks already
        /// waiting simply stay in their slots and keep their cost, so the queue is not rebuilt
        /// from scratch for nothing.
        /// </summary>
        void BuildQueue(Vector2Int center, int radius, bool keepExisting)
        {
            _queue.Clear();
            _queueCost.Clear();

            int r = radius;
            float rSq = (r + 0.35f) * (r + 0.35f);

            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    float dsq = dx * dx + dz * dz;
                    if (dsq > rSq) continue;

                    var coord = new Vector2Int(center.x + dx, center.y + dz);
                    if (_live.ContainsKey(coord)) continue;
                    if (_generator.IsLegacy(coord)) { _legacySkipped++; continue; }

                    _queue.Add(coord);
                    _queueCost.Add(Mathf.Sqrt(dsq));
                }
            }

            if (!keepExisting || _queue.Count < 2) return;

            // Nearest first: the list is almost sorted (ring by ring), so this stays cheap.
            SortByCost();
        }

        /// <summary>
        /// Creates chunks from the queue - but only while there is time left in this frame.
        /// The budget is what turns a 200 ms hitch into four 50 ms frames.
        /// </summary>
        public void ProcessQueue(int budget)
        {
            if (_generator == null) return;
            if (_queue.Count == 0) return;

            // On a machine that is already behind, doing work every frame makes it worse. Do one
            // chunk every other frame instead, so the world streams in at a pace the GPU can follow.
            if (Application.isPlaying)
            {
                float frame = FrameStats.SmoothedFrame;
                float target = FrameStats.TargetFrameTime;

                if (frame > target * 1.6f)
                {
                    _slowSkip ^= 1;
                    if (_slowSkip == 0) return;
                }
                else
                {
                    _slowSkip = 0;
                }
            }

            int cap = Mathf.Clamp(Mathf.Min(budget, maxChunksPerFrame), 1, 8);
            int done = 0;

            while (done < cap && _queue.Count > 0)
            {
                Vector2Int coord = _queue[0];
                _queue.RemoveAt(0);
                _queueCost.RemoveAt(0);

                if (_live.ContainsKey(coord)) continue;
                if (_generator.IsLegacy(coord)) continue;

                CreateChunk(coord);
                done++;

                // Enough of this frame spent? Stop here; the rest waits for the next frame.
                if (Application.isPlaying && done >= 1 && done < cap &&
                    FrameStats.TimeSpentThisFrame > FrameStats.TargetFrameTime * Mathf.Clamp01(frameBudget))
                    break;
            }

            if (done > 0) ReleaseFarChunks();
        }

        WorldChunk CreateChunk(Vector2Int coord)
        {
            float dist = ChunkDistance(coord, _center);
            bool highLod = dist <= settings.lodDistanceChunks;

            // Every chunk gets its trees/rocks/grass, whether it is near or far: the props are the
            // world, and they are released together with the chunk (ReleaseFarChunks) once it is
            // behind the player. Only populating the "detail" ring used to leave bare ground further
            // out - exactly what a player sees as "no trees once I walk away from the spawn".
            WorldChunk chunk = _generator.CreateChunk(Root, coord, highLod, true, 1f);
            if (chunk == null) return null;

            if (!Application.isPlaying) chunk.gameObject.hideFlags = HideFlags.DontSave;

            _live[coord] = chunk;
            return chunk;
        }

        /// <summary>
        /// Keeps the LOD and the prop detail of existing chunks in sync with the player.
        ///
        /// This used to rebuild the terrain mesh of every chunk that crossed a LOD line in a
        /// single frame - several chunks, each with a fresh mesh and a re-baked mesh collider.
        /// That is the classic "walk a few metres and the camera jerks once" hitch. Now the
        /// needed changes are only <i>collected</i> here; <see cref="ApplyLodJobs"/> spends a
        /// single chunk's worth of them from the frame budget.
        /// </summary>
        void UpdateLiveChunks()
        {
            _rebuild.Clear();
            _rebuildDetail.Clear();

            foreach (var pair in _live)
            {
                WorldChunk chunk = pair.Value;
                if (chunk == null) continue;

                float dist = ChunkDistance(pair.Key, _center);
                bool highLod = dist <= settings.lodDistanceChunks;

                // Props live on every streamed chunk and are released with it (ReleaseFarChunks).
                if (chunk.highLod != highLod)
                {
                    _rebuild.Add(pair.Key);
                    _rebuildDetail.Add(true);
                }

                // Whatever happens above, a chunk is never left empty: if its LOD job is dropped (the
                // LOD flipped back before it ran) the props are still grown here.
                if (chunk.PropCount == 0)
                    _generator.Populate(chunk, _generator.BuildChunkField(chunk), 1f);
            }

            ApplyLodJobs();
        }

        /// <summary>Applies at most one queued LOD rebuild per frame (and only if there is time).</summary>
        void ApplyLodJobs()
        {
            if (_rebuild.Count == 0) return;

            if (Application.isPlaying &&
                FrameStats.TimeSpentThisFrame > FrameStats.TargetFrameTime * Mathf.Clamp01(frameBudget) * 0.5f)
                return;

            for (int i = 0; i < _rebuild.Count; i++)
            {
                if (!_live.TryGetValue(_rebuild[i], out WorldChunk chunk) || chunk == null) continue;

                bool highLod = ChunkDistance(_rebuild[i], _center) <= settings.lodDistanceChunks;
                if (chunk.highLod == highLod) continue;

                chunk.ClearProps();
                RoadField field = _generator.BuildTerrain(chunk, highLod);
                if (_rebuildDetail[i]) _generator.Populate(chunk, field, 1f);
                break;   // one per frame keeps the budget honest
            }

            _rebuild.Clear();
            _rebuildDetail.Clear();
        }

        /// <summary>Destroys every chunk that is not wanted any more.</summary>
        public void ReleaseFarChunks()
        {
            _stale.Clear();
            // The live radius may be one chunk wider than the settings radius (see
            // StreamingRadius), so unload relative to that - otherwise the extra ring would be
            // created and deleted over and over.
            int live = Mathf.Max(settings.viewRadiusChunks, _centerRadius);
            int limit = live + settings.unloadMarginChunks + 1;

            foreach (var pair in _live)
                if (ChunkDistance(pair.Key, _center) > limit) _stale.Add(pair.Key);

            for (int i = 0; i < _stale.Count; i++) DestroyChunk(_stale[i]);
        }

        void DestroyChunk(Vector2Int coord)
        {
            if (!_live.TryGetValue(coord, out WorldChunk chunk)) return;
            _live.Remove(coord);
            if (chunk == null) return;

            chunk.Release();
            WorldSpawn.Destroy(chunk.gameObject);
        }

        /// <summary>Removes every streamed chunk (the hand-built town stays exactly as it is).</summary>
        public void ClearAll()
        {
            _stale.Clear();
            foreach (var pair in _live) _stale.Add(pair.Key);
            for (int i = 0; i < _stale.Count; i++) DestroyChunk(_stale[i]);
            _queue.Clear();
            _queueCost.Clear();
            _rebuild.Clear();
            _rebuildDetail.Clear();
            _center = new Vector2Int(int.MinValue, int.MinValue);
            _centerRadius = -1;
            _treeChecked.Clear();
            _treeCheck = 0;
            _treeTimer = 0f;
        }

        // =====================================================================
        //  Queries used by the player and the tools
        // =====================================================================
        /// <summary>Ground height anywhere in the world (town, live chunk or procedural field).</summary>
        public float SampleHeight(float x, float z)
        {
            EnsureBuilt();

            // The town area is drawn by the hand-built terrain, so that is the truth there.
            if (_generator.Town != null && _generator.Town.InsideLegacy(x, z) &&
                _generator.Town.legacyTerrain != null)
                return _generator.Town.legacyTerrain.SampleHeight(new Vector3(x, 0f, z));

            var coord = _generator.ChunkOf(new Vector3(x, 0f, z));
            if (_live.TryGetValue(coord, out WorldChunk chunk) && chunk != null && chunk.Data != null &&
                chunk.Data.mesh != null)
                return _generator.SampleHeight(x, z, chunk);

            return _generator.SampleHeight(x, z, null);
        }

        /// <summary>Puts a transform on the ground (used when the world is created at runtime).</summary>
        public void PlaceOnGround(Transform t, float extraHeight = 0.2f)
        {
            if (t == null) return;
            EnsureBuilt();

            float h = SampleHeight(t.position.x, t.position.z) + extraHeight;
            var player = t.GetComponent<SlimePlayer>();
            if (player != null) player.Teleport(new Vector3(t.position.x, h, t.position.z));
            else t.position = new Vector3(t.position.x, h, t.position.z);
        }

        /// <summary>Distance between two chunk coordinates, in chunks.</summary>
        static float ChunkDistance(Vector2Int a, Vector2Int b)
        {
            float dx = a.x - b.x, dz = a.y - b.y;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // =====================================================================
        //  Editor preview / tools
        // =====================================================================
        /// <summary>
        /// Builds the world around a position immediately (used by the editor preview so the
        /// world can be inspected without pressing Play). Safe to call from a menu item.
        /// </summary>
        public void BuildAround(Vector3 position, bool detail)
        {
            EnsureBuilt();
            ClearAll();

            // An editor preview has no player looking at it: grow the whole forest so the country can
            // be judged without pressing Play. The live streamer asks for view-only trees.
            SyncTreeObserver();
            if (_generator.Vegetation != null) _generator.Vegetation.TreesHandledByStreamer = false;
            _generator.SetObserver(null, null, false);

            _center = _generator.ChunkOf(position);
            int r = settings.viewRadiusChunks;
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Mathf.Sqrt(dx * dx + dz * dz) > r + 0.35f) continue;

                    var coord = new Vector2Int(_center.x + dx, _center.y + dz);
                    if (_generator.IsLegacy(coord)) { _legacySkipped++; continue; }

                    float dist = ChunkDistance(coord, _center);
                    bool highLod = dist <= settings.lodDistanceChunks;
                    bool pop = detail && dist <= settings.detailRadiusChunks;
                    CreateChunkAt(coord, highLod, pop);
                }
            }

            // A preview has to be seen: the scene view only redraws objects in a dirty scene.
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
        }

        WorldChunk CreateChunkAt(Vector2Int coord, bool highLod, bool detail)
        {
            WorldChunk chunk = _generator.CreateChunk(Root, coord, highLod, detail, 1f);
            if (chunk == null) return null;

            // Editor previews are never saved into the scene (they are regenerated on demand).
            if (!Application.isPlaying) chunk.gameObject.hideFlags = HideFlags.DontSave;
            _live[coord] = chunk;
            return chunk;
        }

        void OnDrawGizmosSelected()
        {
            if (!drawGizmos || settings == null) return;

            float s = Mathf.Max(10f, settings.chunkSize);
            int r = settings.viewRadiusChunks;

            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.35f);
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Mathf.Sqrt(dx * dx + dz * dz) > r + 0.35f) continue;
                    var center = new Vector3((_center.x + dx + 0.5f) * s, 0f, (_center.y + dz + 0.5f) * s);
                    center.y = SampleHeightSafe(center.x, center.z);
                    Gizmos.DrawWireCube(center, new Vector3(s, 1f, s));
                }
            }

            var town = townAnchor != null ? townAnchor : GetComponent<TownAnchor>();
            if (town != null)
            {
                town.EnsureReady();
                Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.8f);
                Vector2 o = town.Origin, size = town.Size;
                for (float x = o.x; x <= o.x + size.x + 0.1f; x += s)
                    Gizmos.DrawLine(new Vector3(x, 45f, o.y), new Vector3(x, 45f, o.y + size.y));
                for (float z = o.y; z <= o.y + size.y + 0.1f; z += s)
                    Gizmos.DrawLine(new Vector3(o.x, 45f, z), new Vector3(o.x + size.x, 45f, z));
            }
        }

        float SampleHeightSafe(float x, float z)
        {
            if (_generator == null) return 45f;
            return Mathf.Clamp(SampleHeight(x, z), 1f, settings.maxHeight);
        }
    }
}
