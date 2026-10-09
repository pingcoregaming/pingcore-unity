using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Connect;
using PingCore.Editor.Workspace.UI.Sections;
using UnityEditor;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>
    /// The push token of the CDN source Push pushes to (the branch's): the line saying whether one is stored (never
    /// shown), the automatic issue (the default: Push issues one only when none is stored, after the confirmation),
    /// Replace push token..., and "Use an existing push token" for a token issued elsewhere (the panel, CI, a
    /// teammate). The pasted value goes from a write-only field, emptied the moment Use token is pressed, through
    /// <see cref="PushTokenFlow.UseExistingAsync"/> (shape, then PingCore must accept it and name the branch's source)
    /// into the persistent store; it is never shown, logged or kept anywhere else.
    /// </summary>
    public sealed partial class ShipSection
    {
        private Label tokenLine;
        private string replacedLine;
        private TextField existingTokenField;
        private Label existingTokenStatus;
        private SectionActions tokenActions;

        private void BuildTokenArea(VisualElement body)
        {
            tokenLine = Ui.Status();
            body.Add(Ui.Row(Ui.WithClass(tokenLine, "pingcore-grow"), Ui.Button("Replace push token...", () => StartRow(pushRow, ReplaceTokenAsync))));
            tokenActions = new SectionActions();
            existingTokenField = Ui.Password("Use an existing push token");
            existingTokenField.tooltip = "A cdnpush_ token issued in the panel, by CI or by a teammate for this branch's CDN source. It is checked with PingCore, kept in your credential store and never shown.";
            body.Add(Ui.Row(existingTokenField, Ui.Button("Use token", UseExistingToken)));
            existingTokenStatus = Ui.Status();
            body.Add(existingTokenStatus);
        }

        private void RefreshToken()
        {
            if (tokenLine == null)
            {
                return;
            }

            PushTarget target = Target();
            PushTokenFlow tokens = commands?.PushTokens;
            if (!target.CanPush)
            {
                tokenLine.text = "The push token belongs to the branch's CDN source: " + target.Line;
            }
            else if (tokens == null)
            {
                tokenLine.text = commands?.UnavailableReason;
            }
            else if (replacedLine != null && replacedLine.Contains("#" + target.CdnSourceId + " "))
            {
                tokenLine.text = replacedLine;
            }
            else
            {
                PushTokenPresence presence = tokens.Presence(target.CdnSourceId, out string storeProblem);
                tokenLine.text = presence == PushTokenPresence.Stored
                    ? $"A push token for CDN source #{target.CdnSourceId} is in your credential store (never shown)."
                    : presence == PushTokenPresence.None
                        ? $"No push token for CDN source #{target.CdnSourceId} is stored; Push issues one after you confirm, or use an existing one below."
                        : storeProblem;
            }
        }

        // Issues a push token only when none is stored (or when replacing one on purpose), after the confirmation.
        private async Task<bool> EnsurePushTokenAsync(IPingCoreApi api, long sourceId, bool replace, ShipRowView row, CancellationToken ct)
        {
            PushTokenFlow tokens = commands.PushTokens;
            if (tokens == null)
            {
                row.Raise(ShipRowEvent.Fail, commands.UnavailableReason);
                return false;
            }

            PushTokenPresence presence = tokens.Presence(sourceId, out string storeProblem);
            if (presence == PushTokenPresence.Unreadable)
            {
                row.Raise(ShipRowEvent.Fail, storeProblem + " No push token was issued: issuing would stop every other copy working.");
                return false;
            }

            if (!replace && presence == PushTokenPresence.Stored)
            {
                return true;
            }

            bool confirmed = EditorUtility.DisplayDialog(replace ? "Replace the push token?" : "Issue a push token?", PushTokenFlow.IssueConfirmationText(sourceId), replace ? "Replace it" : "Issue it", "Cancel");
            if (!confirmed)
            {
                row.Raise(ShipRowEvent.Fail, "No push token was issued and nothing was pushed; the current token, if any, keeps working. To use a token issued elsewhere, paste it under Use an existing push token.");
                return false;
            }

            // Not cancellable: a POST the workspace already applied would replace every other copy while the answer,
            // and with it the new token, was dropped. It is one short call.
            ApiResult<SecretReceipt> issued = await tokens.IssueAsync(api, sourceId, true, replace, CancellationToken.None);
            if (!issued.Ok)
            {
                row.Raise(ShipRowEvent.Fail, ErrorText.Of(issued.Error));
                return false;
            }

            return true;
        }

        private async Task ReplaceTokenAsync(ShipRowView row, CancellationToken ct)
        {
            IPingCoreApi api = SignedInApi(row);
            if (api == null)
            {
                return;
            }

            (FleetFacts facts, PluginError error) = await ConnectState.Read(api, WorkspaceContext.LoadProject(out _).FleetId, ct);
            if (facts == null)
            {
                row.Raise(ShipRowEvent.Fail, ErrorText.Of(error));
                return;
            }

            connect.Use(facts);
            PushTarget target = PushTargetResolution.ForFleet(facts, WorkspaceContext.LoadProject(out _).GameBranchId);
            if (!target.CanPush)
            {
                row.Raise(ShipRowEvent.Fail, target.Line);
                return;
            }

            if (await EnsurePushTokenAsync(api, target.CdnSourceId, true, row, ct))
            {
                // The row ran a token replace, not a push: it goes back to idle and the result is on the token line.
                row.Raise(ShipRowEvent.Succeed, "Push token replaced.");
                row.Raise(ShipRowEvent.Reset, null);
                replacedLine = $"A new push token for CDN source #{target.CdnSourceId} is in your credential store; the previous one no longer works.";
            }
        }

        // Use token: the field is emptied at once, whatever happens next, and the value is never captured by a Retry.
        private void UseExistingToken()
        {
            string pasted = existingTokenField.value;
            existingTokenField.value = string.Empty;
            if (busyRow != null || commands?.Events?.RunStatus == PipelineRunStatus.Running)
            {
                existingTokenStatus.text = "Wait for the running step to end, then paste the token again. Nothing was stored.";
                return;
            }

            tokenActions.Run(existingTokenStatus, ct => UseExistingTokenAsync(pasted, ct), Changed);
            pasted = null;
        }

        private async Task UseExistingTokenAsync(string pasted, CancellationToken ct)
        {
            EnsureCommands();
            PushTokenFlow tokens = commands.PushTokens;
            IPingCoreApi api = WorkspaceContext.SignedInApi(out string problem);
            if (tokens == null || api == null)
            {
                existingTokenStatus.text = (problem ?? commands.UnavailableReason) + " Nothing was stored.";
                return;
            }

            existingTokenStatus.text = "Checking the push token with PingCore...";
            (FleetFacts facts, PluginError error) = await ConnectState.Read(api, WorkspaceContext.LoadProject(out _).FleetId, ct);
            if (facts == null)
            {
                existingTokenStatus.text = ErrorText.Of(error) + " Nothing was stored.";
                return;
            }

            connect.Use(facts);
            PushTarget target = PushTargetResolution.ForFleet(facts, WorkspaceContext.LoadProject(out _).GameBranchId);
            if (!target.CanPush)
            {
                existingTokenStatus.text = target.Line + " Nothing was stored.";
                return;
            }

            long sourceId = target.CdnSourceId;
            ApiResult<SecretReceipt> kept = await tokens.UseExistingAsync(api, sourceId, pasted, () => EditorUtility.DisplayDialog(
                "Replace the stored push token?", PushTokenFlow.ReplaceStoredConfirmationText(sourceId), "Use the pasted one", "Keep the stored one"), ct);
            pasted = null;
            if (!kept.Ok)
            {
                existingTokenStatus.text = ErrorText.Of(kept.Error);
                return;
            }

            replacedLine = null;
            existingTokenStatus.text = $"The push token for CDN source #{sourceId} is checked and kept in your credential store (never shown). Push uses it.";
        }
    }
}
