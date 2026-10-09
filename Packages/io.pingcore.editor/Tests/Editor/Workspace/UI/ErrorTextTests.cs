using System;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.UI.Common;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>How the window shows a plugin error.</summary>
    public sealed class ErrorTextTests
    {
        [Test]
        public void AMissingBrandPermissionNamesThePermissionAndTheHint()
        {
            var error = new PluginError("release", PluginErrorKind.MissingBrandPermission, "Forbidden.", "Ask a workspace owner.") { Permission = "fleets.manage" };

            Assert.That(ErrorText.Of(error), Is.EqualTo("Forbidden. This needs the brand permission fleets.manage. Ask a workspace owner."));
        }

        [Test]
        public void ARateLimitNamesTheWaitAndAnyTokenInTheTextIsMasked()
        {
            var error = new PluginError("release", PluginErrorKind.RateLimited, "Too many requests for usr" + "_" + "abcdefghijklmnop1234.", null) { RetryAfter = TimeSpan.FromSeconds(2.5) };

            Assert.That(ErrorText.Of(error), Is.EqualTo("Too many requests for usr_[redacted]. Try again in 3 s."));
            Assert.That(ErrorText.Of(null), Is.Empty);
        }
    }
}
