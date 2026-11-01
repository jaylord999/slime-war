using UnityEngine;

namespace MysticMap.World
{
    /// <summary>
    /// A tiny, dependency-free frame clock that anything doing heavy work can consult.
    ///
    /// Streaming code needs to answer one question every frame: "is there still room in this
    /// frame to generate another chunk?". Unity gives <see cref="Time.deltaTime"/> (the whole
    /// frame) and <see cref="Time.unscaledDeltaTime"/>, but neither tells you how much time has
    /// already been spent <b>this</b> frame. <see cref="Time.realtimeSinceStartup"/> does: it is
    /// read once when a new frame starts (see <see cref="Tick"/>), so anything generated later on
    /// in the same frame knows exactly how much budget is left.
    ///
    /// It also smooths the frame time with an exponential average, which is what the streaming
    /// manager uses to avoid adding more work to a machine that is already struggling. That is
    /// what keeps a weak PC from stuttering: the world still streams in, just a little more
    /// gently while frames are slow.
    ///
    /// <see cref="StutterMonitor"/> calls <see cref="Tick"/> from a script ordered before
    /// everything else; if nothing calls it, every read does
    /// <see cref="Tick"/> itself, so the class is always correct - just slightly less useful.
    /// </summary>
    public static class FrameStats
    {
        /// <summary>Length of the previous frame in seconds (unscaled).</summary>
        public static float LastFrame { get; private set; } = 0.016f;

        /// <summary>Smoothed frame time in seconds (rises fast, decays slowly).</summary>
        public static float SmoothedFrame { get; private set; } = 0.016f;

        /// <summary>Frames per second of the smoothed frame time.</summary>
        public static float Fps => SmoothedFrame > 0.00001f ? 1f / SmoothedFrame : 0f;

        /// <summary>
        /// Best guess at how long one frame <i>should</i> take (1 s / refresh rate), in seconds.
        /// This is the budget a heavy system should leave room in.
        /// </summary>
        public static float TargetFrameTime
        {
            get
            {
                double hz = Screen.currentResolution.refreshRateRatio.value;
                if (hz < 30.0 || hz > 500.0) hz = 60.0;
                return (float)(1.0 / hz);
            }
        }

        static int _frame = -1;
        static float _frameStart;

        /// <summary>Reads the frame clock. Cheap; call it at the very start of the frame.</summary>
        public static void Tick()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;

            float now = Time.realtimeSinceStartup;
            if (_frameStart <= 0f)
            {
                _frameStart = now;
                LastFrame = Mathf.Max(0.0001f, Time.unscaledDeltaTime);
                SmoothedFrame = LastFrame;
                return;
            }

            LastFrame = Mathf.Clamp(now - _frameStart, 0.0001f, 0.5f);
            _frameStart = now;

            // A spike is picked up immediately (so the streamer backs off at once) and decays
            // gently - going up fast / down slow is the classic "budget" filter.
            float rise = 0.35f, fall = 0.04f;
            float k = LastFrame > SmoothedFrame ? rise : fall;
            SmoothedFrame = Mathf.Lerp(SmoothedFrame, LastFrame, k);
        }

        /// <summary>Seconds already spent in the current frame (excluding a bit of slop).</summary>
        public static float TimeSpentThisFrame
        {
            get
            {
                Tick();
                return Mathf.Max(0f, Time.realtimeSinceStartup - _frameStart);
            }
        }

        /// <summary>Seconds left of a frame with this target duration (never negative).</summary>
        public static float Remaining(float targetFrameTime)
        {
            return Mathf.Max(0f, targetFrameTime - TimeSpentThisFrame);
        }

        /// <summary>Resets the clock (used when leaving play mode so the first frame is clean).</summary>
        public static void Reset()
        {
            _frame = -1;
            _frameStart = 0f;
            LastFrame = 0.016f;
            SmoothedFrame = 0.016f;
        }
    }
}
