using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PingCore.Discovery.Client;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.Infrastructure;
using PingCore.Editor.Workspace.Settings;
using PingCore.Editor.Workspace.Tests.Fakes;

namespace PingCore.Editor.Workspace.Tests.Infrastructure
{
    /// <summary>
    /// The missing-infrastructure answer runs on a thread-pool thread, so its main-thread-only reads (the store choice,
    /// <c>EditorPrefs</c>, <c>SessionState</c>) go through <see cref="MainThreadCalls"/>: run on the thread that drains,
    /// bounded, never run once the waiter gave up. A stand-in main thread drains here; the Editor's is
    /// <c>EditorApplication.update</c>. Then the hook's answer end to end from a pool thread against a scratch project.
    /// </summary>
    public sealed class MainThreadCallsTests
    {
        private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

        private static string FakeKey() => "usr" + "_" + new string('k', 32);

        [Test]
        public void ACallFromAnotherThreadRunsOnTheThreadThatDrainsAndReturnsItsResult()
        {
            using (var main = new StandInMainThread())
            {
                Assert.That(main.Calls.OnMainThread, Is.False, "the test thread is not the stand-in main thread");
                Assert.That(main.Calls.Run(() => Thread.CurrentThread.ManagedThreadId, TimeSpan.FromSeconds(5)), Is.EqualTo(main.Id));
            }
        }

        [Test]
        public void OnTheMainThreadACallRunsInlineWithoutADrain()
        {
            var calls = new MainThreadCalls(Thread.CurrentThread.ManagedThreadId);
            Assert.That(calls.Run(() => 42, Short), Is.EqualTo(42));
        }

        [Test]
        public void ACallsExceptionReachesTheCallerWithItsType()
        {
            using (var main = new StandInMainThread())
            {
                Assert.That(() => main.Calls.Run<bool>(() => throw new CredentialStoreException("store failed"), TimeSpan.FromSeconds(5)),
                    Throws.TypeOf<CredentialStoreException>().With.Message.EqualTo("store failed"));
            }
        }

        [Test]
        public void AMainThreadThatNeverDrainsTimesOutWithinTheBoundAndTheAbandonedCallNeverRuns()
        {
            using (var main = new StandInMainThread(drains: false))
            {
                int ran = 0;
                var clock = Stopwatch.StartNew();
                Assert.That(() => main.Calls.Run(() => Interlocked.Increment(ref ran), Short), Throws.TypeOf<TimeoutException>());
                Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "bounded [mutation: wait without a bound]");

                main.Drains = true;
                main.WaitForDrain();
                Assert.That(Volatile.Read(ref ran), Is.EqualTo(0), "a call the waiter gave up on is never run later");
            }
        }

        [Test]
        public void OnlyTheStoresOnUnityApisAreWrappedAndTheirCallsRunOnTheMainThread()
        {
            using (var main = new StandInMainThread())
            {
                var windows = new FakeCredentialStore(CredentialStoreKind.WindowsCredentialManager);
                var fake = new FakeCredentialStore();
                Assert.That(MainThreadCredentialStore.Wrap(windows, main.Calls, Short), Is.SameAs(windows), "Windows Credential Manager is P/Invoke, safe on any thread");
                Assert.That(MainThreadCredentialStore.Wrap(fake, main.Calls, Short), Is.SameAs(fake));
                Assert.That(MainThreadCredentialStore.Wrap(null, main.Calls, Short), Is.Null);

                foreach (CredentialStoreKind kind in new[] { CredentialStoreKind.EditorPrefs, CredentialStoreKind.Session })
                {
                    var inner = new ThreadRecordingStore(kind);
                    inner.Seed(CredentialTargets.UserKey("app.pingcore.io"), FakeKey());
                    ICredentialStore wrapped = MainThreadCredentialStore.Wrap(inner, main.Calls, TimeSpan.FromSeconds(5));
                    Assert.That(wrapped, Is.Not.SameAs(inner), kind.ToString());
                    Assert.That(wrapped.Kind, Is.EqualTo(kind));
                    Assert.That(wrapped.Exists(CredentialTargets.UserKey("app.pingcore.io")), Is.True);
                    Assert.That(wrapped.Read(CredentialTargets.UserKey("app.pingcore.io")).Secret, Is.EqualTo(FakeKey()));
                    Assert.That(inner.Threads, Is.EqualTo(new[] { main.Id, main.Id }), $"{kind}: every call ran on the main thread");
                }
            }
        }

