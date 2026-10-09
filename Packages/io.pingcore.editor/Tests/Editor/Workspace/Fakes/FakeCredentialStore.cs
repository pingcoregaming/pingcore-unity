using System;
using System.Collections.Generic;
using PingCore.Editor.Workspace.Credentials;

namespace PingCore.Editor.Workspace.Tests.Fakes
{
    /// <summary>
    /// An in-memory <see cref="ICredentialStore"/>. Every write, read and delete is recorded by
    /// target (never by value). <see cref="FailNextWith"/> makes the next operation throw a
    /// <see cref="CredentialStoreException"/>, so a caller's failure path can be driven.
    /// </summary>
    public sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, StoredCredential> entries = new Dictionary<string, StoredCredential>(StringComparer.Ordinal);
        private string failure;

        /// <param name="kind">The kind it reports; <see cref="CredentialStoreKind.Fake"/> by default.</param>
        /// <param name="description">The sentence it reports.</param>
        public FakeCredentialStore(CredentialStoreKind kind = CredentialStoreKind.Fake, string description = "test store")
        {
            Kind = kind;
            Description = description;
        }

        public CredentialStoreKind Kind { get; }

        public string Description { get; }

        /// <summary>Operations in order, for example <c>write PingCore/host/usr</c> or <c>exists PingCore/host/cdnpush/31</c>.</summary>
        public List<string> Operations { get; } = new List<string>();

        /// <summary>The targets currently stored.</summary>
        public IReadOnlyCollection<string> Targets => entries.Keys;

        /// <summary>Makes the next operation throw with <paramref name="message"/>.</summary>
        public void FailNextWith(string message) => failure = message;

        /// <summary>Stores a credential without recording an operation (test setup).</summary>
        public void Seed(string target, string secret, string userName = null) => entries[target] = new StoredCredential(secret, userName);

        public StoredCredential Read(string target)
        {
            Operations.Add("read " + target);
            ThrowIfFailing();
            return entries.TryGetValue(target, out StoredCredential c) ? c : null;
        }

        public bool Exists(string target)
        {
            Operations.Add("exists " + target);
            ThrowIfFailing();
            return entries.ContainsKey(target);
        }

        public void Write(string target, string secret, string userName = null)
        {
            Operations.Add("write " + target);
            ThrowIfFailing();
            if (!CredentialTargets.IsTarget(target))
            {
                throw new ArgumentException("not a credential target", nameof(target));
            }

            entries[target] = new StoredCredential(secret, userName);
        }

        public bool Delete(string target)
        {
            Operations.Add("delete " + target);
            ThrowIfFailing();
            return entries.Remove(target);
        }

        private void ThrowIfFailing()
        {
            if (failure != null)
            {
                string message = failure;
                failure = null;
                throw new CredentialStoreException(message);
            }
        }
    }
}
