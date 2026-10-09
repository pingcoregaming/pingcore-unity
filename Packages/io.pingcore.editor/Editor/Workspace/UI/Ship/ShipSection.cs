using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PingCore.Editor.Build;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Connect;
using PingCore.Editor.Workspace.UI.Sections;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>
    /// Ship: three rows, Build, Push and Release, each with its own button and the hosting spec's four states
    /// (<see cref="ShipRowView"/>). Build runs the project's Linux Dedicated Server Build Profile picked here
    /// (<c>BuildPipeline.BuildPlayer(BuildPlayerWithProfileOptions)</c>, so the scenes, backend and defines are the
    /// developer's); Push sends the last build, or a folder the developer chose, to the CDN source of the game branch
    /// it pushes to (resolved from the fleet's game, never from its deployments) with the bundled <c>pingctl</c>, after
    /// showing the folder, its file count and size and refusing a build without the file the game's startup command
    /// launches (read from the fleet's deployments' template sets, else the game's, never a branch's default deployment
    /// spec; <c>ShipSection.Executable.cs</c> names the build's executable from the same read); Release, the only step that needs deployments, releases the fleet onto the snapshot that push
    /// published and watches the rollout. The pipeline (<see cref="PipelineRunner"/>) comes from
    /// <see cref="DeployCommandsRegistry"/>. The rows are in <c>ShipSection.Build.cs</c>, <c>ShipSection.Push.cs</c>
    /// (with the branch picker in <c>ShipSection.Branch.cs</c> and the push token in <c>ShipSection.Token.cs</c>) and
    /// <c>ShipSection.Release.cs</c>; the developer's own pingctl and the log in <c>ShipSection.Tools.cs</c>.
    /// </summary>
    public sealed partial class ShipSection
    {
        private const string NoProfileChoice = "(no Linux Dedicated Server build profile)";

        private ConnectState connect;
        private ConnectSection connectSection;
        private Action changed;
        private IDeployCommands commands;
        private string commandsFor;
        private CancellationTokenSource runCancellation;
        private ShipRowView busyRow;
        private bool cancelReleaseWhenStopped;
        private Label wiredBanner;
        private Label outsideBanner;
        private DropdownField profileField;
        private Label profileNote;
        private TextField versionField;
        private VisualElement root;
        private IReadOnlyList<BuildProfileInfo> profiles = Array.Empty<BuildProfileInfo>();

        /// <summary>The window's <c>OnEnable</c>: binds the pipeline.</summary>
        public void Enable()
        {
            commands = DeployCommandsRegistry.Create();
            commandsFor = CommandsKey();
            BindEvents();
        }

        /// <summary>The window's <c>OnDisable</c>: unbinds and stops the running row (its saved state stays resumable).</summary>
        public void Disable()
        {
            UnbindEvents();
            cancelReleaseWhenStopped = false;
            StopRunning();
        }

        // Stops whatever row runs, inside the pipeline or before it (a fleet read, the startup command, a token issue).
        // The row's own StartRow disposes its token source when it ends.
        private void StopRunning()
        {
            commands?.Stop();
            runCancellation?.Cancel();
        }

        /// <summary>Builds the section's body.</summary>
        public void Build(VisualElement body, ConnectState shared, ConnectSection connectView, Action onChanged)
        {
            if (commands == null)
            {
                Enable();
            }

            root = body;
            connect = shared;
            connectSection = connectView;
            changed = onChanged;
            wiredBanner = Ui.Banner(commands.Available ? string.Empty : commands.UnavailableReason, BannerKind.Warning);
            body.Add(wiredBanner);
            outsideBanner = Ui.Banner(string.Empty, BannerKind.Info);
            body.Add(outsideBanner);

            profileField = new DropdownField("Build profile", new List<string>(), 0);
            profileField.AddToClassList("pingcore-grow");
            profileField.RegisterValueChangedCallback(e => SectionActions.Guard(buildRowStatus, () => SaveProfile(e.newValue), Changed));
            body.Add(Ui.Row(profileField, Ui.Button("Refresh", Changed)));
            profileNote = Ui.Note(string.Empty);
            body.Add(profileNote);
            BuildExecutableArea(body);
            versionField = Ui.Text("Build version", DefaultVersion());
            versionField.tooltip = DeployVersion.Rule + " It names the build's folder under Builds/Server/.";
            body.Add(Ui.Row(versionField, Ui.Button("Default", () => versionField.value = DefaultVersion())));

            BuildRows(body);
            BuildTools(body);
            RestoreRows();
            Refresh();
        }

        /// <summary>Re-reads the build profiles, the push folder, the token and the release offer.</summary>
        public void Refresh()
        {
            if (profileField == null)
            {
                return;
            }

            if (wiredBanner != null)
            {
                Ui.SetBanner(wiredBanner, commands.Available ? string.Empty : commands.UnavailableReason, BannerKind.Warning);
            }

            PushTarget target = Target();
            Ui.SetBanner(outsideBanner, target.OutsideThePlugin ? target.Line : string.Empty, BannerKind.Info);
            profiles = BuildProfiles.FindLinuxDedicatedServer();
            string chosen = WorkspaceContext.LoadProject(out _).BuildProfile;
            List<string> choices = profiles.Select(p => p.Path).ToList();
            profileField.choices = choices.Count == 0 ? new List<string> { NoProfileChoice } : choices;
            profileField.SetValueWithoutNotify(choices.Contains(chosen) ? chosen : choices.Count == 0 ? NoProfileChoice : "(pick one)");
            profileNote.text = choices.Count == 0
                ? "This project has no Linux Dedicated Server build profile. Create one in File > Build Profiles: pick Linux Server (Dedicated Server), then Add Build Profile, and set its scenes."
                : chosen != null && !choices.Contains(chosen)
                    ? $"The project names {chosen}, which is not a Linux Dedicated Server build profile here; pick one."
                    : "Build uses the profile's own scenes, scripting backend and defines.";
            RefreshExecutable();
            RefreshPush();
            RefreshRelease();
        }

        /// <summary>True when the project names a Linux Dedicated Server build profile that exists.</summary>
        public bool ProfileChosen
        {
            get
            {
                string chosen = WorkspaceContext.LoadProject(out _).BuildProfile;
                return chosen != null && BuildProfiles.FindLinuxDedicatedServer().Any(p => p.Path == chosen);
            }
        }

        private void SaveProfile(string choice)
        {
            if (profiles.All(p => p.Path != choice))
            {
                return;
            }

            EditorProjectSettings project = EditorProjectSettings.Load(WorkspaceContext.ProjectRoot);
            project.BuildProfile = choice;
            project.Save(WorkspaceContext.ProjectRoot);
        }

        // The picked fleet's facts, when they belong to the project's fleet.
        private FleetFacts Facts()
        {
            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            return connect?.Facts != null && connect.Facts.FleetId == fleetId ? connect.Facts : null;
        }

        private IPingCoreApi SignedInApi(ShipRowView row)
        {
            IPingCoreApi api = WorkspaceContext.SignedInApi(out string problem);
            if (api == null)
            {
                row.Raise(ShipRowEvent.Fail, problem);
            }

            return api;
        }

        // A window opened before sign-in picks the pipeline up on the next press, and a new sign-in (another API host or
        // key store) gets commands over its own client. Never swapped while a run goes.
        private void EnsureCommands()
        {
            string key = CommandsKey();
            if (commands != null && commands.Available && key == commandsFor)
            {
                return;
            }

            if (commands?.Events?.RunStatus == PipelineRunStatus.Running)
            {
                return;
            }

            UnbindEvents();
            commands = DeployCommandsRegistry.Create();
            commandsFor = key;
            BindEvents();
            Ui.SetBanner(wiredBanner, commands.Available ? string.Empty : commands.UnavailableReason, BannerKind.Warning);
        }

        // What the commands were built for: the API host and the key store choice.
        private static string CommandsKey()
        {
            string host = WorkspaceContext.Endpoint(out _)?.Host ?? string.Empty;
            return host + "|" + WorkspaceContext.LoadUser(out _).SessionOnlyKey;
        }

        private void OpenFleet()
        {
            long fleetId = WorkspaceContext.LoadProject(out _).FleetId;
            if (fleetId > 0)
            {
                Application.OpenURL(PanelLinks.Fleet(connectSection?.PanelBase, fleetId));
            }
        }

        private void Changed()
        {
            Refresh();
            changed?.Invoke();
        }

        private static string DefaultVersion() => DeployVersion.Default(DateTime.Now, DeployVersion.GitShortSha(WorkspaceContext.ProjectRoot));
    }
}