        [Test]
        public void AStoreReadTheMainThreadNeverRunsFailsAsAStoreErrorSoTheAnswerReadsNotSignedIn()
        {
            using (var main = new StandInMainThread(drains: false))
            {
                var inner = new ThreadRecordingStore(CredentialStoreKind.EditorPrefs);
                inner.Seed(CredentialTargets.UserKey("app.pingcore.io"), FakeKey());
                ICredentialStore wrapped = MainThreadCredentialStore.Wrap(inner, main.Calls, Short);
                Assert.That(() => wrapped.Exists(CredentialTargets.UserKey("app.pingcore.io")), Throws.TypeOf<CredentialStoreException>());
                Assert.That(global::PingCore.Editor.Workspace.UI.Common.WorkspaceContext.HasKey(wrapped, "app.pingcore.io"), Is.False);
                Assert.That(inner.Threads, Is.Empty);
            }
        }

        [Test]
        public async Task TheHooksAnswerFromAPoolThreadReadsTheStoreOnlyOnTheMainThreadAndSendsTheReadOnlyFleetGet()
        {
            string root = ScratchProject(fleetId: 1);
            try
            {
                using (var main = new StandInMainThread())
                {
                    var store = new ThreadRecordingStore(CredentialStoreKind.EditorPrefs);
                    store.Seed(CredentialTargets.UserKey("app.pingcore.io"), FakeKey());
                    var chosenOn = new List<int>();
                    var transport = new FakeHttpTransport().Answer(200, WorkspaceFixtures.Envelope("fleets.get.json"));
                    var question = new InfrastructureQuestion(InfrastructureState.AppUnknown, "dscp_" + new string('0', 32), "message");

                    string detail = await Task.Run(() => InfrastructureDetailHook.AnswerAsync(
                        root,
                        main.Calls,
                        sessionOnly =>
                        {
                            chosenOn.Add(Thread.CurrentThread.ManagedThreadId);
                            return store;
                        },
                        (endpoint, keys) => new PingCoreApiClient(endpoint, keys, transport),
                        question,
                        CancellationToken.None));

                    Assert.That(detail, Does.Contain("is not the Discovery app of Fleet EU Matchmaking"), "the answer came from the fleet read");
                    Assert.That(chosenOn, Is.EqualTo(new[] { main.Id }), "the store was chosen on the main thread");
                    Assert.That(store.Operations, Is.EqualTo(new[] { "exists PingCore/app.pingcore.io/usr", "read PingCore/app.pingcore.io/usr" }));
                    Assert.That(store.Threads, Is.EqualTo(new[] { main.Id, main.Id }), "Exists and the client's Read ran on the main thread [mutation: read EditorPrefs from the pool thread]");
                    Assert.That(transport.Requests.Count, Is.EqualTo(1));
                    Assert.That(transport.Requests[0].Method + " " + transport.Requests[0].Url, Does.EndWith("/api/fleets/1"));
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public async Task WhenTheMainThreadNeverAnswersTheHookGivesNoDetailWithinItsBoundAndNeverChoosesAStore()
        {
            string root = ScratchProject(fleetId: 1);
            try
            {
                using (var main = new StandInMainThread(drains: false))
                {
                    int chosen = 0;
                    var transport = new FakeHttpTransport();
                    var clock = Stopwatch.StartNew();
                    string detail = await Task.Run(() => InfrastructureDetailHook.AnswerAsync(
                        root,
                        main.Calls,
                        sessionOnly =>
                        {
                            Interlocked.Increment(ref chosen);
                            return new FakeCredentialStore();
                        },
                        (endpoint, keys) => new PingCoreApiClient(endpoint, keys, transport),
                        new InfrastructureQuestion(InfrastructureState.NoAppId, null, "message"),
                        CancellationToken.None));

                    Assert.That(detail, Is.Null);
                    Assert.That(clock.Elapsed, Is.LessThan(InfrastructureDetailHook.MainThreadBound + InfrastructureDetailHook.MainThreadBound + TimeSpan.FromSeconds(2)));
                    main.Drains = true;
                    main.WaitForDrain();
                    Assert.That(Volatile.Read(ref chosen), Is.EqualTo(0), "the abandoned store choice never ran");
                    Assert.That(transport.Requests, Is.Empty);
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public async Task NoFleetPickedAnswersNullWithoutTouchingTheMainThreadOrTheStore()
        {
            string root = ScratchProject(fleetId: 0);
            try
            {
                using (var main = new StandInMainThread(drains: false))
                {
                    int chosen = 0;
                    string detail = await Task.Run(() => InfrastructureDetailHook.AnswerAsync(
                        root, main.Calls, s => { chosen++; return null; }, (e, k) => null,
                        new InfrastructureQuestion(InfrastructureState.NoAppId, null, "message"), CancellationToken.None));
                    Assert.That(detail, Is.Null);
                    Assert.That(chosen, Is.EqualTo(0));
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static string ScratchProject(long fleetId)
        {
            string root = Path.Combine(Path.GetTempPath(), "pingcore-hook-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            new EditorProjectSettings { FleetId = fleetId }.Save(root);
            return root;
        }

        /// <summary>A thread that stands in for the Editor's main thread: it drains its <see cref="MainThreadCalls"/> while <see cref="Drains"/> is set.</summary>
        private sealed class StandInMainThread : IDisposable
        {
            private readonly Thread thread;
            private readonly ManualResetEventSlim started = new ManualResetEventSlim(false);
            private volatile bool stop;
            private volatile int drained;

            public StandInMainThread(bool drains = true)
            {
                Drains = drains;
                thread = new Thread(Loop) { IsBackground = true, Name = "stand-in main thread" };
                thread.Start();
                Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
            }

            public MainThreadCalls Calls { get; private set; }

            public int Id { get; private set; }

            public volatile bool Drains;

            /// <summary>Waits until the loop has drained at least once more after the call.</summary>
            public void WaitForDrain()
            {
                int seen = drained;
                var clock = Stopwatch.StartNew();
                while (drained < seen + 2 && clock.ElapsedMilliseconds < 5000)
                {
                    Thread.Sleep(5);
                }
            }

            public void Dispose()
            {
                stop = true;
                thread.Join(TimeSpan.FromSeconds(5));
                started.Dispose();
            }

            private void Loop()
            {
                Id = Thread.CurrentThread.ManagedThreadId;
                Calls = new MainThreadCalls(Id);
                started.Set();
                while (!stop)
                {
                    if (Drains)
                    {
                        Calls.Drain();
                        drained++;
                    }

                    Thread.Sleep(1);
                }
            }
        }

        /// <summary>A <see cref="FakeCredentialStore"/> of a given kind that also records the thread of each call.</summary>
        private sealed class ThreadRecordingStore : ICredentialStore
        {
            private readonly FakeCredentialStore inner;

            public ThreadRecordingStore(CredentialStoreKind kind)
            {
                inner = new FakeCredentialStore(kind);
            }

            public List<int> Threads { get; } = new List<int>();

            public List<string> Operations => inner.Operations;

            public CredentialStoreKind Kind => inner.Kind;

            public string Description => inner.Description;

            public void Seed(string target, string secret) => inner.Seed(target, secret);

            public StoredCredential Read(string target)
            {
                Record();
                return inner.Read(target);
            }

            public bool Exists(string target)
            {
                Record();
                return inner.Exists(target);
            }

            public void Write(string target, string secret, string userName = null)
            {
                Record();
                inner.Write(target, secret, userName);
            }

            public bool Delete(string target)
            {
                Record();
                return inner.Delete(target);
            }

            private void Record()
            {
                lock (Threads)
                {
                    Threads.Add(Thread.CurrentThread.ManagedThreadId);
                }
            }
        }
    }
}
