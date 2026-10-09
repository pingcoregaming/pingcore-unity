using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;

namespace PingCore.Sdk.Tests.Editor
{
    /// <summary>
    /// Every type carrying <see cref="WireContractAttribute"/> must be exercised by at least one
    /// repository fixture: a <c>contracts/*/fixtures/*.json</c> file whose <c>dto</c> is the
    /// type's CLR full name and whose <c>contract</c> matches one of its attributes. A DTO
    /// without a fixture is never round-tripped, so a renamed field in it would go unnoticed.
    /// This always reads the repository's <c>contracts/</c>, never <c>PINGCORE_FIXTURES_DIR</c>.
    /// </summary>
    public sealed class FixtureCoverageTests
    {
        [Test]
        public void EveryWireContractTypeHasAFixtureForOneOfItsContracts()
        {
            IReadOnlyList<Type> types = WireCatalog.ContractTypes();
            Assert.That(types.Count, Is.GreaterThan(5), "no [WireContract] types found; the catalog is wrong");

            List<(string Dto, JObject Contract)> fixtures = RepositoryFixtures();
            Assert.That(fixtures.Count, Is.GreaterThan(0), "no fixtures found under contracts/*/fixtures/");

            List<string> uncovered = Uncovered(types, fixtures);
            Assert.That(uncovered, Is.Empty, "[WireContract] types with no matching fixture:\n" + string.Join("\n", uncovered));
        }

        [Test]
        public void TheCoverageCheckFlagsATypeWithNoFixtureOrOnlyAWrongContract()
        {
            Type ticket = typeof(PingCore.Discovery.Client.Wire.TicketResponse);
            Type cancel = typeof(PingCore.Discovery.Client.Wire.CancelTicketResponse);
            WireContractAttribute ticketContract = ticket.GetCustomAttributes<WireContractAttribute>(false).First();
            JObject matching = ContractObject(ticketContract);

            var covered = new List<(string, JObject)> { (ticket.FullName, matching) };
            Assert.That(Uncovered(new[] { ticket }, covered), Is.Empty, "precondition: a matching fixture covers the type");

            Assert.That(Uncovered(new[] { ticket, cancel }, covered), Is.EqualTo(new[] { $"{cancel.FullName}: no fixture names it" }));

            JObject wrongStatus = ContractObject(ticketContract);
            wrongStatus["status"] = 418;
            Assert.That(Uncovered(new[] { ticket }, new List<(string, JObject)> { (ticket.FullName, wrongStatus) }), Has.Some.StartsWith($"{ticket.FullName}: 1 fixture(s) name it"));

            Assert.That(Uncovered(new[] { ticket }, new List<(string, JObject)> { (cancel.FullName, matching) }), Has.Some.Contains("no fixture names it"), "a fixture for another DTO does not count");
        }

        /// <summary>One line per contract type that no fixture covers.</summary>
        internal static List<string> Uncovered(IEnumerable<Type> types, IReadOnlyList<(string Dto, JObject Contract)> fixtures)
        {
            var problems = new List<string>();
            foreach (Type type in types)
            {
                List<WireContractAttribute> contracts = type.GetCustomAttributes<WireContractAttribute>(false).ToList();
                List<JObject> named = fixtures.Where(f => string.Equals(f.Dto, type.FullName, StringComparison.Ordinal)).Select(f => f.Contract).ToList();
                if (named.Count == 0)
                {
                    problems.Add($"{type.FullName}: no fixture names it");
                    continue;
                }

                if (!named.Any(c => contracts.Any(a => FixtureRoundTripTests.ContractMatches(a, c))))
                {
                    problems.Add($"{type.FullName}: {named.Count} fixture(s) name it, but none matches one of its {contracts.Count} [WireContract] route(s)");
                }
            }

            return problems;
        }

        private static List<(string Dto, JObject Contract)> RepositoryFixtures()
        {
            string contracts = RepoPaths.ContractsRoot;
            var fixtures = new List<(string, JObject)>();
            foreach (string file in Directory.GetFiles(contracts, "*.json", SearchOption.AllDirectories)
                .Where(RepoPaths.IsSdkFixture)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                JObject fixture = FixtureRoundTripTests.ParseFixture(File.ReadAllText(file));
                fixtures.Add(((string)fixture["dto"], fixture["contract"] as JObject));
            }

            return fixtures;
        }

        private static JObject ContractObject(WireContractAttribute a)
        {
            return new JObject
            {
                ["source"] = a.Source,
                ["method"] = a.Method,
                ["path"] = a.Path,
                ["direction"] = DtoDump.DirectionName(a.Direction),
                ["status"] = a.Status,
            };
        }
    }
}
