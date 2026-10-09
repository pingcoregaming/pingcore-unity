using System;
using System.Collections.Generic;
using PingCore.Editor.Workspace.Confirmation;
using FleetText = PingCore.Editor.Workspace.UI.Connect.FleetResolution;

namespace PingCore.Editor.Workspace.UI
{
    /// <summary>The four sections of Window > PingCore, top to bottom.</summary>
    public enum Section
    {
        Connect,
        Ship,
        Status,
        PlayerHosting,
    }

    /// <summary>A section's status chip.</summary>
    public enum SectionStatus
    {
        Done,
        ToDo,

        /// <summary>Everything is in place; the section's actions are repeated work (Ship).</summary>
        Ready,

        /// <summary>Connect must be done first.</summary>
        Blocked,

        /// <summary>The branch Push pushes to is not on a CDN source: its builds are pushed and released outside the plugin.</summary>
        Unavailable,

        /// <summary>An optional section nothing is set in (Player hosting).</summary>
        Off,
    }

    /// <summary>What the window knows when it works the sections out. Gathered by the window; never a secret.</summary>
    public sealed class SectionFacts
    {
        /// <summary>A key is stored for the endpoint (and the endpoint is usable) and the workspace has not refused it.</summary>
        public bool SignedIn { get; set; }

        /// <summary>"Signed in to Studio as dev@studio.test".</summary>
        public string SignedInLine { get; set; }

        /// <summary>How many fleets <c>GET fleets</c> listed, or -1 before it answered.</summary>
        public int FleetCount { get; set; } = -1;

        /// <summary>The fleet the project names, or 0.</summary>
        public long FleetId { get; set; }

        public string FleetName { get; set; }

        /// <summary>The fleet's details were read in this window.</summary>
        public bool FleetResolved { get; set; }

        public int DeploymentCount { get; set; }

        /// <summary>Why Push cannot run for the branch it would push to (no branch picked, a branch not on a CDN source, the game not read), or null.</summary>
        public string PushProblem { get; set; }

        /// <summary>The game's branches must be picked from (several, none remembered).</summary>
        public bool BranchToPick { get; set; }

        /// <summary>How many branches the fleet's game has (0 when not read).</summary>
        public int BranchCount { get; set; }

        /// <summary>Why Release cannot run (no deployment, members on different CDN sources), or null. Never stops Build or Push.</summary>
        public string ReleaseProblem { get; set; }

        /// <summary>The branch Push would push to gets its files from an image or Steam.</summary>
        public bool OutsideThePlugin { get; set; }

        /// <summary>The project names a Linux Dedicated Server build profile.</summary>
        public bool BuildProfileChosen { get; set; }

        /// <summary>A pingctl may run (the bundled one passed its check, or the developer's own exists).</summary>
        public bool PingctlFound { get; set; }

        /// <summary>Why no pingctl may run, or null.</summary>
        public string PingctlProblem { get; set; }

        /// <summary>The client settings asset names a community app.</summary>
        public bool CommunityAppSet { get; set; }

        /// <summary>The asset's heartbeat token against the guard's confirmations.</summary>
        public ConfirmationState HeartbeatToken { get; set; } = ConfirmationState.NoToken;
    }

    /// <summary>One section as the window shows it: header, chip, the line under it, and the one next action.</summary>
    public sealed class SectionState
    {
        public SectionState(Section section, SectionStatus status, string line, string nextAction)
        {
            Section = section;
            Status = status;
            Line = line;
            NextAction = nextAction;
        }

        public Section Section { get; }

        public string Header => SectionBook.Title(Section);

        public SectionStatus Status { get; }

        /// <summary>The chip's text.</summary>
        public string Chip
        {
            get
            {
                switch (Status)
                {
                    case SectionStatus.Done:
                        return "done";
                    case SectionStatus.ToDo:
                        return "to do";
                    case SectionStatus.Ready:
                        return "ready";
                    case SectionStatus.Blocked:
                        return "connect first";
                    case SectionStatus.Unavailable:
                        return "outside the plugin";
                    default:
                        return "off";
                }
            }
        }

