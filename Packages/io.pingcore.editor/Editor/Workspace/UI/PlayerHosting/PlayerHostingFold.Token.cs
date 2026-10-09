using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Discovery.Client;
using PingCore.Editor.BuildGuard;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Confirmation;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Sections;
using UnityEditor;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.PlayerHosting
{
    /// <summary>
    /// The community heartbeat token of Player hosting: a write-only password field that never holds the
    /// asset's value (it empties as soon as a typed token is applied, and a label says only whether one is
    /// set), Clear, Confirm for build (<see cref="HeartbeatTokenConfirmationWriter"/>) and the
    /// build guard's view of every configured token.
    /// </summary>
    public sealed partial class PlayerHostingFold
    {
        private TextField tokenField;
        private Label tokenState;
        private Label confirmStatus;
        private Label guardView;

        private void BuildToken(VisualElement body)
        {
            body.Add(Ui.Section("Community heartbeat token (listen hosts only)"));
            body.Add(Ui.Note("The one secret a player build may carry, and only when it is the open community app's heartbeat token and Confirm for build has checked it. Leave it empty and a listen host takes its token from PINGCORE_DISCOVERY_TOKEN instead."));
            tokenField = Ui.Password("Set token");
            tokenField.isDelayed = true;
            tokenField.tooltip = "Type or paste the token and press Enter. The field empties at once; the token is never shown again.";
            tokenField.RegisterValueChangedCallback(e => SectionActions.Guard(confirmStatus, () => SetTyped(e.newValue), Changed));
            body.Add(tokenField);
            tokenState = Ui.Status();
            body.Add(Ui.Row(Ui.WithClass(tokenState, "pingcore-grow"),
                Ui.Button("Clear", () => SectionActions.Guard(confirmStatus, ClearToken, Changed))));
            body.Add(Ui.Row(Ui.Primary(Ui.Button("Confirm for build", () => actions.Run(confirmStatus, ConfirmAsync, Changed)))));
            confirmStatus = Ui.Status();
            body.Add(confirmStatus);
            body.Add(Ui.Note(HeartbeatTokenConfirmationPolicy.LastFourLimitation));
            body.Add(Ui.Section("Build guard view of every configured token"));
            guardView = Ui.Status();
            body.Add(guardView);
        }

        private void RefreshToken(PingCoreClientSettings asset)
        {
            tokenState.text = string.IsNullOrEmpty(asset?.OpenRegistrationHeartbeatToken)
                ? "No heartbeat token is set."
                : "A heartbeat token is set (never shown). Type a new one to replace it.";
            IReadOnlyList<BuildGuardConfirmation> confirmations = BuildGuardConfirmationFile.Read(WorkspaceContext.ProjectRoot);
            List<string> lines = ClientSettingsAsset.FindPaths()
                .Select(p => (Path: p, Settings: AssetDatabase.LoadAssetAtPath<PingCoreClientSettings>(p)))
                .Select(s => $"{s.Path}: {ConfirmationStatus.Describe(s.Settings.OpenRegistrationHeartbeatToken, confirmations)}")
                .ToList();
            if (asset != null && TokenState(asset) == ConfirmationState.Unconfirmed && ConfirmationStatus.StateOf(asset.OpenRegistrationHeartbeatToken, confirmations) == ConfirmationState.Confirmed)
            {
                lines.Add("The token was confirmed for another app, not this asset's community app: press Confirm for build again, or clear it.");
            }

            guardView.text = lines.Count == 0 ? "No PingCoreClientSettings asset in the project: builds carry no token." : string.Join("\n", lines);
        }

        // The field never holds the asset's value: it empties at once and the typed token goes straight into the asset.
        private void SetTyped(string typed)
        {
            string token = (typed ?? string.Empty).Trim();
            tokenField.SetValueWithoutNotify(string.Empty);
            if (token.Length == 0)
            {
                return;
            }

            PingCoreClientSettings asset = ClientSettingsAsset.FindOrCreate(out _, out _);
            ApplyToAsset(new SerializedObject(asset), "openRegistrationHeartbeatToken", token);
            confirmStatus.text = "Heartbeat token saved in the asset. Press Confirm for build before building a player with it.";
        }

        private void ClearToken()
        {
            PingCoreClientSettings asset = Asset(out _);
            if (asset != null)
            {
                ApplyToAsset(new SerializedObject(asset), "openRegistrationHeartbeatToken", string.Empty);
            }

            confirmStatus.text = "Heartbeat token cleared; builds carry none.";
        }

        private async Task ConfirmAsync(CancellationToken cancellationToken)
        {
            PingCoreClientSettings asset = Asset(out _);
            if (asset == null || string.IsNullOrEmpty(asset.OpenRegistrationHeartbeatToken))
            {
                confirmStatus.text = "No heartbeat token is set, so there is nothing to confirm: builds carry none.";
                return;
            }

            IPingCoreApi api = WorkspaceContext.SignedInApi(out string problem);
            if (api == null)
            {
                confirmStatus.text = problem;
                return;
            }

            confirmStatus.text = "Asking the workspace about the app and its tokens...";
            // The key lives in the store the developer chose; push tokens always in the persistent store (WorkspaceContext.PushTokenStore).
            List<string> stored = HeartbeatTokenConfirmationWriter.StoredSecrets(WorkspaceContext.Store(), api.Endpoint.Host, 0)
                .Concat(HeartbeatTokenConfirmationWriter.StoredSecrets(WorkspaceContext.PushTokenStore(), api.Endpoint.Host, CdnSourceId?.Invoke() ?? 0))
                .Distinct().ToList();
            try
            {
                ConfirmationResult result = await new HeartbeatTokenConfirmationWriter(api).ConfirmAsync(
                    WorkspaceContext.ProjectRoot, asset.OpenRegistrationHeartbeatToken, asset.CommunityAppPublicId, stored, cancellationToken);
                confirmStatus.text = result.Error != null ? "Not confirmed: " + ErrorText.Of(result.Error) : (result.Ok ? result.Message : "Not confirmed: " + result);
            }
            catch (IOException e)
            {
                confirmStatus.text = "Not confirmed: the confirmation file could not be written (" + e.GetType().Name + ").";
            }
        }
    }
}
