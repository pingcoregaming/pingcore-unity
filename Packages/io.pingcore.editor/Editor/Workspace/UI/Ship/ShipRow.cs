using System;
using PingCore.Editor.Workspace.Pipeline;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>The three rows of Ship.</summary>
    public enum ShipRowKind
    {
        Build,
        Push,
        Release,
    }

    /// <summary>Where a row stands: the hosting spec's four states.</summary>
    public enum ShipRowState
    {
        Idle,
        Running,

        /// <summary>Finished, with its result (<see cref="ShipRow.Text"/>).</summary>
        Done,

        /// <summary>Stopped, with the API's own text (<see cref="ShipRow.Text"/>), Retry and Open in panel.</summary>
        Failed,
    }

    /// <summary>What happened to a row.</summary>
    public enum ShipRowEvent
    {
        /// <summary>Its button (or Retry) was pressed.</summary>
        Start,

        /// <summary>Its run succeeded; the text is the result.</summary>
        Succeed,

        /// <summary>Its run failed or was stopped; the text says why, in the API's words.</summary>
        Fail,

        /// <summary>The fleet changed, so the row's last result no longer applies.</summary>
        Reset,
    }

    /// <summary>One row of Ship as the window shows it. Immutable.</summary>
    public sealed class ShipRow
    {
        public ShipRow(ShipRowKind kind, ShipRowState state, string text)
        {
            Kind = kind;
            State = state;
            Text = text ?? string.Empty;
        }

        public ShipRowKind Kind { get; }

        public ShipRowState State { get; }

        /// <summary>The result when done, the reason when failed, what runs while running; empty when idle.</summary>
        public string Text { get; }

        /// <summary>The row's button is disabled only while it runs.</summary>
        public bool ButtonEnabled => State != ShipRowState.Running;

        /// <summary>Retry shows on a failed row.</summary>
        public bool ShowsRetry => State == ShipRowState.Failed;

        /// <summary>Open in panel shows on a failed push or release (the fleet's page); a build is local, with nothing in the panel to open.</summary>
        public bool ShowsOpenInPanel => State == ShipRowState.Failed && Kind != ShipRowKind.Build;

        public static ShipRow Idle(ShipRowKind kind) => new ShipRow(kind, ShipRowState.Idle, null);

        public override string ToString() => $"{Kind}: {State}" + (Text.Length == 0 ? string.Empty : " - " + Text);
    }

    /// <summary>
    /// The per-row state machine of Ship, pure. Idle or Done or Failed go to Running on Start (Retry is a Start);
    /// Running goes to Done on Succeed and to Failed on Fail; a Start while Running is refused, so a row never runs
    /// twice at once; Reset returns a row that is not running to Idle; anything else leaves the row as it is and is
    /// not accepted.
    /// </summary>
    public static class ShipRowMachine
    {
        /// <summary>The row after <paramref name="e"/>, and whether the event was accepted.</summary>
        public static (bool Accepted, ShipRow Row) Next(ShipRow row, ShipRowEvent e, string text = null)
        {
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row));
            }

            switch (e)
            {
                case ShipRowEvent.Start:
                    return row.State == ShipRowState.Running
                        ? (false, row)
                        : (true, new ShipRow(row.Kind, ShipRowState.Running, text));
                case ShipRowEvent.Succeed:
                    return row.State == ShipRowState.Running
                        ? (true, new ShipRow(row.Kind, ShipRowState.Done, text))
                        : (false, row);
                case ShipRowEvent.Fail:
                    return row.State == ShipRowState.Running
                        ? (true, new ShipRow(row.Kind, ShipRowState.Failed, text))
                        : (false, row);
                case ShipRowEvent.Reset:
                    return row.State == ShipRowState.Running
                        ? (false, row)
                        : (true, ShipRow.Idle(row.Kind));
                default:
                    throw new ArgumentOutOfRangeException(nameof(e), e, null);
            }
        }

        /// <summary>
        /// The event a finished pipeline call raises on its row: a success is Succeed; a failure, a stop, a refusal, a
        /// wait for input or a saved run left to continue is Fail with the outcome's sentence. Pure.
        /// </summary>
        public static ShipRowEvent EventOf(PipelineOutcome outcome)
        {
            return outcome != null && outcome.Status == PipelineRunStatus.Succeeded && !outcome.Refused
                ? ShipRowEvent.Succeed
                : ShipRowEvent.Fail;
        }
    }
}
