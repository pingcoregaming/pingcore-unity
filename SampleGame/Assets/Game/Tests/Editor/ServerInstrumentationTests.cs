using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BeaconRush.Hosting;
using BeaconRush.Networking;
using BeaconRush.Session;
using NUnit.Framework;
using PingCore.Core.Handshake;
using PingCore.Discovery.Host;
using UnityEngine;
using UnityEngine.TestTools;

namespace BeaconRush.Tests.Editor
{
    /// <summary>
    /// The game's instrumentation hook: with nothing installed the game plays exactly as it is (in particular no approval
    /// admits a join on a supervisor self-allocation), an installed instrumentation reaches each call site, it can never
    /// change the game's own approval fields, and one that throws never stops the game.
    /// </summary>
    public sealed class ServerInstrumentationTests
    {
        private static readonly GameHostingMode[] Modes = { GameHostingMode.Hosted, GameHostingMode.SelfHosted, GameHostingMode.Local, GameHostingMode.Listen };

        private static readonly string[] GameApprovalKeys = { "clientId", "decision", "reason", "kind", "mode", "evidence", "ms", "ticketRef", "sessionClaim", "detail" };

        /// <summary>A mode with nothing of its own but the selection, so the base class's options are what is tested.</summary>
        private sealed class BareMode : HostingModeBase
        {
            public BareMode(GameHostingMode mode)
                : base(new HostingSelection(mode, "test", mode == GameHostingMode.Local, false))
            {
            }

            public override IAdmissionEvidence Evidence { get; } = new LanAdmissionEvidence();

            public override bool UsesAllocations => Mode == GameHostingMode.Hosted;
        }

        /// <summary>Records what the game asked and switches on whatever the test says.</summary>
        private sealed class Recording : ServerInstrumentation
        {
            public bool SetEverything { get; set; }

            public IAdmissionEvidence Wrapped { get; private set; }

            public Func<IAdmissionEvidence, IAdmissionEvidence> Wrap { get; set; }

            public IEnumerable<KeyValuePair<string, object>> Extra { get; set; }

            public override void ConfigureApproval(GameHostingMode mode, ApprovalOptions options)
            {
                if (!SetEverything)
                {
                    return;
                }

                options.ProtocolVersion = 99;
                options.Mode = HostingMode.SelfHosted;
                options.LanOnly = mode != GameHostingMode.Local;
                options.ClaimIdleSessions = mode != GameHostingMode.Hosted;
                options.AllowSelfAllocatedJoins = true;
            }

            public override IAdmissionEvidence WrapHeartbeatEvidence(IAdmissionEvidence evidence)
            {
                Wrapped = evidence;
                return Wrap == null ? evidence : Wrap(evidence);
            }

            public override IEnumerable<KeyValuePair<string, object>> ApprovalEventFields(AdmissionDecision decision) => Extra;
        }

        private sealed class Throwing : ServerInstrumentation
        {
            public override void ConfigureApproval(GameHostingMode mode, ApprovalOptions options)
            {
                options.AllowSelfAllocatedJoins = true;
                throw new InvalidOperationException("instrumentation broke");
            }

            public override IEnumerable<KeyValuePair<string, object>> ApprovalEventFields(AdmissionDecision decision) => Lazy();

            public override bool DeferFirstPlayersWrite => throw new InvalidOperationException("instrumentation broke");

            private static IEnumerable<KeyValuePair<string, object>> Lazy()
            {
                yield return new KeyValuePair<string, object>("partial", 1);
                throw new InvalidOperationException("instrumentation broke while enumerated");
            }
        }

        private sealed class WatchingEvidence : IAdmissionEvidence
        {
            public WatchingEvidence(IAdmissionEvidence inner)
            {
                Inner = inner;
            }

            public IAdmissionEvidence Inner { get; }

            public string Source => Inner.Source;

            public bool IsStopping => Inner.IsStopping;

            public Task GatherAsync(JoinTicket ticket, AdmissionFacts facts, ApprovalOptions options, CancellationToken cancellationToken) =>
                Inner.GatherAsync(ticket, facts, options, cancellationToken);
        }

        [SetUp]
        [TearDown]
        public void Uninstall() => ServerInstrumentation.Current = null;

        private static AdmissionDecision Refusal() => AdmissionDecision.Reject(JoinRejectReason.PayloadEmpty, "empty", HostingMode.Hosted, "fleet");

        private static HeartbeatTier Tier() => new HeartbeatTier(new HeartbeatReporterOptions
        {
            BaseUrl = HostingEnvironment.DefaultDiscoveryUrl,
            Name = "instrumentation test",
            GamePort = 7790,
            MaxPlayers = 8,
        });

        [Test]
        public void NothingInstalledIsNoneAndSettingNullRestoresIt()
        {
            Assert.That(ServerInstrumentation.Current, Is.SameAs(ServerInstrumentation.None));
            ServerInstrumentation.Current = new Recording();
            Assert.That(ServerInstrumentation.Current, Is.Not.SameAs(ServerInstrumentation.None), "the control");
            ServerInstrumentation.Current = null;
            Assert.That(ServerInstrumentation.Current, Is.SameAs(ServerInstrumentation.None));
        }

