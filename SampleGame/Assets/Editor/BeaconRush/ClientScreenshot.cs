using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BeaconRush.Editor
{
    /// <summary>
    /// A local look at the client: opens <c>Client.unity</c> in an interactive editor (never <c>-batchmode</c>: the client
    /// draws nothing in batchmode), enters Play mode, optionally joins a LAN-only host through Direct connect, waits for the
    /// match to show and for a frame with beacons on the field, and saves one screenshot of the Game view, then quits. Talks
    /// to no Discovery and no platform.
    /// <para>
    /// <c>Unity.exe -projectPath SampleGame -buildTarget Win64 -standaloneBuildSubtarget Player
    /// -executeMethod BeaconRush.Editor.ClientScreenshot.Run [-beaconRushShotConnect 127.0.0.1:7799]
    /// [-beaconRushShotOut Builds/Screenshots/beacon-rush.png]</c>. Exit 0 once the file is written, 1 after 120 s without it.
    /// The output path is relative to the project; the default is under its <c>Builds/</c>, which git ignores.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class ClientScreenshot
    {
        public const string ConnectFlag = "-beaconRushShotConnect";
        public const string OutFlag = "-beaconRushShotOut";

        private const string LogPrefix = "[ClientScreenshot] ";
        private const string PendingKey = "BeaconRush.Shot.Pending";
        private const string ConnectKey = "BeaconRush.Shot.Connect";
        private const string OutKey = "BeaconRush.Shot.Out";
        private const string StartedKey = "BeaconRush.Shot.Started";
        private const string StepKey = "BeaconRush.Shot.Step";
        private const string MatchSeenKey = "BeaconRush.Shot.MatchSeen";
        private const double ConnectAfterSeconds = 2.0;
        private const double SettleSeconds = 6.0;
        private const double MenuShotAfterSeconds = 4.0;
        private const double BeaconWaitSeconds = 20.0;
        private const double TimeoutSeconds = 120.0;

        static ClientScreenshot()
        {
            if (SessionState.GetBool(PendingKey, false))
            {
                EditorApplication.update += Tick;
            }
        }

        /// <summary>The <c>-executeMethod</c> entry point.</summary>
        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = Path.GetFullPath(Path.Combine(projectRoot, "Builds", "Screenshots", "beacon-rush.png"));
            string connect = string.Empty;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], ConnectFlag, StringComparison.OrdinalIgnoreCase))
                {
                    connect = args[i + 1];
                }
                else if (string.Equals(args[i], OutFlag, StringComparison.OrdinalIgnoreCase))
                {
                    output = Path.GetFullPath(Path.Combine(projectRoot, args[i + 1]));
                }
            }

            if (Application.isBatchMode)
            {
                Debug.LogError(LogPrefix + "run it without -batchmode: the client draws nothing in batchmode");
                EditorApplication.Exit(2);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output));
            if (File.Exists(output))
            {
                File.Delete(output);
            }

            SessionState.SetBool(PendingKey, true);
            SessionState.SetString(ConnectKey, connect);
            SessionState.SetString(OutKey, output);
            SessionState.SetFloat(StartedKey, (float)EditorApplication.timeSinceStartup);
            SessionState.SetInt(StepKey, 0);
            EditorSceneManager.OpenScene(ClientBuildArgs.ScenePath, OpenSceneMode.Single);
            SizeGameView();
            EditorApplication.update += Tick;
            EditorApplication.isPlaying = true;
        }

        private static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            double elapsed = now - SessionState.GetFloat(StartedKey, (float)now);
            string output = SessionState.GetString(OutKey, string.Empty);
            int step = SessionState.GetInt(StepKey, 0);
            if (elapsed > TimeoutSeconds)
            {
                Finish(1, "no screenshot after " + TimeoutSeconds + " s");
                return;
            }

            if (step == 3)
            {
                if (File.Exists(output) && new FileInfo(output).Length > 0)
                {
                    Finish(0, "wrote " + output);
                }

                return;
            }

            if (!EditorApplication.isPlaying)
            {
                return;
            }

            Component ui = FindClientUi();
            if (ui == null)
            {
                return;
            }

            string connect = SessionState.GetString(ConnectKey, string.Empty);
            if (step == 0 && elapsed > ConnectAfterSeconds)
            {
                if (connect.Length > 0)
                {
                    ui.GetType().GetProperty("DirectText", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(ui, connect);
                    ui.GetType().GetMethod("DirectConnect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ui, null);
                    Debug.Log(LogPrefix + "joining " + connect);
                }

                SessionState.SetInt(StepKey, 1);
                return;
            }

            if (step == 1)
            {
                string screen = ui.GetType().GetProperty("Screen").GetValue(ui).ToString();
                bool ready = connect.Length == 0 ? elapsed > MenuShotAfterSeconds : screen == "Match";
                if (ready)
                {
                    SessionState.SetFloat(MatchSeenKey, (float)now);
                    SessionState.SetInt(StepKey, 2);
                }

                return;
            }

            double sinceMatch = now - SessionState.GetFloat(MatchSeenKey, (float)now);
            bool settled = sinceMatch > (connect.Length == 0 ? 0.5 : SettleSeconds);
            // In a match, wait (a little) for a frame with beacons on the field: bots pick them up fast.
            bool showsBeacons = connect.Length == 0 || Object.FindObjectsByType<BeaconRush.Match.Beacon>(FindObjectsSortMode.None).Length >= 1
                || sinceMatch > SettleSeconds + BeaconWaitSeconds;
            if (step == 2 && settled && showsBeacons)
            {
                ScreenCapture.CaptureScreenshot(output);
                SessionState.SetInt(StepKey, 3);
                Debug.Log(LogPrefix + "capturing the Game view");
            }
        }

        private static Component FindClientUi()
        {
            Type type = Type.GetType("BeaconRush.Client.UI.ClientUi, BeaconRush.Client");
            return type == null ? null : Object.FindFirstObjectByType(type) as Component;
        }

        /// <summary>Gives the Game view a 1600 x 900 window of its own, so the shot has a known size.</summary>
        private static void SizeGameView()
        {
            Type gameView = Type.GetType("UnityEditor.GameView, UnityEditor");
            if (gameView == null)
            {
                return;
            }

            EditorWindow window = EditorWindow.GetWindow(gameView);
            window.position = new Rect(40, 40, 1600, 900 + 21);
            window.Focus();
        }

        private static void Finish(int code, string message)
        {
            EditorApplication.update -= Tick;
            SessionState.EraseBool(PendingKey);
            Debug.Log(LogPrefix + (code == 0 ? "PASS: " : "FAIL: ") + message);
            EditorApplication.isPlaying = false;
            EditorApplication.Exit(code);
        }
    }
}
