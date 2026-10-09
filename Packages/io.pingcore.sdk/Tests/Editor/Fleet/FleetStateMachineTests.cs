using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>The lifecycle table, row by row, as a pure function.</summary>
    public sealed class FleetStateMachineTests
    {
        private static readonly FleetState[] All = { FleetState.Inert, FleetState.Starting, FleetState.NotReady, FleetState.Ready, FleetState.InSession, FleetState.ShuttingDown, FleetState.Stopping, FleetState.Unreachable };

        private static FleetInput Kind(FleetInputKind kind) => FleetInput.Of(kind);

        private static FleetInput Frame(string agones, bool allocation = false) => new FleetInput(FleetInputKind.Frame, agones, allocation);

        private static FleetInput View(string agones, bool allocation = false) => new FleetInput(FleetInputKind.FirstView, agones, allocation);

        private static IEnumerable<FleetInput> EveryInput()
        {
            yield return Kind(FleetInputKind.Start);
            yield return Kind(FleetInputKind.StartFailed);
            yield return Kind(FleetInputKind.ReadyAccepted);
            yield return Kind(FleetInputKind.ShutdownAccepted);
            yield return Kind(FleetInputKind.ProcessStopping);
            yield return Kind(FleetInputKind.WatchLost);
            foreach (string agones in new[] { "Scheduled", "Ready", "Allocated", "Shutdown", null })
            {
                yield return View(agones);
                yield return View(agones, true);
                yield return Frame(agones);
                yield return Frame(agones, true);
            }
        }

        [Test]
        public void InertAndStoppingAreTerminalForEveryInput()
        {
            foreach (FleetInput input in EveryInput())
            {
                Assert.That(FleetStateMachine.Next(FleetState.Inert, input), Is.EqualTo(FleetState.Inert), input.ToString());
                Assert.That(FleetStateMachine.Next(FleetState.Stopping, input), Is.EqualTo(FleetState.Stopping), input.ToString());
            }
        }

        [Test]
        public void ProcessStoppingMovesEveryNonInertStateToStopping()
        {
            foreach (FleetState state in All.Where(s => s != FleetState.Inert))
            {
                Assert.That(FleetStateMachine.Next(state, Kind(FleetInputKind.ProcessStopping)), Is.EqualTo(FleetState.Stopping), state.ToString());
            }
        }

        [Test]
        public void StartEntersStartingFromStartingOrUnreachableAndChangesNothingElse()
        {
            Assert.That(FleetStateMachine.Next(FleetState.Starting, Kind(FleetInputKind.Start)), Is.EqualTo(FleetState.Starting));
            Assert.That(FleetStateMachine.Next(FleetState.Unreachable, Kind(FleetInputKind.Start)), Is.EqualTo(FleetState.Starting));
            foreach (FleetState state in new[] { FleetState.NotReady, FleetState.Ready, FleetState.InSession, FleetState.ShuttingDown })
            {
                Assert.That(FleetStateMachine.Next(state, Kind(FleetInputKind.Start)), Is.EqualTo(state), state.ToString());
            }
        }

        [Test]
        public void TheFirstViewMapsTheAgonesStateAndAnAllocationWins()
        {
            Assert.That(FleetStateMachine.Next(FleetState.Starting, View("Scheduled")), Is.EqualTo(FleetState.NotReady));
            Assert.That(FleetStateMachine.Next(FleetState.Starting, View(null)), Is.EqualTo(FleetState.NotReady));
            Assert.That(FleetStateMachine.Next(FleetState.Starting, View("Ready")), Is.EqualTo(FleetState.Ready));
            Assert.That(FleetStateMachine.Next(FleetState.Starting, View("Allocated")), Is.EqualTo(FleetState.InSession));
            Assert.That(FleetStateMachine.Next(FleetState.Starting, View("Ready", true)), Is.EqualTo(FleetState.InSession));
            Assert.That(FleetStateMachine.Next(FleetState.Starting, View("Shutdown", true)), Is.EqualTo(FleetState.ShuttingDown));
            Assert.That(FleetStateMachine.Next(FleetState.Ready, View("Scheduled")), Is.EqualTo(FleetState.Ready), "only Starting reads a first view");
        }

        [Test]
        public void ThirtyFailedReadsMakeStartingUnreachable()
        {
            Assert.That(FleetStateMachine.Next(FleetState.Starting, Kind(FleetInputKind.StartFailed)), Is.EqualTo(FleetState.Unreachable));
            Assert.That(FleetStateMachine.Next(FleetState.Ready, Kind(FleetInputKind.StartFailed)), Is.EqualTo(FleetState.Ready));
        }

        [Test]
        public void ReadyMovesOnlyNotReadyToReady()
        {
            Assert.That(FleetStateMachine.Next(FleetState.NotReady, Kind(FleetInputKind.ReadyAccepted)), Is.EqualTo(FleetState.Ready));
            foreach (FleetState state in new[] { FleetState.Starting, FleetState.Ready, FleetState.InSession, FleetState.ShuttingDown, FleetState.Unreachable })
            {
                Assert.That(FleetStateMachine.Next(state, Kind(FleetInputKind.ReadyAccepted)), Is.EqualTo(state), state.ToString());
            }
        }

        [Test]
        public void ANewAllocationOnAFrameMovesReadyToInSession()
        {
            Assert.That(FleetStateMachine.Next(FleetState.Ready, Frame("Allocated", true)), Is.EqualTo(FleetState.InSession));
            Assert.That(FleetStateMachine.Next(FleetState.Ready, Frame("Ready", true)), Is.EqualTo(FleetState.InSession), "the annotation alone is enough");
            Assert.That(FleetStateMachine.Next(FleetState.NotReady, Frame("Allocated", true)), Is.EqualTo(FleetState.InSession), "a self-allocation before Ready");
        }

        [Test]
        public void AFrameWithoutTheAllocationMovesInSessionBackToReady()
        {
            Assert.That(FleetStateMachine.Next(FleetState.InSession, Frame("Ready")), Is.EqualTo(FleetState.Ready));
            Assert.That(FleetStateMachine.Next(FleetState.InSession, Frame("Scheduled")), Is.EqualTo(FleetState.NotReady), "the view says the game never called Ready");
        }

        [Test]
        public void AStaleScheduledFrameNeverUnreadiesReady()
        {
            Assert.That(FleetStateMachine.Next(FleetState.Ready, Frame("Scheduled")), Is.EqualTo(FleetState.Ready));
            Assert.That(FleetStateMachine.Next(FleetState.NotReady, Frame("Scheduled")), Is.EqualTo(FleetState.NotReady));
            Assert.That(FleetStateMachine.Next(FleetState.NotReady, Frame("Ready")), Is.EqualTo(FleetState.Ready));
        }

        [Test]
        public void ShutdownByCallOrFrameMovesEveryLiveStateToShuttingDownAndOnlyStoppingLeavesIt()
        {
            foreach (FleetState state in new[] { FleetState.Starting, FleetState.NotReady, FleetState.Ready, FleetState.InSession, FleetState.Unreachable })
            {
                Assert.That(FleetStateMachine.Next(state, Kind(FleetInputKind.ShutdownAccepted)), Is.EqualTo(FleetState.ShuttingDown), state.ToString());
            }

            foreach (FleetState state in new[] { FleetState.NotReady, FleetState.Ready, FleetState.InSession, FleetState.Unreachable })
            {
                Assert.That(FleetStateMachine.Next(state, Frame("Shutdown")), Is.EqualTo(FleetState.ShuttingDown), state.ToString());
            }

            foreach (FleetInput input in EveryInput().Where(i => i.Kind != FleetInputKind.ProcessStopping))
            {
                Assert.That(FleetStateMachine.Next(FleetState.ShuttingDown, input), Is.EqualTo(FleetState.ShuttingDown), input.ToString());
            }
        }

        [Test]
        public void ALostWatchMakesReadyOrInSessionUnreachableAndTheNextFrameRestoresTheViewState()
        {
            Assert.That(FleetStateMachine.Next(FleetState.Ready, Kind(FleetInputKind.WatchLost)), Is.EqualTo(FleetState.Unreachable));
            Assert.That(FleetStateMachine.Next(FleetState.InSession, Kind(FleetInputKind.WatchLost)), Is.EqualTo(FleetState.Unreachable));
            Assert.That(FleetStateMachine.Next(FleetState.NotReady, Kind(FleetInputKind.WatchLost)), Is.EqualTo(FleetState.NotReady));

            Assert.That(FleetStateMachine.Next(FleetState.Unreachable, Frame("Ready")), Is.EqualTo(FleetState.Ready));
            Assert.That(FleetStateMachine.Next(FleetState.Unreachable, Frame("Allocated", true)), Is.EqualTo(FleetState.InSession));
            Assert.That(FleetStateMachine.Next(FleetState.Unreachable, Frame("Scheduled")), Is.EqualTo(FleetState.NotReady));
        }

        [Test]
        public void TheTableNeverProducesInertFromAHostedState()
        {
            foreach (FleetState state in All.Where(s => s != FleetState.Inert))
            {
                foreach (FleetInput input in EveryInput())
                {
                    Assert.That(FleetStateMachine.Next(state, input), Is.Not.EqualTo(FleetState.Inert), state + " + " + input);
                }
            }
        }
    }
}