        [Test]
        public void NoneChangesNothingTheGameAsks()
        {
            ServerInstrumentation none = ServerInstrumentation.None;
            var evidence = new LanAdmissionEvidence();
            Assert.That(none.DeferFirstPlayersWrite, Is.False);
            Assert.That(BootPlan.For(none.DeferFirstPlayersWrite).FirstPlayersWrite, Is.EqualTo(FirstPlayersWrite.BeforeListening));
            Assert.That(none.BeforeReadyAsync(CancellationToken.None).IsCompleted, Is.True);
            Assert.That(none.WrapHeartbeatEvidence(evidence), Is.SameAs(evidence));
            Assert.That(none.ApprovalEventFields(Refusal()), Is.Null);
            Assert.That(none.AllocatedSessionSettings(null, null, SessionSettings.Default), Is.SameAs(SessionSettings.Default));
            Assert.That(none.LocalSessionSettings(GameHostingMode.Listen, SessionSettings.Local), Is.SameAs(SessionSettings.Local));
        }

        [Test]
        public void WithNothingInstalledNoModeAdmitsJoinsOnASelfAllocation()
        {
            foreach (GameHostingMode mode in Modes)
            {
                ApprovalOptions options = new BareMode(mode).ApprovalOptions();
                Assert.That(options.AllowSelfAllocatedJoins, Is.False, mode.ToString());
                Assert.That(options.ClaimIdleSessions, Is.EqualTo(mode == GameHostingMode.Hosted), mode.ToString());
                Assert.That(options.ProtocolVersion, Is.EqualTo(BeaconRushProtocol.Version));
            }
        }

        [Test]
        public void AnInstrumentationReachesTheApprovalButNeverTheGamesOwnFields()
        {
            ServerInstrumentation.Current = new Recording { SetEverything = true };
            foreach (GameHostingMode mode in Modes)
            {
                var bare = new BareMode(mode);
                ApprovalOptions options = bare.ApprovalOptions();
                Assert.That(options.AllowSelfAllocatedJoins, Is.True, mode + ": what the instrumentation switched on stays on");
                Assert.That(options.ProtocolVersion, Is.EqualTo(BeaconRushProtocol.Version), mode.ToString());
                Assert.That(options.Mode, Is.EqualTo(bare.Selection.ApprovalMode), mode.ToString());
                Assert.That(options.LanOnly, Is.EqualTo(mode == GameHostingMode.Local), mode.ToString());
                Assert.That(options.ClaimIdleSessions, Is.EqualTo(mode == GameHostingMode.Hosted), mode.ToString());
            }
        }

        [Test]
        public void AnInstrumentationThatThrowsIsLoggedAndTheGameGoesOn()
        {
            ServerInstrumentation.Current = new Throwing();
            LogAssert.Expect(LogType.Exception, new Regex("instrumentation broke"));
            ApprovalOptions options = new BareMode(GameHostingMode.Hosted).ApprovalOptions();
            Assert.That(options.ClaimIdleSessions, Is.True, "the game's fields are set after the hook, whatever it did");

            LogAssert.Expect(LogType.Exception, new Regex("instrumentation broke"));
            Assert.That(ServerInstrumentation.Ask(i => i.DeferFirstPlayersWrite, false), Is.False, "the fallback");

            LogAssert.Expect(LogType.Exception, new Regex("instrumentation broke while enumerated"));
            object[] fields = GameServerRuntime.ApprovalEventFields(Refusal(), GameHostingMode.Hosted);
            Assert.That(Keys(fields), Is.EqualTo(GameApprovalKeys), "a field it yielded before it threw is not half-added");
        }

        [Test]
        public void TheHeartbeatTierUsesTheEvidenceTheInstrumentationHandsBack()
        {
            var recording = new Recording { Wrap = inner => new WatchingEvidence(inner) };
            ServerInstrumentation.Current = recording;
            var mode = new HeartbeatMode(HostingModeSelector.ForListen(false), Tier(), true, "host");
            try
            {
                Assert.That(mode.Evidence, Is.TypeOf<WatchingEvidence>());
                Assert.That(((WatchingEvidence)mode.Evidence).Inner, Is.SameAs(recording.Wrapped));
                Assert.That(mode.Evidence.Source, Is.EqualTo("heartbeat"));
            }
            finally
            {
                mode.Dispose();
            }

            ServerInstrumentation.Current = new Recording { Wrap = inner => null };
            var unwrapped = new HeartbeatMode(HostingModeSelector.ForListen(false), Tier(), true, "host");
            try
            {
                Assert.That(unwrapped.Evidence, Is.Not.Null, "a null answer keeps the game's own evidence");
                Assert.That(unwrapped.Evidence.Source, Is.EqualTo("heartbeat"));
            }
            finally
            {
                unwrapped.Dispose();
            }
        }

        [Test]
        public void TheApprovalEventCarriesTheGamesFieldsThenTheInstrumentations()
        {
            object[] plain = GameServerRuntime.ApprovalEventFields(Refusal(), GameHostingMode.Hosted);
            Assert.That(Keys(plain), Is.EqualTo(GameApprovalKeys));
            Assert.That(plain[Array.IndexOf(plain, "mode") + 1], Is.EqualTo("hosted"));
            Assert.That(plain[Array.IndexOf(plain, "reason") + 1], Is.EqualTo("payload_empty"));

            ServerInstrumentation.Current = new Recording { Extra = new[] { new KeyValuePair<string, object>("watched", true) } };
            object[] extended = GameServerRuntime.ApprovalEventFields(Refusal(), null);
            Assert.That(Keys(extended), Is.EqualTo(GameApprovalKeys.Concat(new[] { "watched" })));
            Assert.That(extended[extended.Length - 1], Is.EqualTo(true));
            Assert.That(extended[Array.IndexOf(extended, "mode") + 1], Is.Null, "no mode yet");
        }

        private static string[] Keys(object[] pairs)
        {
            var keys = new List<string>();
            for (int i = 0; i < pairs.Length; i += 2)
            {
                keys.Add((string)pairs[i]);
            }

            return keys.ToArray();
        }
    }
}
