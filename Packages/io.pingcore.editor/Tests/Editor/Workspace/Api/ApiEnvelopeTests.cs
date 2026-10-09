using System;
using System.Collections.Generic;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Redaction;

namespace PingCore.Editor.Workspace.Tests.Api
{
    /// <summary>The envelope rule, row by row (<see cref="ApiEnvelope.Classify"/>).</summary>
    public sealed class ApiEnvelopeTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        private static readonly WorkspaceRoute Release = WorkspaceRoutes.Get(WorkspaceRouteId.CreateRelease);

        private static ApiEnvelope.Outcome Classify(int status, string body, IReadOnlyDictionary<string, string> headers = null, WorkspaceRoute route = null)
            => ApiEnvelope.Classify(new PingCoreHttpResponse(status, headers, body), route ?? Release, Redactor.PatternsOnly, Now);

        [Test]
        public void A2xxJsonEnvelopeWithErrorFalseSucceedsWithItsData()
        {
            ApiEnvelope.Outcome o = Classify(200, "{\"error\":false,\"message\":\"Release started.\",\"data\":{\"cleanServerCount\":0}}");
            Assert.That(o.Ok, Is.True);
            Assert.That((int)o.Data["cleanServerCount"], Is.EqualTo(0));
            Assert.That(o.Message, Is.EqualTo("Release started."));
        }

        [Test]
        public void Http200WithErrorTrueIsAFailureCarryingTheMessageAndReason()
        {
            ApiEnvelope.Outcome o = Classify(200, "{\"error\":true,\"message\":\"Category not found\",\"data\":{\"reason\":\"no_member_deployments\"}}");
            Assert.That(o.Ok, Is.False, "the HTTP 200 error:true trap");
            Assert.That(o.Error.Kind, Is.EqualTo(PluginErrorKind.Rejected));
            Assert.That(o.Error.Message, Is.EqualTo("Category not found"));
            Assert.That(o.Error.Reason, Is.EqualTo("no_member_deployments"));
            Assert.That(o.Error.HttpStatus, Is.EqualTo(200));
            Assert.That(o.Error.Step, Is.EqualTo("release"));
        }

        [Test]
        public void Http401IsNotSignedInWhateverTheBody()
        {
            Assert.That(Classify(401, "{\"error\":true,\"message\":\"Authentication required\",\"data\":[]}").Error.Kind, Is.EqualTo(PluginErrorKind.NotSignedIn));
            Assert.That(Classify(401, "<html>nope</html>").Error.Kind, Is.EqualTo(PluginErrorKind.NotSignedIn));
        }

        [Test]
        public void Http403NamesTheRoutesBrandPermissionNeverOneParsedFromTheText()
        {
            ApiEnvelope.Outcome o = Classify(403, "{\"error\":true,\"message\":\"You need the registry.manage permission\",\"data\":[]}");
            Assert.That(o.Error.Kind, Is.EqualTo(PluginErrorKind.MissingBrandPermission));
            Assert.That(o.Error.Permission, Is.EqualTo("fleets.manage"), "from the route table");
            Assert.That(o.Error.Hint, Does.Contain("fleets.manage"));

            WorkspaceRoute templates = WorkspaceRoutes.Get(WorkspaceRouteId.GetTemplateSet);
            Assert.That(Classify(403, "not json", route: templates).Error.Permission, Is.EqualTo("my-games.templates"));
        }

        [Test]
        public void Http429ReadsRetryAfterAsSecondsOrADateAndNullWithoutIt()
        {
            ApiEnvelope.Outcome seconds = Classify(429, "{\"error\":true,\"message\":\"Too many requests\",\"data\":[]}", new Dictionary<string, string> { ["Retry-After"] = "7" });
            Assert.That(seconds.Error.Kind, Is.EqualTo(PluginErrorKind.RateLimited));
            Assert.That(seconds.Error.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(7)));

