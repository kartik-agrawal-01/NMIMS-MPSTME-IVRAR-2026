using UnityEngine;
using BoardroomXR.Auth;

namespace BoardroomXR.UI
{
    /// <summary>
    /// TEST HARNESS - replace with real XR controller gesture capture.
    /// Lets Factor 2 be demonstrated in the simulator without drawing a gesture
    /// with the controller: on <see cref="SubmitTestGesture"/> it drives the
    /// GestureAuthenticator with a synthetic straight-line path that matches the
    /// hardcoded TEST TEMPLATE closely enough to clear gestureMatchThreshold.
    /// Wire a "Submit Gesture (Test)" button's OnClick() to SubmitTestGesture().
    /// </summary>
    public class GestureTestSubmitter : MonoBehaviour
    {
        public GestureAuthenticator gestureAuthenticator;

        public void SubmitTestGesture()
        {
            if (gestureAuthenticator == null)
            {
                Debug.LogWarning("[GestureTestSubmitter] gestureAuthenticator not assigned.", this);
                return;
            }

            Debug.Log("[GestureTestSubmitter] Feeding synthetic gesture path (TEST).", this);
            gestureAuthenticator.BeginCapture();
            // A straight line matching the TEST TEMPLATE. Scoring normalises
            // scale/position, so a colinear path scores ~1.0 against it.
            gestureAuthenticator.RecordPoint(new Vector3(0.00f, 0f, 0f));
            gestureAuthenticator.RecordPoint(new Vector3(0.25f, 0f, 0f));
            gestureAuthenticator.RecordPoint(new Vector3(0.50f, 0f, 0f));
            gestureAuthenticator.RecordPoint(new Vector3(0.75f, 0f, 0f));
            gestureAuthenticator.RecordPoint(new Vector3(1.00f, 0f, 0f));
            gestureAuthenticator.EndCaptureAndScore();
        }
    }
}
