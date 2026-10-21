using System.Collections;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using BoardroomXR.Auth;

namespace BoardroomXR.Tests
{
    /// <summary>
    /// Guards the Inspector-authored UnityEvent wiring the demo depends on.
    /// The keypad buttons store their target as an assembly-qualified type name
    /// (".., Assembly-CSharp"), so moving the BoardroomXR scripts into their own
    /// assembly could silently break every digit button. This drives the real
    /// Button.onClick and asserts the PIN actually reached PinAuthenticator.
    /// </summary>
    public class SceneWiringPlayModeTests
    {
        private const string SceneName = "SampleScene";

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var manager = NetworkManager.Singleton;
            if (manager != null)
            {
                if (manager.IsListening) manager.Shutdown();

                float deadline = Time.realtimeSinceStartup + 10f;
                while (manager != null && manager.IsListening && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                if (manager != null) Object.DestroyImmediate(manager.gameObject);
            }

            yield return new WaitForSecondsRealtime(0.5f);
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        public IEnumerator KeypadButtons_StillInvokePinAuthenticator()
        {
            LogAssert.ignoreFailingMessages = true;

            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            for (int i = 0; i < 5; i++) yield return null;

            var keypad = Object.FindAnyObjectByType<BoardroomXR.UI.PinKeypadUI>(FindObjectsInactive.Include);
            Assert.IsNotNull(keypad, "No PinKeypadUI in SampleScene.");

            var pin = Object.FindAnyObjectByType<PinAuthenticator>(FindObjectsInactive.Include);
            Assert.IsNotNull(pin, "No PinAuthenticator in SampleScene.");

            // Find the digit buttons by name and confirm their persistent call
            // still resolves to a PinKeypadUI method.
            int wired = 0;
            var buttons = Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var button in buttons)
            {
                if (!button.gameObject.name.StartsWith("Btn_")) continue;

                for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                {
                    var target = button.onClick.GetPersistentTarget(i);
                    string method = button.onClick.GetPersistentMethodName(i);
                    if (target is BoardroomXR.UI.PinKeypadUI && !string.IsNullOrEmpty(method))
                    {
                        wired++;
                        Debug.Log("[WIRING] " + button.gameObject.name + " -> " + method
                                  + " on " + target.GetType().FullName
                                  + " (asm " + target.GetType().Assembly.GetName().Name + ")");
                    }
                }
            }

            Assert.GreaterOrEqual(wired, 10,
                "Expected at least 10 keypad buttons still bound to PinKeypadUI (0-9 plus Clear); "
                + "found " + wired + ". A broken binding here means the PIN cannot be entered on the headset.");

            // Now actually drive the buttons and confirm the PIN was submitted.
            pin.Initialize(Object.FindAnyObjectByType<MFACoordinator>().settings);

            string submitted = null;
            void OnSubmit(string value) => submitted = value;
            pin.OnPinSubmitted += OnSubmit;

            try
            {
                foreach (string name in new[] { "Btn_1", "Btn_2", "Btn_3", "Btn_4" })
                {
                    var go = FindByName(name);
                    Assert.IsNotNull(go, "No GameObject named " + name + " in SampleScene.");

                    var button = go.GetComponent<Button>();
                    Assert.IsNotNull(button, name + " has no Button component.");

                    button.onClick.Invoke();
                    yield return null;
                }
            }
            finally
            {
                pin.OnPinSubmitted -= OnSubmit;
            }

            Assert.AreEqual("1234", submitted,
                "Driving Btn_1..Btn_4 did not deliver '1234' to PinAuthenticator (got "
                + (submitted == null ? "<nothing>" : "'" + submitted + "'")
                + "). The Inspector OnClick bindings are broken.");
        }

        private static GameObject FindByName(string name)
        {
            var all = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var transform in all)
            {
                if (transform.gameObject.name == name) return transform.gameObject;
            }
            return null;
        }
    }
}
