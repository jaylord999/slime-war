using UnityEngine;

namespace MysticMap.World
{
    /// <summary>
    /// Keeps the frame clock running and logs the frames that hitch.
    ///
    /// Two jobs, both cheap:
    ///   1. Calls <see cref="FrameStats.Tick"/> at the very start of the frame, before any
    ///      script that streams or generates, so the frame budget the streaming code reads is
    ///      accurate. (It has an explicit script order, but the class also self-heals if this
    ///      component is missing.)
    ///   2. Watches for frames that take far longer than the rest and prints one line saying how
    ///      long they took. That is how you find out <i>when</i> the game stutters without
    ///      attaching a profiler - useful while tuning the streaming settings.
    ///
    /// It is created automatically in play mode by <see cref="ChunkManager"/> (and it is harmless
    /// to add it by hand).
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    [DisallowMultipleComponent]
    [AddComponentMenu("MysticMap/World/Stutter Monitor")]
    public class StutterMonitor : MonoBehaviour
    {
        [Tooltip("Print a line whenever a frame is much longer than the smoothed frame time.")]
        public bool reportStutters = true;

        [Tooltip("A frame is reported when it takes at least this many seconds.")]
        public float minReportedSeconds = 0.05f;

        [Tooltip("...and is at least this many times longer than the smoothed frame time.")]
        public float spikeMultiplier = 2.5f;

        [Tooltip("Minimum seconds between two log lines, so one bad moment cannot flood the console.")]
        public float logCooldown = 1.5f;

        [Tooltip("Seconds between FPS lines in the console (0 = off).")]
        public float fpsLogInterval = 0f;

        float _nextLog;
        float _nextFps;

        void OnEnable()
        {
            FrameStats.Reset();
        }

        void Update()
        {
            FrameStats.Tick();

            CameraMotion.Sample();

            float dt = FrameStats.LastFrame;
            float smoothed = FrameStats.SmoothedFrame;

            if (reportStutters && dt >= minReportedSeconds && dt > smoothed * spikeMultiplier &&
                Time.unscaledTime >= _nextLog)
            {
                _nextLog = Time.unscaledTime + Mathf.Max(0.1f, logCooldown);
                Debug.Log("[MysticMap] Frame hitch: " + (dt * 1000f).ToString("0") + " ms (" +
                          FrameStats.Fps.ToString("0") + " fps average).", this);
            }

            if (fpsLogInterval > 0f && Time.unscaledTime >= _nextFps)
            {
                _nextFps = Time.unscaledTime + fpsLogInterval;
                Debug.Log("[MysticMap] " + FrameStats.Fps.ToString("0") + " fps", this);
            }
        }

        /// <summary>Adds the monitor to the scene once (called by ChunkManager in play mode).</summary>
        public static void EnsureExists()
        {
            if (!Application.isPlaying) return;
            if (FindAnyObjectByType<StutterMonitor>() != null) return;

            var go = new GameObject("~MysticMap Frame Monitor");
            go.AddComponent<StutterMonitor>();
            DontDestroyOnLoad(go);
        }
    }
}