            ApiEnvelope.Outcome date = Classify(429, null, new Dictionary<string, string> { ["retry-after"] = Now.AddSeconds(30).ToString("r") });
            Assert.That(date.Error.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(30)));

            ApiEnvelope.Outcome none = Classify(429, "{\"error\":true,\"message\":\"x\",\"data\":[]}");
            Assert.That(none.Error.Kind, Is.EqualTo(PluginErrorKind.RateLimited));
            Assert.That(none.Error.RetryAfter, Is.Null);

            Assert.That(Classify(429, null, new Dictionary<string, string> { ["Retry-After"] = "soon" }).Error.RetryAfter, Is.Null);
            Assert.That(Classify(429, null, new Dictionary<string, string> { ["Retry-After"] = "99999" }).Error.RetryAfter, Is.EqualTo(TimeSpan.FromHours(1)), "clamped");
        }

        [Test]
        public void ANonJsonBodyIsAnEnvelopeFailureOnAnyOtherStatus()
        {
            Assert.That(Classify(200, "<html>proxy error</html>").Error.Kind, Is.EqualTo(PluginErrorKind.Envelope));
            Assert.That(Classify(502, "Bad Gateway").Error.Kind, Is.EqualTo(PluginErrorKind.Envelope));
            Assert.That(Classify(200, null).Error.Kind, Is.EqualTo(PluginErrorKind.Envelope));
            Assert.That(Classify(200, "[1,2]").Error.Kind, Is.EqualTo(PluginErrorKind.Envelope), "an array is not the envelope");
            Assert.That(Classify(200, "{\"message\":\"ok\",\"data\":{}}").Error.Kind, Is.EqualTo(PluginErrorKind.Envelope), "no error field");
            Assert.That(Classify(200, "{\"error\":\"false\",\"data\":{}}").Error.Kind, Is.EqualTo(PluginErrorKind.Envelope), "a string is not a boolean");
            Assert.That(Classify(500, "{\"error\":false,\"data\":{}}").Error.Kind, Is.EqualTo(PluginErrorKind.Envelope), "error:false is a success only on 2xx");
        }

        [Test]
        public void ARedirectIsRefusedNeverFollowed()
        {
            ApiEnvelope.Outcome o = Classify(302, null, new Dictionary<string, string> { ["Location"] = "https://elsewhere.example/api/fleets" });
            Assert.That(o.Error.Kind, Is.EqualTo(PluginErrorKind.Transport));
            Assert.That(o.Error.Message, Does.Contain("redirect").And.Not.Contain("elsewhere.example"));
        }

        [Test]
        public void NotFoundConflictAndAServerErrorEnvelopeKeepTheirKinds()
        {
            Assert.That(Classify(404, "{\"error\":true,\"message\":\"Fleet not found\",\"data\":[]}").Error.Kind, Is.EqualTo(PluginErrorKind.NotFound));
            ApiEnvelope.Outcome conflict = Classify(409, "{\"error\":true,\"message\":\"In progress\",\"data\":{\"reason\":\"release_in_progress\"}}");
            Assert.That(conflict.Error.Kind, Is.EqualTo(PluginErrorKind.Conflict));
            Assert.That(conflict.Error.Reason, Is.EqualTo("release_in_progress"));
            Assert.That(Classify(503, "{\"error\":true,\"message\":\"cdn\",\"data\":{\"reason\":\"cdn_unreadable\"}}").Error.Kind, Is.EqualTo(PluginErrorKind.Rejected));
        }

        [Test]
        public void ATokenInTheServerMessageIsMaskedAndLongMessagesAreClipped()
        {
            string token = "usr_" + new string('A', 20);
            ApiEnvelope.Outcome o = Classify(200, "{\"error\":true,\"message\":\"bad key " + token + "\",\"data\":[]}");
            Assert.That(o.Error.Message, Does.Not.Contain(token), "precondition: the body held it");
            Assert.That(o.Error.Message, Does.Contain("usr_" + Redactor.Mask));

            ApiEnvelope.Outcome longOne = Classify(400, "{\"error\":true,\"message\":\"" + new string('x', 1000) + "\",\"data\":[]}");
            Assert.That(longOne.Error.Message.Length, Is.LessThanOrEqualTo(ApiEnvelope.MaxMessageLength + 3));
        }
    }
}
