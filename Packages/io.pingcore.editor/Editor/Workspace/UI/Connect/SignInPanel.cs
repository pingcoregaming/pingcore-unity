using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.UI.Common;
using PingCore.Editor.Workspace.UI.Sections;
using UnityEditor;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Connect
{
    /// <summary>
    /// The sign-in half of Connect. Signed out: the store banner, the <c>usr_</c> key as a write-only password field
    /// (emptied the moment Sign in is pressed, never shown again), "This session only" and Sign in. Signed in:
    /// "Signed in to &lt;workspace&gt; as &lt;email&gt;", Verify and Sign out. There is no workspace URL: the API finds the
    /// workspace from the key. The line and the workspace panel's address (<c>me/capabilities</c> <c>brandUrl</c>,
    /// <see cref="PanelLinks"/>) are remembered for this Editor session (<see cref="SessionState"/>), and read once
    /// with Verify when the window opens without them. The logic is <see cref="SignInModel"/>.
    /// </summary>
    public sealed class SignInPanel
    {
        private const string LineKeyPrefix = "PingCore.SignedInLine.";
        private const string PanelKeyPrefix = "PingCore.PanelBase.";
        private const string AutoVerifiedKeyPrefix = "PingCore.SignedInAutoVerified.";
        private const string RefusedKeyPrefix = "PingCore.SignedInRefused.";

        private SignInModel model;
        private Action changed;
        private SectionActions actions;
        private VisualElement signedOutBox;
        private VisualElement signedInBox;
        private TextField keyField;
        private Label storeBanner;
        private Label status;

        /// <summary>
        /// True while a key is stored for the endpoint (asked without reading it) and the workspace has not refused
        /// it: a 401 on the last Verify, remembered for this Editor session so a domain reload does not forget it.
        /// </summary>
        public bool SignedIn => model != null && model.HasStoredKey() && !model.KeyRefused
            && !SessionState.GetBool(RefusedKeyPrefix + model.Endpoint.Host, false);

        /// <summary>The signed-in line: the workspace and email when known, else a prompt to Verify (the key is never read for it).</summary>
        public string Line
        {
            get
            {
                if (!SignedIn)
                {
                    return null;
                }

                string remembered = SessionState.GetString(LineKeyPrefix + model.Endpoint.Host, string.Empty);
                return remembered.Length > 0 ? remembered : "A key is stored; press Verify to show the workspace's name.";
            }
        }

        /// <summary>The workspace panel's address for this Editor session, or <see cref="PanelLinks.DefaultBase"/> before it is known.</summary>
        public static string PanelBase(string host)
        {
            string remembered = string.IsNullOrEmpty(host) ? string.Empty : SessionState.GetString(PanelKeyPrefix + host, string.Empty);
            return remembered.Length > 0 ? remembered : PanelLinks.DefaultBase;
        }

        /// <summary>Builds the panel into <paramref name="body"/>.</summary>
        public void Build(VisualElement body, SignInModel signIn, Action onChanged)
        {
            model = signIn;
            changed = onChanged;
            actions = new SectionActions();

            signedOutBox = new VisualElement();
            body.Add(signedOutBox);
            storeBanner = Ui.Banner(string.Empty, BannerKind.Info);
            signedOutBox.Add(storeBanner);
            signedOutBox.Add(Ui.Note($"Paste the API key (usr_) of a brand member of your workspace. The plugin calls {WorkspaceEndpoint.DefaultApiBase}, and PingCore finds your workspace from the key: there is no workspace URL to type."));
            keyField = Ui.Password("API key (usr_)");
            keyField.tooltip = "A brand member's usr_ key. It goes straight to the credential store and is never shown again.";
            signedOutBox.Add(keyField);
            var sessionOnly = new Toggle("This session only") { value = model.User.SessionOnlyKey };
            sessionOnly.tooltip = "Keep the key in memory until the Editor quits instead of the OS credential store.";
            sessionOnly.RegisterValueChangedCallback(e => SectionActions.Guard(status, () => model.SetSessionOnly(e.newValue), Refresh));
            signedOutBox.Add(sessionOnly);
            signedOutBox.Add(Ui.Row(Ui.Primary(Ui.Button("Sign in", () => actions.Run(status, SignInAsync, Changed)))));
            signedOutBox.Add(Ui.Note("Use a key of a brand member who holds only the brand permissions the plugin needs, not the workspace owner's key."));

            signedInBox = new VisualElement();
            body.Add(signedInBox);
            signedInBox.Add(Ui.Row(
                Ui.Button("Verify", () => actions.Run(status, VerifyAsync, Changed)),
                Ui.Button("Sign out", () => SectionActions.Guard(status, SignOut, Changed))));
            signedInBox.Add(Ui.Note("Sign out deletes the key from the credential store and from this session."));

            status = Ui.Status();
            body.Add(status);
            Refresh();
        }

        /// <summary>Shows the signed-in or signed-out controls; verifies once per Editor session when the line is unknown.</summary>
        public void Refresh()
        {
            if (model == null)
            {
                return;
            }

            (string text, bool warning) = model.StoreBanner();
            Ui.SetBanner(storeBanner, text, warning ? BannerKind.Warning : BannerKind.Info);
            bool signedIn = SignedIn;
            signedOutBox.style.display = signedIn ? DisplayStyle.None : DisplayStyle.Flex;
            signedInBox.style.display = signedIn ? DisplayStyle.Flex : DisplayStyle.None;
            if (model.LastResult != null && !model.LastResult.Ok)
            {
                status.text = SignInModel.Describe(model.LastResult);
            }

            string host = model.Endpoint?.Host;
            if (signedIn && !actions.Busy && SessionState.GetString(LineKeyPrefix + host, string.Empty).Length == 0 && !SessionState.GetBool(AutoVerifiedKeyPrefix + host, false))
            {
                SessionState.SetBool(AutoVerifiedKeyPrefix + host, true);
                actions.Run(status, VerifyAsync, Changed);
            }
        }

        /// <summary>The window's <c>OnDisable</c>: the key field never outlives the window.</summary>
        public void Disable()
        {
            if (keyField != null)
            {
                keyField.value = string.Empty;
            }
        }

        private void Changed()
        {
            Refresh();
            changed?.Invoke();
        }

        private async Task SignInAsync(CancellationToken cancellationToken)
        {
            string key = keyField.value;
            keyField.value = string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                status.text = "Paste a usr_ key into the API key field first.";
                return;
            }

            status.text = "Signing in...";
            await model.SignInAsync(key, cancellationToken);
            key = null;
            Remember();
        }

        private async Task VerifyAsync(CancellationToken cancellationToken)
        {
            status.text = "Verifying...";
            await model.VerifyAsync(cancellationToken);
            Remember();
        }

        // The line and the panel's address live in this Editor session only: the workspace's name, the brand member's
        // email and the brand's panel URL, never the key.
        private void Remember()
        {
            string line = SignInModel.SignedInLine(model.LastResult);
            status.text = line != null ? string.Empty : SignInModel.Describe(model.LastResult);
            if (model.Endpoint == null)
            {
                return;
            }

            string host = model.Endpoint.Host;
            if (line != null)
            {
                SessionState.SetString(LineKeyPrefix + host, line);
                SessionState.SetString(PanelKeyPrefix + host, PanelLinks.BaseFrom(model.LastResult?.Identity?.Identity?.BrandUrl));
                SessionState.EraseBool(RefusedKeyPrefix + host);
            }
            else if (model.KeyRefused)
            {
                SessionState.EraseString(LineKeyPrefix + host);
                SessionState.EraseString(PanelKeyPrefix + host);
                SessionState.SetBool(RefusedKeyPrefix + host, true);
            }
        }

        private void SignOut()
        {
            if (model.Endpoint != null)
            {
                SessionState.EraseString(LineKeyPrefix + model.Endpoint.Host);
                SessionState.EraseString(PanelKeyPrefix + model.Endpoint.Host);
                SessionState.EraseBool(RefusedKeyPrefix + model.Endpoint.Host);
            }

            status.text = model.SignOut();
        }
    }
}
