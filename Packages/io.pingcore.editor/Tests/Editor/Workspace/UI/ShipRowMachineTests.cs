using NUnit.Framework;
using PingCore.Editor.Workspace.Pipeline;
using PingCore.Editor.Workspace.UI.Ship;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>
    /// The per-row state machine of Ship as a table: every state against every event, what the row shows (its
    /// button, Retry, Open in panel), and the event a pipeline outcome raises.
    /// </summary>
    public sealed class ShipRowMachineTests
    {
        private static ShipRow Row(ShipRowState state, ShipRowKind kind = ShipRowKind.Push) => new ShipRow(kind, state, state == ShipRowState.Idle ? null : "before");

        [TestCase(ShipRowState.Idle, ShipRowEvent.Start, true, ShipRowState.Running)]
        [TestCase(ShipRowState.Idle, ShipRowEvent.Succeed, false, ShipRowState.Idle)]
        [TestCase(ShipRowState.Idle, ShipRowEvent.Fail, false, ShipRowState.Idle)]
        [TestCase(ShipRowState.Idle, ShipRowEvent.Reset, true, ShipRowState.Idle)]
        [TestCase(ShipRowState.Running, ShipRowEvent.Start, false, ShipRowState.Running)]
        [TestCase(ShipRowState.Running, ShipRowEvent.Succeed, true, ShipRowState.Done)]
        [TestCase(ShipRowState.Running, ShipRowEvent.Fail, true, ShipRowState.Failed)]
        [TestCase(ShipRowState.Running, ShipRowEvent.Reset, false, ShipRowState.Running)]
        [TestCase(ShipRowState.Done, ShipRowEvent.Start, true, ShipRowState.Running)]
        [TestCase(ShipRowState.Done, ShipRowEvent.Succeed, false, ShipRowState.Done)]
        [TestCase(ShipRowState.Done, ShipRowEvent.Fail, false, ShipRowState.Done)]
        [TestCase(ShipRowState.Done, ShipRowEvent.Reset, true, ShipRowState.Idle)]
        [TestCase(ShipRowState.Failed, ShipRowEvent.Start, true, ShipRowState.Running)]
        [TestCase(ShipRowState.Failed, ShipRowEvent.Succeed, false, ShipRowState.Failed)]
        [TestCase(ShipRowState.Failed, ShipRowEvent.Fail, false, ShipRowState.Failed)]
        [TestCase(ShipRowState.Failed, ShipRowEvent.Reset, true, ShipRowState.Idle)]
        public void EveryStateAndEvent(ShipRowState from, ShipRowEvent e, bool accepted, ShipRowState to)
        {
            ShipRow before = Row(from);
            (bool took, ShipRow after) = ShipRowMachine.Next(before, e, "after");
            Assert.That(took, Is.EqualTo(accepted));
            Assert.That(after.State, Is.EqualTo(to));
            Assert.That(after.Kind, Is.EqualTo(before.Kind));
            if (!accepted)
            {
                Assert.That(after, Is.SameAs(before), "a refused event changes nothing, its text included");
            }
            else if (e != ShipRowEvent.Reset)
            {
                Assert.That(after.Text, Is.EqualTo("after"), "the result, the API's text or what runs");
            }
            else
            {
                Assert.That(after.Text, Is.Empty, "a reset row says nothing");
            }
        }

        [TestCase(ShipRowState.Idle, true, false, false)]
        [TestCase(ShipRowState.Running, false, false, false)]
        [TestCase(ShipRowState.Done, true, false, false)]
        [TestCase(ShipRowState.Failed, true, true, true)]
        public void TheButtonIsDisabledOnlyWhileRunningAndAFailureOffersRetryAndThePanel(ShipRowState state, bool button, bool retry, bool panel)
        {
            ShipRow row = Row(state);
            Assert.That((row.ButtonEnabled, row.ShowsRetry, row.ShowsOpenInPanel), Is.EqualTo((button, retry, panel)));
        }

        [Test]
        public void AFailedBuildOffersRetryButNoPanelPage()
        {
            ShipRow build = Row(ShipRowState.Failed, ShipRowKind.Build);
            Assert.That((build.ShowsRetry, build.ShowsOpenInPanel), Is.EqualTo((true, false)), "a build is local; there is nothing in the panel to open");
            Assert.That(Row(ShipRowState.Failed, ShipRowKind.Release).ShowsOpenInPanel, Is.True);
        }

        [Test]
        public void RetryIsAStartFromFailedAndASecondStartWhileRunningIsRefused()
        {
            (bool _, ShipRow failed) = ShipRowMachine.Next(new ShipRow(ShipRowKind.Release, ShipRowState.Running, null), ShipRowEvent.Fail, "release_in_progress");
            (bool retried, ShipRow running) = ShipRowMachine.Next(failed, ShipRowEvent.Start, "Starting...");
            Assert.That((retried, running.State), Is.EqualTo((true, ShipRowState.Running)));
            Assert.That(ShipRowMachine.Next(running, ShipRowEvent.Start, "again").Accepted, Is.False, "[mutation: let a row run twice at once]");
        }

        [TestCase(PipelineRunStatus.Succeeded, false, ShipRowEvent.Succeed)]
        [TestCase(PipelineRunStatus.Succeeded, true, ShipRowEvent.Fail)]
        [TestCase(PipelineRunStatus.Failed, false, ShipRowEvent.Fail)]
        [TestCase(PipelineRunStatus.Cancelled, false, ShipRowEvent.Fail)]
        [TestCase(PipelineRunStatus.NeedsInput, false, ShipRowEvent.Fail)]
        [TestCase(PipelineRunStatus.CanResume, false, ShipRowEvent.Fail)]
        [TestCase(PipelineRunStatus.Idle, true, ShipRowEvent.Fail)]
        public void APipelineOutcomeRaisesSucceedOnlyForASuccess(PipelineRunStatus status, bool refused, ShipRowEvent expected)
        {
            Assert.That(ShipRowMachine.EventOf(new PipelineOutcome(status, null, "m", refused)), Is.EqualTo(expected));
            Assert.That(ShipRowMachine.EventOf(null), Is.EqualTo(ShipRowEvent.Fail));
        }
    }
}
