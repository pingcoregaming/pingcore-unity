using BeaconRush.Match;
using BeaconRush.Networking;
using UnityEngine;
using UnityEngine.Rendering;

namespace BeaconRush.Client.UI
{
    /// <summary>
    /// The arena camera and the player's input. The scene's orthographic camera looks down on the arena at a steep angle
    /// (<c>ClientSceneBuilder</c> places it); this keeps the whole walled arena in frame on any window shape, behind the menu
    /// as well as during a session. During a match the movement axes (WASD or arrows) go to <see cref="BeaconRushPlayer.Local"/>
    /// every frame; it throttles its own sends. It also sets the ambient light from the scene's flat colour (the scene bakes
    /// no lighting). Nothing runs in batchmode or in a headless process.
    /// </summary>
    public sealed class MatchView : MonoBehaviour
    {
        /// <summary>Half the width the camera must always show: the arena, its walls and its corner pylons, plus a margin.</summary>
        public const float FramedHalfWidth = MatchRules.ArenaHalfSize + 2.5f;

        [SerializeField]
        [Tooltip("The orthographic arena camera.")]
        private Camera viewCamera;

        [SerializeField]
        [Tooltip("The orthographic size that frames the arena's depth; widened on narrow windows.")]
        private float baseOrthographicSize = 12.5f;

        private ClientUi ui;

        private void Start()
        {
            if (!ClientLaunch.Interactive)
            {
                enabled = false;
                return;
            }

            ui = GetComponent<ClientUi>();

            // The client scene bakes no lighting, so the ambient probe would be a default grey that lifts every colour.
            // Build it from the scene's flat ambient colour instead.
            var ambient = new SphericalHarmonicsL2();
            ambient.AddAmbientLight(RenderSettings.ambientLight);
            RenderSettings.ambientProbe = ambient;
        }

        private void Update()
        {
            if (viewCamera != null && viewCamera.aspect > 0f)
            {
                float size = Mathf.Max(baseOrthographicSize, FramedHalfWidth / viewCamera.aspect);
                if (!Mathf.Approximately(viewCamera.orthographicSize, size))
                {
                    viewCamera.orthographicSize = size;
                }
            }

            BeaconRushPlayer local = BeaconRushPlayer.Local;
            if (ui == null || ui.ActiveManager == null || ui.Screen != ClientScreen.Match || local == null)
            {
                return;
            }

            var move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            local.SubmitInput(move.sqrMagnitude > 1f ? move.normalized : move);
        }
    }
}
