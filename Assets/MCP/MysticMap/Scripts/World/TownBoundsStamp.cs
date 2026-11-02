using UnityEngine;

namespace MysticMap.World
{
    /// <summary>
    /// Carries the town's real size in the scene and hands it to <see cref="TownBounds"/> when
    /// the game starts.
    ///
    /// <see cref="TownBounds"/> is a static, so it is wiped every time you enter Play mode; this
    /// tiny component (created and filled by the editor with "Capture town design" / the town
    /// build) is what puts the numbers back. Without it, runtime scripts would fall back to their
    /// defaults - which is exactly how roadside props ended up spawning all over the grown town.
    /// </summary>
    [DisallowMultipleComponent]
    public class TownBoundsStamp : MonoBehaviour
    {
        [Tooltip("Middle of the town (world metres).")]
        public Vector2 center = new Vector2(500f, 520f);

        [Tooltip("Half width of the town, centre to the east / west wall (m).")]
        public float halfX = 0f;

        [Tooltip("Half depth of the town, centre to the north / south wall (m).")]
        public float halfZ = 0f;

        void Awake() { Apply(); }

        void OnEnable() { Apply(); }

        /// <summary>Publishes the stamped size to <see cref="TownBounds"/>.</summary>
        public void Apply()
        {
            if (halfX > 1f && halfZ > 1f) TownBounds.Set(center, halfX, halfZ);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.55f, 0.2f, 0.9f);
            Gizmos.DrawWireCube(new Vector3(center.x, 45f, center.y),
                                new Vector3(halfX * 2f, 2f, halfZ * 2f));
        }
    }
}
