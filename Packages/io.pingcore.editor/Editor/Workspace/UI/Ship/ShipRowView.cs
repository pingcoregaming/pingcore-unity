using System;
using PingCore.Editor.Workspace.UI.Common;
using UnityEditor;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>
    /// One Ship row on the page: its button, its state, the result or the API's text, Retry and Open in panel
    /// (<see cref="ShipRow"/>). The button is disabled while the row runs, and while a gate names why the row cannot
    /// run at all (<see cref="Gate"/>: Release with no deployment). The row's state is kept in
    /// <see cref="SessionState"/>, so the domain reload a build causes does not forget it; a row that was running
    /// when the reload came and is not running now reads as interrupted (<see cref="Restore"/>).
    /// </summary>
    public sealed class ShipRowView
    {
        private const string KeyPrefix = "PingCore.Ship.Row.";

        private readonly Button button;
        private readonly Label chip;
        private readonly Label text;
        private readonly Button retry;
        private readonly Button openInPanel;
        private Action lastStarted;
        private string gate;

        public ShipRowView(ShipRowKind kind, string label, Action start, Action open)
        {
            Kind = kind;
            Row = ShipRow.Idle(kind);
            Root = Ui.WithClass(new VisualElement(), "pingcore-step");
            button = Ui.Primary(Ui.Button(label, start));
            chip = Ui.WithClass(new Label(), "pingcore-step__state");
            text = Ui.WithClass(new Label(), "pingcore-grow");
            text.style.whiteSpace = WhiteSpace.Normal;
            // Retry runs again what failed on this row (a Replace push token... retries the replace, never a push); after a
            // domain reload, when nothing is remembered, it runs the row's own action.
            retry = Ui.Button("Retry", () => (lastStarted ?? start)());
            openInPanel = Ui.Button("Open in panel", open);
            Root.Add(button);
            Root.Add(chip);
            Root.Add(text);
            Root.Add(retry);
            Root.Add(openInPanel);
            Show();
        }

        public ShipRowKind Kind { get; }

        public VisualElement Root { get; }

        /// <summary>The row as last shown.</summary>
        public ShipRow Row { get; private set; }

        /// <summary>
        /// Disables the row's button with <paramref name="reason"/> as its tooltip while it is not null (Release for a fleet
        /// with no deployment); null enables it again. Retry stays: it may retry a Continue, Acknowledge or Cancel release,
        /// and a retried Release press reads the fleet again and refuses with the same reason. A running row is never
        /// interrupted by it.
        /// </summary>
        public void Gate(string reason)
        {
            if (gate == reason)
            {
                return;
            }

            gate = reason;
            Show();
        }

        /// <summary>Remembers what was started on this row, so Retry starts that again (this Editor session's window only).</summary>
        public void Started(Action again) => lastStarted = again;

        /// <summary>Applies <paramref name="e"/> through the state machine; false when the row refused it (a second Start while running).</summary>
        public bool Raise(ShipRowEvent e, string message = null)
        {
            (bool accepted, ShipRow next) = ShipRowMachine.Next(Row, e, message);
            if (accepted)
            {
                Row = next;
                Save();
                Show();
            }

            return accepted;
        }

        /// <summary>Says what a running row is doing now; nothing for a row that is not running.</summary>
        public void Running(string message)
        {
            if (Row.State == ShipRowState.Running)
            {
                Row = new ShipRow(Kind, ShipRowState.Running, message);
                Save();
                Show();
            }
        }

        /// <summary>
        /// Takes the row saved in this Editor session. A row saved as running while nothing runs now was cut off by a
        /// domain reload or a stop of the Editor, so it reads as failed with that reason.
        /// </summary>
        public void Restore(bool somethingRuns)
        {
            int saved = SessionState.GetInt(KeyPrefix + Kind + ".State", (int)ShipRowState.Idle);
            string savedText = SessionState.GetString(KeyPrefix + Kind + ".Text", string.Empty);
            var state = Enum.IsDefined(typeof(ShipRowState), saved) ? (ShipRowState)saved : ShipRowState.Idle;
            if (state == ShipRowState.Running && !somethingRuns)
            {
                state = ShipRowState.Failed;
                savedText = "Interrupted: the Editor reloaded or stopped while this ran. Press Retry.";
            }

            Row = new ShipRow(Kind, state, savedText);
            Save();
            Show();
        }

        private void Save()
        {
            SessionState.SetInt(KeyPrefix + Kind + ".State", (int)Row.State);
            SessionState.SetString(KeyPrefix + Kind + ".Text", Row.Text);
        }

        private void Show()
        {
            button.SetEnabled(Row.ButtonEnabled && gate == null);
            button.tooltip = gate ?? string.Empty;
            chip.text = Row.State.ToString().ToLowerInvariant();
            text.text = Row.Text;
            Root.EnableInClassList("pingcore-step--running", Row.State == ShipRowState.Running);
            Root.EnableInClassList("pingcore-step--failed", Row.State == ShipRowState.Failed);
            Root.EnableInClassList("pingcore-step--succeeded", Row.State == ShipRowState.Done);
            retry.style.display = Row.ShowsRetry ? DisplayStyle.Flex : DisplayStyle.None;
            openInPanel.style.display = Row.ShowsOpenInPanel ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
