using System.Collections.Generic;
using UnityEngine;

namespace MysticMap.World
{
    /// <summary>Spawns and tears down the content of a chunk.</summary>
    public static class WorldSpawn
    {
        /// <summary>Instantiate a prefab under a parent with a yaw and a uniform scale.</summary>
        public static GameObject Spawn(GameObject prefab, Transform parent, Vector3 position,
                                       float yaw, float scale)
        {
            if (prefab == null || parent == null) return null;

            GameObject go = Object.Instantiate(prefab, position, Quaternion.Euler(0f, yaw, 0f), parent);
            if (go == null) return null;

            if (!Mathf.Approximately(scale, 1f))
                go.transform.localScale = go.transform.localScale * scale;

            return go;
        }

        public static void Destroy(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }

    /// <summary>
    /// One streamed piece of the endless world: its terrain mesh, its collider and every
    /// tree/rock/ruin that belongs to it. Destroying the object releases all of it.
    /// </summary>
    [DisallowMultipleComponent]
    public class WorldChunk : MonoBehaviour
    {
        public Vector2Int coord;
        public bool highLod;

        MeshFilter _filter;
        MeshRenderer _renderer;
        MeshCollider _collider;
        Transform _propsRoot;

        public TerrainGenerator.ChunkMesh Data { get; private set; }

        public int PropCount { get; private set; }

        public void Setup(Vector2Int chunkCoord, TerrainGenerator.ChunkMesh data, Material[] materials,
                          bool highLodMesh, bool withCollider)
        {
            coord = chunkCoord;
            Data = data;
            highLod = highLodMesh;

            if (_filter == null) _filter = gameObject.AddComponent<MeshFilter>();
            if (_renderer == null) _renderer = gameObject.AddComponent<MeshRenderer>();

            _filter.sharedMesh = data.mesh;
            _renderer.sharedMaterials = materials;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            _renderer.receiveShadows = true;

            if (withCollider)
            {
                bool needsBake = _collider == null || _collider.sharedMesh == null;
                if (_collider == null) _collider = gameObject.AddComponent<MeshCollider>();

                // Only bake a new collider the FIRST time. A later LOD switch swaps the visual
                // mesh; re-baking here would re-cook a 35x35 grid (a very expensive operation)
                // in the middle of a frame while the player is walking over it. The old collider
                // already follows the same height field, so it stays.
                if (needsBake)
                {
                    _collider.sharedMesh = null;              // force a fresh bake
                    _collider.sharedMesh = data.mesh;
                }
                _collider.enabled = true;
            }
            else if (_collider != null)
            {
                _collider.enabled = false;
            }

            if (_propsRoot == null)
            {
                var go = new GameObject("Props");
                go.transform.SetParent(transform, false);
                _propsRoot = go.transform;
            }

            // Let the slime's magic find this chunk's grass, trees and rocks (see MagicEnvironment).
            MagicEnvironment.Register(_propsRoot, MagicEnvMode.Destroy, 160f);
        }

        public Transform PropsRoot
        {
            get
            {
                if (_propsRoot == null)
                {
                    var go = new GameObject("Props");
                    go.transform.SetParent(transform, false);
                    _propsRoot = go.transform;
                }
                return _propsRoot;
            }
        }

        public void AddProp() => PropCount++;

        /// <summary>World height of this chunk's mesh at a local position.</summary>
        public float HeightAtLocal(float localX, float localZ) =>
            Data != null ? Data.SampleHeight(localX, localZ) : 0f;

        /// <summary>Removes the props only (keeps the terrain, used when the detail LOD changes).</summary>
        public void ClearProps()
        {
            if (_propsRoot == null) return;
            for (int i = _propsRoot.childCount - 1; i >= 0; i--)
                WorldSpawn.Destroy(_propsRoot.GetChild(i).gameObject);
            PropCount = 0;
            _treeCount = 0;
            _hasTrees = true;
        }

        // ---- trees ------------------------------------------------------------
        // The trees of a chunk are grown around the player (see TreeGenerator / ChunkManager), so a
        // chunk remembers only how many of its props are trees - never where they are.
        bool _hasTrees;
        int _treeCount;

        /// <summary>Counts the trees the chunk was just given (called by the tree pass).</summary>
        public void SetTreeCount(int count)
        {
            _treeCount = Mathf.Max(0, count);
            _hasTrees = _treeCount > 0;
            if (_hasTrees) MagicEnvironment.Register(_propsRoot, MagicEnvMode.Destroy, 160f);
        }

        /// <summary>True when the chunk has never been dressed with trees in this session.</summary>
        public bool NeedsTrees => !_hasTrees;

        /// <summary>How many of this chunk's props are trees.</summary>
        public int TreeCount => _treeCount;

        /// <summary>
        /// Gives the trees of this chunk back (the grass, rocks and the terrain stay). Used when the
        /// player turns away from the chunk or walks off: the forest is not stored, so it can simply
        /// be dropped and grown again when it comes back into view.
        /// </summary>
        public void ClearTreeProps()
        {
            if (_propsRoot == null || _treeCount <= 0) return;

            var doomed = new List<GameObject>(_treeCount);
            for (int i = 0; i < _propsRoot.childCount; i++)
            {
                Transform child = _propsRoot.GetChild(i);
                const string Prefix = "Tree_";
                if (child != null && child.name.StartsWith(Prefix, System.StringComparison.Ordinal))
                    doomed.Add(child.gameObject);
            }

            for (int i = 0; i < doomed.Count; i++) WorldSpawn.Destroy(doomed[i]);

            PropCount = Mathf.Max(0, PropCount - doomed.Count);
            _treeCount = 0;
            _hasTrees = true;
        }

        public void Release()
        {
            MagicEnvironment.Unregister(_propsRoot);

            if (_filter != null) _filter.sharedMesh = null;
            if (_renderer != null) _renderer.sharedMaterials = new Material[0];
            if (_collider != null) _collider.sharedMesh = null;
            if (Data != null && Data.mesh != null) WorldSpawn.Destroy(Data.mesh);
            Data = null;
        }

        void OnDestroy() => Release();
    }
}
