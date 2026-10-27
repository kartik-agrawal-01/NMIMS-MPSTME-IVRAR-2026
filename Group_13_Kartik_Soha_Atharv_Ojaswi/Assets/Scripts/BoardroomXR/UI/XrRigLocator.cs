using UnityEngine;

namespace BoardroomXR.UI
{
    /// <summary>
    /// Finds the pieces of the XR rig that the runtime-built expo furniture needs,
    /// without any scene wiring: the XR Origin, the head, the two controllers and
    /// each controller's aim transform.
    ///
    /// It also turns the measured room layout into world coordinates. Everything
    /// below is expressed the way the layout was measured - (forward, right, height)
    /// from the XR Origin - so the furniture lands in the right place even though
    /// the origin sits at (-1.169, 0, 0.841) with a 120 degree yaw.
    /// </summary>
    public static class XrRigLocator
    {
        // Fallbacks measured from SampleScene, used until the live rig is found.
        private static readonly Vector3 FallbackOrigin = new Vector3(-1.169f, 0f, 0.841f);
        private static readonly Vector3 FallbackForward = new Vector3(0.866f, 0f, -0.5f);   // yaw 120
        private static readonly Vector3 FallbackRight = new Vector3(-0.5f, 0f, -0.866f);

        private static Transform _origin;
        private static Transform _head;
        private static Transform _left, _right, _leftAim, _rightAim;
        private static float _nextSearch;

        /// <summary>The XR Origin transform, or null until it exists.</summary>
        public static Transform Origin
        {
            get { Refresh(); return _origin; }
        }

        public static Transform Head
        {
            get { Refresh(); return _head; }
        }

        public static Transform LeftController { get { Refresh(); return _left; } }
        public static Transform RightController { get { Refresh(); return _right; } }

        /// <summary>Ray origin/direction source for a hand: the XRI ray interactor if present.</summary>
        public static Transform AimTransform(bool left)
        {
            Refresh();
            if (left) return _leftAim != null ? _leftAim : _left;
            return _rightAim != null ? _rightAim : _right;
        }

        // ---------------- room coordinates ----------------

        public static Vector3 OriginPosition => Origin != null ? Origin.position : FallbackOrigin;

        /// <summary>Unit vector the seated user faces.</summary>
        public static Vector3 Forward
        {
            get
            {
                var o = Origin;
                if (o == null) return FallbackForward;
                Vector3 f = o.forward; f.y = 0f;
                return f.sqrMagnitude > 1e-4f ? f.normalized : FallbackForward;
            }
        }

        public static Vector3 Right
        {
            get
            {
                var o = Origin;
                if (o == null) return FallbackRight;
                Vector3 r = o.right; r.y = 0f;
                return r.sqrMagnitude > 1e-4f ? r.normalized : FallbackRight;
            }
        }

        /// <summary>World position of a point given as (metres forward, metres right, height).</summary>
        public static Vector3 Place(float forward, float right, float height)
        {
            Vector3 o = OriginPosition;
            return new Vector3(o.x, 0f, o.z) + Forward * forward + Right * right + Vector3.up * height;
        }

        /// <summary>
        /// Rotation that makes a quad or TextMesh at <paramref name="worldPos"/> readable
        /// from where the user stands: Unity text and quads read correctly when their
        /// local +Z points away from the viewer, so forward = (panel - viewer).
        /// </summary>
        public static Quaternion FacingUser(Vector3 worldPos, bool keepUpright = true)
        {
            Vector3 viewer = Head != null ? Head.position : Place(0f, 0f, 1.6f);
            Vector3 dir = worldPos - viewer;
            if (keepUpright) dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = Forward;
            return Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        /// <summary>Rotation facing the user from the measured origin, ignoring head yaw drift.</summary>
        public static Quaternion FacingOrigin(Vector3 worldPos, float viewerHeight = 1.6f)
        {
            Vector3 viewer = Place(0f, 0f, viewerHeight);
            Vector3 dir = worldPos - viewer;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = Forward;
            return Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        // ---------------- discovery ----------------

        /// <summary>Re-runs discovery for anything still missing, at most once a second.</summary>
        public static void Refresh(bool force = false)
        {
            bool missing = _origin == null || _head == null || _left == null || _right == null ||
                           _leftAim == null || _rightAim == null;
            if (!force && (!missing || Time.unscaledTime < _nextSearch)) return;
            _nextSearch = Time.unscaledTime + 1f;

            if (_head == null && Camera.main != null) _head = Camera.main.transform;

            if (_origin == null)
            {
                foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
                {
                    string n = t.name.ToLowerInvariant();
                    if (n.Contains("xr origin") || n.Contains("xr rig")) { _origin = t; break; }
                }
                // Last resort: the camera's parent chain usually hangs off the origin.
                if (_origin == null && _head != null && _head.parent != null)
                    _origin = _head.parent.parent != null ? _head.parent.parent : _head.parent;
            }

            if (_left == null || _right == null)
            {
                Transform bestL = null, bestR = null; int scoreL = 0, scoreR = 0;
                foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
                {
                    int sc = ControllerNameScore(t.name, out bool isLeft);
                    if (sc <= 0) continue;
                    if (isLeft && sc > scoreL) { bestL = t; scoreL = sc; }
                    if (!isLeft && sc > scoreR) { bestR = t; scoreR = sc; }
                }
                if (_left == null) _left = bestL;
                if (_right == null) _right = bestR;
            }

            if (_leftAim == null && _left != null) _leftAim = FindRayInteractor(_left);
            if (_rightAim == null && _right != null) _rightAim = FindRayInteractor(_right);
        }

        /// <summary>The XRI "Ray Interactor" under a controller, whose forward is the aim direction.</summary>
        private static Transform FindRayInteractor(Transform controller)
        {
            foreach (var t in controller.GetComponentsInChildren<Transform>(true))
                if (t.name.IndexOf("Ray Interactor", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return t;
            return null;
        }

        /// <summary>
        /// Ranks transform names so the real tracked controller ("Left Controller") wins
        /// over helper children (attach points, visuals, interactors). Same ranking the
        /// biometric sampler uses, kept here so both agree on which transform is the hand.
        /// </summary>
        public static int ControllerNameScore(string name, out bool isLeft)
        {
            string n = name.ToLowerInvariant();
            isLeft = n.Contains("left");
            bool isRight = n.Contains("right");
            if (isLeft == isRight || !n.Contains("controller")) return 0;
            string[] reject = { "attach", "interaction", "stabiliz", "stabilis", "teleport", "visual", "model", "poke", "ray", "near", "far", "grab" };
            foreach (var r in reject) if (n.Contains(r)) return 0;
            if (n == "left controller" || n == "right controller") return 3;
            if (n == "lefthand controller" || n == "righthand controller" ||
                n == "left hand controller" || n == "right hand controller") return 2;
            return 1;
        }

        /// <summary>Clears cached transforms (scene reload, tests).</summary>
        public static void Invalidate()
        {
            _origin = _head = _left = _right = _leftAim = _rightAim = null;
            _nextSearch = 0f;
        }
    }
}
