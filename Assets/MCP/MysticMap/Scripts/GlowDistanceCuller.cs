using UnityEngine;

namespace MysticMap
{
    /// <summary>
    /// Keeps the little glow sprites (the glow under grass/props) only within a short
    /// distance of the player, switching each prop's "GlowSprite" child off when it is
    /// too far away. Very cheap: it just toggles active state on a timer (the same
    /// trick PropStreamer uses), which removes far, unnoticeable glow draw calls.
    /// </summary>
    public class GlowDistanceCuller : MonoBehaviour
    {
        [Tooltip("Who to measure distance to (usually the player camera).")]
        public Transform target;
        [Tooltip("Glows further than this (m) from the player are hidden.")]
        public float radius = 25f;
        [Tooltip("Seconds between distance checks. Bigger = cheaper.")]
        public float checkInterval = 0.35f;

        [Tooltip("While the camera is being swung around, re-check every frame or two so glows never pop in late.")]
        public bool lookAhead = true;

        Transform[] _prop;
        Transform[] _glow;
        float _timer;
        float _sqr;
        int _count;
        bool _fullScanNow;

        void Start() { Collect(); }

        void OnEnable()
        {
            if (_glow == null || _glow.Length == 0) Collect();
        }

        /// <summary>
        /// Reads the children once. New props are picked up later without another allocation:
        /// <see cref="Update"/> grows the cache only when the child count changed.
        /// </summary>
        void Collect()
        {
            int n = transform.childCount;
            _count = n;
            if (_prop == null || _prop.Length < n)
            {
                _prop = new Transform[n];
                _glow = new Transform[n];
            }

            for (int i = 0; i < n; i++)
            {
                _prop[i] = transform.GetChild(i);
                _glow[i] = (_prop[i] != null) ? _prop[i].Find("GlowSprite") : null;
            }
            _sqr = radius * radius;
        }

        void Update()
        {
            int n = transform.childCount;
            if (n != _count)
            {
                Collect();
                _fullScanNow = true;      // rescan at once, a prop was just added
            }

            bool turning = lookAhead && CameraMotion.IsTurning;

            _timer -= Time.deltaTime;
            if (_timer > 0f && !_fullScanNow) return;
            _timer = turning ? Mathf.Min(checkInterval, 0.05f) : checkInterval;
            _fullScanNow = false;
            if (_prop == null || _prop.Length == 0) return;

            Transform t = target;
            if (t == null && Camera.main != null) t = Camera.main.transform;
            Vector3 cp = (t != null) ? t.position : transform.position;

            // Slightly wider while turning, so a glow at the edge of the view is already on
            // before it is actually seen.
            float sqr = turning ? _sqr * 1.3f : _sqr;

            for (int i = 0; i < _prop.Length; i++)
            {
                Transform par = _prop[i];
                if (par == null) continue;
                if (!par.gameObject.activeInHierarchy) continue;      // prop already culled (or behind the camera)
                Transform g = _glow[i];
                if (g == null) continue;                              // prop has no glow
                bool on = (par.position - cp).sqrMagnitude <= sqr;
                if (g.gameObject.activeSelf != on) g.gameObject.SetActive(on);
            }
        }
    }
}
