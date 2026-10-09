using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Discovery.Client;
using PingCore.Editor.BuildGuard;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Api.Wire;
using PingCore.Editor.Workspace.Confirmation;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Sections;
using UnityEditor;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.PlayerHosting
{
    /// <summary>
    /// Player hosting, an optional fold, off by default: only for games whose players host their own game
    /// servers. It shows what a player build ships from the <see cref="PingCoreClientSettings"/> asset the plugin
    /// owns (<see cref="ClientSettingsAsset"/>; there is no object field): the fleet app's public id (Connect writes
    /// it), the open community app's public id with a Change dropdown of the workspace's open Discovery apps, the
    /// community heartbeat token as a write-only field (<c>PlayerHostingFold.Token.cs</c>) with Confirm for build, and
    /// the build guard's view of every configured token. The Discovery URL is the SDK's constant and is not shown
    /// or stored.
    /// </summary>
    public sealed partial class PlayerHostingFold
    {
        private const string NoCommunityApp = "(none: no listen hosts)";

        private Action changed;

        /// <summary>The fleet's CDN source (Connect's), so Confirm for build refuses a token equal to its push token.</summary>
        public Func<long> CdnSourceId { get; set; }
        private SectionActions actions;
        private Label assetLine;
        private Label fleetAppLine;
        private Label communityAppLine;
        private DropdownField communityAppChoice;
        private Label appsStatus;
        private List<DiscoveryAppListItem> apps = new List<DiscoveryAppListItem>();

        /// <summary>The asset the plugin uses, or null when the project has none yet (nothing is created to read it).</summary>
        public static PingCoreClientSettings Asset(out string path) => ClientSettingsAsset.Find(out path);

        /// <summary>Builds the fold's body.</summary>
        public void Build(VisualElement body, Action onChanged)
        {
            changed = onChanged;
            actions = new SectionActions();
            apps = new List<DiscoveryAppListItem>();

            assetLine = Ui.Note(string.Empty);
            body.Add(assetLine);
            body.Add(Ui.Section("Discovery apps (public ids, not secrets)"));
            fleetAppLine = Ui.Status();
            body.Add(fleetAppLine);
            communityAppLine = Ui.Status();
            communityAppChoice = new DropdownField("Community app", new List<string>(), 0) { style = { display = DisplayStyle.None } };
            communityAppChoice.RegisterValueChangedCallback(e => SectionActions.Guard(appsStatus, () => Choose(e.newValue), Changed));
            body.Add(Ui.Row(Ui.WithClass(communityAppLine, "pingcore-grow"), Ui.Button("Change", () => actions.Run(appsStatus, ct => ShowChoicesAsync(communityAppChoice, ct), Changed))));
            body.Add(communityAppChoice);
            appsStatus = Ui.Status();
            body.Add(appsStatus);

            BuildToken(body);
            Refresh();
        }

        /// <summary>Re-reads the asset and the guard's confirmations.</summary>
        public void Refresh()
        {
            if (assetLine == null)
            {
                return;
            }

            IReadOnlyList<string> paths = ClientSettingsAsset.FindPaths();
            string path = ClientSettingsAsset.Choose(paths);
            PingCoreClientSettings asset = path == null ? null : AssetDatabase.LoadAssetAtPath<PingCoreClientSettings>(path);
            assetLine.text = asset == null
                ? $"No settings asset yet: the plugin creates {ClientSettingsAsset.DefaultPath} when it first writes an id."
                : $"The plugin keeps these in {path}." + (ClientSettingsAsset.SeveralNote(paths) is string several ? " " + several : string.Empty);
            fleetAppLine.text = "Fleet app: " + Describe(asset?.FleetAppPublicId, "not set (pick the fleet under Connect)");
            communityAppLine.text = "Community app: " + Describe(asset?.CommunityAppPublicId, "none (only listen hosts need one)");
            RefreshToken(asset);
        }

        private string Describe(string publicId, string empty)
        {
            if (string.IsNullOrWhiteSpace(publicId))
            {
                return empty;
            }

            DiscoveryAppListItem app = apps.FirstOrDefault(a => a.PublicId == publicId);
            return app == null ? publicId : $"{app.Name} ({publicId}, {app.RegistrationMode})";
        }

        private void Changed()
        {
            Refresh();
            changed?.Invoke();
        }

        private async Task ShowChoicesAsync(DropdownField field, CancellationToken ct)
        {
            IPingCoreApi api = WorkspaceContext.SignedInApi(out string problem);
            if (api == null)
            {
                appsStatus.text = problem;
                return;
            }

            appsStatus.text = "Listing the workspace's Discovery apps...";
            ApiResult<DiscoveryAppListResponse> listed = await api.ListDiscoveryAppsAsync(ct);
            if (!listed.Ok)
            {
                appsStatus.text = ErrorText.Of(listed.Error);
                return;
            }

            apps = (listed.Value.Apps ?? new List<DiscoveryAppListItem>()).Where(a => a != null && !string.IsNullOrEmpty(a.PublicId)).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            List<DiscoveryAppListItem> fitting = apps.Where(a => a.RegistrationMode == "open").ToList();
            List<string> choices = fitting.Select(Choice).ToList();
            choices.Insert(0, NoCommunityApp);

            field.choices = choices;
            field.SetValueWithoutNotify(string.Empty);
            field.style.display = DisplayStyle.Flex;
            appsStatus.text = $"{fitting.Count} open app(s). Pick the community app listen hosts register with, or none.";
        }

        private static string Choice(DiscoveryAppListItem app) => $"{app.Name} ({app.PublicId})";

        private void Choose(string choice)
        {
            string publicId = choice == NoCommunityApp ? string.Empty : apps.FirstOrDefault(a => Choice(a) == choice)?.PublicId;
            if (publicId == null)
            {
                return;
            }

            PingCoreClientSettings asset = ClientSettingsAsset.FindOrCreate(out string path, out _);
            var serialized = new SerializedObject(asset);
            serialized.Update();
            bool tokenCleared = SetCommunityApp(serialized, publicId);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(asset);

            appsStatus.text = $"Saved in {path}." + (tokenCleared ? " The community heartbeat token was cleared: it belonged to the previous community app." : string.Empty);
            communityAppChoice.style.display = DisplayStyle.None;
        }

        private static string ApplyToAsset(SerializedObject serialized, string property, string value)
        {
            serialized.Update();
            SerializedProperty target = serialized.FindProperty(property);
            target.stringValue = (value ?? string.Empty).Trim();
            serialized.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(serialized.targetObject);
            return target.stringValue;
        }

        /// <summary>
        /// Sets the community app id on <paramref name="serialized"/> (not yet applied) and clears the heartbeat
        /// token when it replaces another app's id, since the token belongs to that app (a token set while no app
        /// was named was never confirmed, and Confirm for build checks it against the new app). True when a token
        /// was cleared.
        /// </summary>
        public static bool SetCommunityApp(SerializedObject serialized, string publicId)
        {
            SerializedProperty community = serialized.FindProperty("communityAppPublicId");
            SerializedProperty token = serialized.FindProperty("openRegistrationHeartbeatToken");
            string next = (publicId ?? string.Empty).Trim();
            string previous = community?.stringValue ?? string.Empty;
            bool changed = previous.Length > 0 && !string.Equals(previous, next, StringComparison.Ordinal);
            if (community != null)
            {
                community.stringValue = next;
            }

            if (changed && token != null && !string.IsNullOrEmpty(token.stringValue))
            {
                token.stringValue = string.Empty;
                return true;
            }

            return false;
        }

        /// <summary>
        /// The asset's token state for the section model: confirmed only when a confirmation matches the token
        /// AND names the asset's own community app, so a token confirmed for another app reads unconfirmed.
        /// </summary>
        public static ConfirmationState TokenState(PingCoreClientSettings asset)
        {
            IReadOnlyList<BuildGuardConfirmation> confirmations = BuildGuardConfirmationFile.Read(WorkspaceContext.ProjectRoot);
            return TokenState(asset?.OpenRegistrationHeartbeatToken, asset?.CommunityAppPublicId, confirmations);
        }

        /// <summary>The rule behind <see cref="TokenState(PingCoreClientSettings)"/>, pure.</summary>
        public static ConfirmationState TokenState(string token, string communityAppPublicId, IReadOnlyList<BuildGuardConfirmation> confirmations)
        {
            ConfirmationState state = ConfirmationStatus.StateOf(token, confirmations);
            if (state != ConfirmationState.Confirmed)
            {
                return state;
            }

            string trimmed = token.Trim();
            return (confirmations ?? new List<BuildGuardConfirmation>()).Any(c => BuildGuardConfirmations.Confirms(c, trimmed) && string.Equals(c.appPublicId, (communityAppPublicId ?? string.Empty).Trim(), StringComparison.Ordinal))
                ? ConfirmationState.Confirmed
                : ConfirmationState.Unconfirmed;
        }
    }
}