        /// <summary>Where the section stands, one sentence.</summary>
        public string Line { get; }

        /// <summary>The one thing to do next, or null when nothing is asked.</summary>
        public string NextAction { get; }

        /// <summary>A blocked section is folded and its controls disabled.</summary>
        public bool Enabled => Status != SectionStatus.Blocked;

        public override string ToString() => $"{Header} [{Chip}] {Line}" + (NextAction == null ? string.Empty : " Next: " + NextAction);
    }

    /// <summary>
    /// The section model of Window > PingCore, pure: each section's title, the line that says what it is, and,
    /// from <see cref="SectionFacts"/>, its status, the sentence under it and its one next action. Ship and Status
    /// need Connect (signed in, a fleet picked); Player hosting is an optional fold, off by default.
    /// </summary>
    public static class SectionBook
    {
        /// <summary>The line Ship and Status show before Connect is done.</summary>
        public const string ConnectFirst = "Sign in and pick a fleet under Connect first.";

        /// <summary>Ship's next action when Release cannot run because the fleet has no deployment.</summary>
        public const string ReleaseWaitsForDeployment = "Build, then Push. Release waits for a deployment; see Release below.";

        /// <summary>Ship's next action when Release cannot run for another reason (members on different CDN sources, an unread build target).</summary>
        public const string ReleaseCannotRunYet = "Build, then Push. Release cannot run yet; see Release below.";

        /// <summary>Status's next action for a fleet with no deployment.</summary>
        public const string AddDeployment = "Add a deployment in the panel.";

        /// <summary>Ship's line while the picked fleet is not read yet.</summary>
        public const string NotReadYet = "The fleet's game is not read yet, so the branch and CDN source to push to are not known.";

        /// <summary>Every section in order.</summary>
        public static IReadOnlyList<Section> All { get; } = new[] { Section.Connect, Section.Ship, Section.Status, Section.PlayerHosting };

        public static string Title(Section section)
        {
            switch (section)
            {
                case Section.Connect:
                    return "Connect";
                case Section.Ship:
                    return "Ship";
                case Section.Status:
                    return "Status";
                case Section.PlayerHosting:
                    return "Player hosting";
                default:
                    throw new ArgumentOutOfRangeException(nameof(section), section, null);
            }
        }

        /// <summary>The one line under the header that says what the section is.</summary>
        public static string What(Section section)
        {
            switch (section)
            {
                case Section.Connect:
                    return "Sign in to your PingCore workspace and pick the fleet this project ships to. Games and fleets are set up in the panel or through MCP, never here.";
                case Section.Ship:
                    return "Build the Linux dedicated server with your build profile, push it to your game branch's CDN source, and release it to the fleet.";
                case Section.Status:
                    return "The fleet's live state and its deployments, read only. Deployments are added in the panel.";
                case Section.PlayerHosting:
                    return "Only for games whose players host their own game servers: the community app and its heartbeat token.";
                default:
                    throw new ArgumentOutOfRangeException(nameof(section), section, null);
            }
        }

        /// <summary>Every section's state for <paramref name="facts"/>, in order.</summary>
        public static IReadOnlyList<SectionState> Evaluate(SectionFacts facts)
        {
            facts = facts ?? new SectionFacts();
            return new[] { Connect(facts), Ship(facts), Status(facts), PlayerHosting(facts) };
        }

        private static bool Connected(SectionFacts f) => f.SignedIn && f.FleetId > 0;

