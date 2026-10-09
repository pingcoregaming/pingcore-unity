using System.Linq;
using System.Reflection;
using NUnit.Framework;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.UI;
using UnityEditor;

namespace PingCore.Editor.Workspace.Tests.UI
{
    /// <summary>
    /// The one PingCore window: one menu item, Window > PingCore, on the window's own method, and no
    /// settings provider or second window left behind. Also the endpoint every section calls: the
    /// default and its development override. The sections' model is <see cref="SectionBookTests"/>.
    /// </summary>
    public sealed class PingCoreWindowTests
    {
        [Test]
        public void TheWindowHasOneMenuItemWindowPingCore()
        {
            MethodInfo[] methods = typeof(PingCoreWindow).GetMethods(BindingFlags.Public | BindingFlags.Static);
            var items = methods.SelectMany(m => m.GetCustomAttributes<MenuItem>().Select(a => (a.menuItem, m.Name))).ToList();
            Assert.That(items.Select(i => i.menuItem), Is.EqualTo(new[] { "Window/PingCore" }));
            Assert.That(items.Single().Name, Is.EqualTo(nameof(PingCoreWindow.Open)));
            Assert.That(PingCoreMenu.WindowItem, Is.EqualTo("Window/PingCore"));
            Assert.That(new[] { PingCoreMenu.SignInText, PingCoreMenu.ShipText, PingCoreMenu.StatusText, PingCoreMenu.PlayerHostingText }, Is.EqualTo(new[]
            {
                "Window > PingCore, Connect", "Window > PingCore, Ship", "Window > PingCore, Status", "Window > PingCore, Player hosting",
            }), "messages name the sections exactly as their headers read");
            Assert.That(SectionBook.All.Select(SectionBook.Title), Is.EqualTo(new[] { "Connect", "Ship", "Status", "Player hosting" }), "the spec's four sections, in order");
        }

        [Test]
        public void NoPingCoreSettingsProviderOrSecondWindowIsLeft()
        {
            Assembly workspace = typeof(PingCoreWindow).Assembly;
            Assert.That(workspace.GetTypes().Where(t => typeof(SettingsProvider).IsAssignableFrom(t)).Select(t => t.Name), Is.Empty, "Preferences and Project Settings no longer hold PingCore pages");
            Assert.That(workspace.GetTypes().Where(t => typeof(EditorWindow).IsAssignableFrom(t)).Select(t => t.Name), Is.EqualTo(new[] { nameof(PingCoreWindow) }), "one window");
        }

        [Test]
        public void TheWindowSerializesNothingSoNoKeyOrTokenCanReachTheLayout()
        {
            System.Collections.Generic.IEnumerable<FieldInfo> serialized = typeof(PingCoreWindow)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => f.IsPublic || f.GetCustomAttribute<UnityEngine.SerializeField>() != null);
            Assert.That(serialized.Select(f => f.Name), Is.Empty, "[mutation: serialize a section into the window layout]");
        }

        [Test]
        public void TheDefaultEndpointIsAppPingcoreIoAndAnOverrideMustBeHttps()
        {
            Assert.That(WorkspaceEndpoint.Default.ApiBase, Is.EqualTo("https://app.pingcore.io/api/"));
            Assert.That(WorkspaceEndpoint.DefaultApiBase, Is.EqualTo("https://app.pingcore.io/api"));
            Assert.That(WorkspaceEndpoint.Default.IsOverride, Is.False);

            Assert.That(WorkspaceEndpoint.Resolve(null, out WorkspaceEndpoint none, out _), Is.True);
            Assert.That(none, Is.SameAs(WorkspaceEndpoint.Default));
            Assert.That(WorkspaceEndpoint.Resolve("  ", out WorkspaceEndpoint blank, out _) && blank == WorkspaceEndpoint.Default, Is.True);

            Assert.That(WorkspaceEndpoint.Resolve("https://API.example.test/api", out WorkspaceEndpoint dev, out _), Is.True);
            Assert.That((dev.Host, dev.ApiBase, dev.IsOverride), Is.EqualTo(("api.example.test", "https://api.example.test/api/", true)));

            foreach (string refused in new[] { "http://api.example.test", "https://api.example.test/v2", "https://user@api.example.test", "https://api.example.test/?cmd=x" })
            {
                Assert.That(WorkspaceEndpoint.Resolve(refused, out WorkspaceEndpoint endpoint, out string problem), Is.False, refused);
                Assert.That(endpoint, Is.Null, "[mutation: fall back to the default] " + refused);
                Assert.That(problem, Does.Contain("apiBaseOverride"));
            }
        }
    }
}
