using UnityEngine;

namespace MysticMap
{
    /// <summary>
    /// One shared answer to "is the camera moving right now?".
    ///
    /// Everything that streams the world around the player has the same problem: whoever is
    /// visible was decided when the frame was drawn, a frame or two before the streamer's
    /// <c>Update</c> runs. If the camera is swinging around at 400 degrees a second, the frames
    /// it has been heading towards are ones the streamer never prepared - so props and glows
    /// "pop" a moment late, which reads as the camera being laggy even when the framerate is
    /// fine.
    ///
    /// So while the camera is moving fast the streamers switch to a slightly wider, extra-safe
    /// window (see <see cref="LookAheadFactor"/>) and go back to their normal, cheap window the
    /// moment it settles. This class only measures; nothing here costs anything per frame.
    ///
    /// The camera already binds itself with <see cref="Bind"/> (see SlimePlayer); if nothing has
    /// bound, a Camera.main lookup is tried now and then.
    /// </summary>
    public static class CameraMotion
    {
        static Transform _cam;
        static Quaternion _lastRotation = Quaternion.identity;
        static Vector3 _lastPosition;
        static Vector3 _velocity;
        static bool _hasSample;
        static float _turnRate;      // degrees per second, smoothed
        static float _speed;         // metres per second, smoothed
        static float _nextLookup;

        /// <summary>Degrees per second the camera is currently turning (smoothed).</summary>
        public static float TurnRate => _turnRate;

        /// <summary>Metres per second the camera is currently moving (smoothed).</summary>
        public static float Speed => _speed;

        /// <summary>World-space velocity of the camera (smoothed), for look-ahead culling.</summary>
        public static Vector3 Velocity => _velocity;

        /// <summary>True while the camera is turning or travelling quickly.</summary>
        public static bool Moving => _turnRate > 25f || _speed > 3f;

        /// <summary>The camera being tracked, or null.</summary>
        public static Transform CameraTransform => _cam;

        /// <summary>Tells the tracker which transform to watch (SlimePlayer does this on startup).</summary>
        public static void Bind(Transform cameraTransform)
        {
            if (cameraTransform == _cam) return;
            _cam = cameraTransform;
            _hasSample = false;
            _turnRate = 0f;
            _speed = 0f;
            _velocity = Vector3.zero;
        }

        /// <summary>Reads the current camera pose and updates the smoothed rates. Once per frame.</summary>
        public static void Sample()
        {
            if (_cam == null)
            {
                if (Time.unscaledTime < _nextLookup) return;
                _nextLookup = Time.unscaledTime + 1f;

                var c = Camera.main;
                if (c != null) Bind(c.transform);
                if (_cam == null) return;
            }

            Quaternion rot = _cam.rotation;
            Vector3 pos = _cam.position;

            if (!_hasSample)
            {
                _hasSample = true;
                _lastRotation = rot;
                _lastPosition = pos;
                return;
            }

            float dt = Mathf.Max(0.0001f, Time.unscaledDeltaTime);

            float angle = Quaternion.Angle(_lastRotation, rot) / dt;
            Vector3 delta = pos - _lastPosition;
            Vector3 instantVelocity = delta / dt;

            _lastRotation = rot;
            _lastPosition = pos;

            // Rise fast (react at once), decay reasonably quickly (so a stopped view counts as
            // settled within a few frames) - the settle flags below do the anti-flicker part.
            float k = 1f - Mathf.Exp(-(instantVelocity.magnitude > _speed ? 12f : 8f) * dt);
            _speed = Mathf.Lerp(_speed, instantVelocity.magnitude, k);
            _velocity = Vector3.Lerp(_velocity, instantVelocity, k);
            _turnRate = Mathf.Lerp(_turnRate, angle, k);

            if (angle > 20f || instantVelocity.magnitude > 1f)
            {
                _settled = false;
                _lastMove = Time.unscaledTime;
            }
        }

        static float _lastMove;

        /// <summary>
        /// True while the view should get the wider streaming window: it is being turned or
        /// travelled quickly, and it has not recently settled.
        /// </summary>
        public static bool IsTurning => !Settled && (_turnRate > 25f || _speed > 3f);

        /// <summary>
        /// How much wider the streamers should spread their window for this frame: 1 while the
        /// view is calm, up to ~1.5 while it is being swung around quickly.
        /// </summary>
        public static float LookAheadFactor => Moving ? 1f + Mathf.Clamp01(_turnRate / 220f) * 0.5f : 1f;

        /// <summary>
        /// Called by the camera when the view has been still for a frame or two (see SlimePlayer).
        /// A later sample that shows real movement clears the flag again.
        /// </summary>
        public static void NotifySettled()
        {
            _settled = true;
            _turnRate = 0f;
            _speed = 0f;
            _velocity = Vector3.zero;
        }

        static bool _settled;

        /// <summary>True while the view is still/steady (used to shrink streaming windows back).</summary>
        public static bool Settled => _settled || (!Moving && Time.unscaledTime - _lastMove > 0.15f);

        /// <summary>Forgets the sample (leaving play mode / teleporting).</summary>
        public static void Reset()
        {
            _hasSample = false;
            _turnRate = 0f;
            _speed = 0f;
            _velocity = Vector3.zero;
            _settled = false;
            _lastMove = Time.unscaledTime;
        }
    }
}