        private static SectionState Connect(SectionFacts f)
        {
            if (!f.SignedIn)
            {
                return new SectionState(Section.Connect, SectionStatus.ToDo, "Not signed in.", "Paste a brand member's usr_ key and press Sign in.");
            }

            if (f.FleetId <= 0)
            {
                return f.FleetCount == 0
                    ? new SectionState(Section.Connect, SectionStatus.ToDo, FleetText.NoFleetMessage, "Open Fleets in the panel, then press Refresh.")
                    : new SectionState(Section.Connect, SectionStatus.ToDo, "No fleet is picked for this project yet.", "Pick the fleet this project ships to.");
            }

            // Done once signed in with a fleet picked: a fleet with no deployment is Status's
            // to say, and Release's to refuse; it never makes Connect look unfinished.
            return new SectionState(Section.Connect, SectionStatus.Done, $"This project ships to {f.FleetName ?? "fleet #" + f.FleetId}.", null);
        }

        private static SectionState Ship(SectionFacts f)
        {
            if (!Connected(f))
            {
                return new SectionState(Section.Ship, SectionStatus.Blocked, ConnectFirst, null);
            }

            if (f.OutsideThePlugin)
            {
                return new SectionState(Section.Ship, SectionStatus.Unavailable, f.PushProblem, f.BranchCount > 1 ? "Pick another branch under Ship, Push to branch." : null);
            }

            if (!f.BuildProfileChosen)
            {
                return new SectionState(Section.Ship, SectionStatus.ToDo, "No Linux Dedicated Server build profile is picked.", "Pick the build profile (create one in File > Build Profiles first if the list is empty).");
            }

            if (!f.PingctlFound)
            {
                return new SectionState(Section.Ship, SectionStatus.ToDo, f.PingctlProblem ?? "No pingctl may run on this PC.", "Reinstall the Editor package, or set your own pingctl.");
            }

            if (!f.FleetResolved)
            {
                return new SectionState(Section.Ship, SectionStatus.ToDo, NotReadYet, "Press Refresh under Connect.");
            }

            if (f.PushProblem != null)
            {
                return new SectionState(Section.Ship, SectionStatus.ToDo, f.PushProblem, f.BranchToPick ? "Pick the branch under Ship, Push to branch." : "Fix it in the panel, then press Refresh under Connect.");
            }

            // Release alone needs deployments: its problem never holds Build or Push back.
            return new SectionState(Section.Ship, SectionStatus.Ready, $"Ready to ship to {f.FleetName ?? "fleet #" + f.FleetId}.",
                f.ReleaseProblem == null ? "Build, then Push, then Release." : f.DeploymentCount == 0 ? ReleaseWaitsForDeployment : ReleaseCannotRunYet);
        }

        private static SectionState Status(SectionFacts f)
        {
            if (!Connected(f))
            {
                return new SectionState(Section.Status, SectionStatus.Blocked, ConnectFirst, null);
            }

            // The no-deployment state lives here: nothing runs, so there is nothing to show until a deployment is added.
            return f.FleetResolved && f.DeploymentCount == 0
                ? new SectionState(Section.Status, SectionStatus.ToDo, FleetText.NoDeploymentMessage, AddDeployment)
                : new SectionState(Section.Status, SectionStatus.Done, $"What {f.FleetName ?? "fleet #" + f.FleetId} runs now, read only.", null);
        }

        private static SectionState PlayerHosting(SectionFacts f)
        {
            if (f.HeartbeatToken == ConfirmationState.Unconfirmed)
            {
                return new SectionState(Section.PlayerHosting, SectionStatus.ToDo, "A community heartbeat token is set but not confirmed for the asset's community app: player builds with it must not ship.", "Press Confirm for build, or clear the token.");
            }

            if (f.HeartbeatToken == ConfirmationState.Confirmed)
            {
                return new SectionState(Section.PlayerHosting, SectionStatus.Done, "Player builds name the community app and carry its confirmed heartbeat token.", null);
            }

            return f.CommunityAppSet
                ? new SectionState(Section.PlayerHosting, SectionStatus.Done, "Player builds name the community app; listen hosts take their token from PINGCORE_DISCOVERY_TOKEN.", null)
                : new SectionState(Section.PlayerHosting, SectionStatus.Off, "Off: no community app is set, so player builds cannot host.", null);
        }
    }
}
