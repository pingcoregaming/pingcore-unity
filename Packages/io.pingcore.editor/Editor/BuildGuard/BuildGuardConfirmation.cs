using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// A record that the PingCore API confirmed a configured <c>openRegistrationHeartbeatToken</c> is
    /// the heartbeat-scope token of an open-registration Discovery app. It holds the SHA-256 digest of
    /// the token, never the token. Only the Editor plugin's confirmation writer
    /// (<c>PingCore.Editor.Workspace.Confirmation.HeartbeatTokenConfirmationWriter</c>, "Confirm for
    /// build" on Window > PingCore, Player hosting) produces one, after asking the API; a configured token
    /// without one fails the build.
    /// </summary>
    [Serializable]
    public sealed class BuildGuardConfirmation
    {
        /// <summary>The app's public id (<c>dscp_...</c>).</summary>
        public string appPublicId;

        /// <summary>Lowercase hex SHA-256 of the token's UTF-8 bytes.</summary>
        public string tokenDigest;

        /// <summary>Must be <see cref="BuildGuardConfirmations.HeartbeatScope"/>.</summary>
        public string scope;

        /// <summary>Must be <see cref="BuildGuardConfirmations.OpenRegistrationMode"/>.</summary>
        public string registrationMode;

        /// <summary>When the API confirmed it, ISO 8601 UTC.</summary>
        public string confirmedAt;
    }

    /// <summary>The contents of <c>ProjectSettings/PingCoreBuildGuard.json</c>.</summary>
    [Serializable]
    public sealed class BuildGuardConfirmationRecord
    {
        public BuildGuardConfirmation[] confirmations = Array.Empty<BuildGuardConfirmation>();
    }

    /// <summary>A non-empty <c>openRegistrationHeartbeatToken</c> and the asset that holds it.</summary>
    public readonly struct BuildGuardConfiguredToken
    {
        public BuildGuardConfiguredToken(string assetPath, string token)
        {
            AssetPath = assetPath ?? string.Empty;
            Token = token ?? string.Empty;
        }

        public string AssetPath { get; }
        public string Token { get; }
    }

    /// <summary>The heartbeat tokens a build may carry, and a finding for each one it may not.</summary>
    public sealed class BuildGuardTokenResolution
    {
        public static readonly BuildGuardTokenResolution None =
            new BuildGuardTokenResolution(Array.Empty<string>(), Array.Empty<BuildGuardFinding>());

        public BuildGuardTokenResolution(IReadOnlyCollection<string> allowed, IReadOnlyList<BuildGuardFinding> findings)
        {
            Allowed = allowed ?? Array.Empty<string>();
            Findings = findings ?? Array.Empty<BuildGuardFinding>();
        }

        /// <summary>Configured tokens with a matching confirmation. Compared, never logged.</summary>
        public IReadOnlyCollection<string> Allowed { get; }

        /// <summary>One <c>secret_literal</c> finding per configured token without a matching confirmation.</summary>
        public IReadOnlyList<BuildGuardFinding> Findings { get; }
    }

    /// <summary>The pure confirmation rules: digest, match and resolution.</summary>
    public static class BuildGuardConfirmations
    {
        public const string HeartbeatScope = "heartbeat";
        public const string OpenRegistrationMode = "open";
        public const string PublicIdPrefix = "dscp_";

        /// <summary>The detail of the finding for a configured token whose scope is not confirmed.</summary>
        public const string UnconfirmedDetail = "openRegistrationHeartbeatToken is set but its scope is unconfirmed; "
            + "confirm it on Window > PingCore, Player hosting";

        /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="token"/>.</summary>
        public static string Digest(string token)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? string.Empty));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    hex.Append(b.ToString("x2"));
                }

                return hex.ToString();
            }
        }

        /// <summary>
        /// The configured token as every confirmation digests it: surrounding whitespace removed. The
        /// confirmation writer, <see cref="Confirms"/> and <see cref="Resolve"/> all use this one form, so a
        /// token pasted with a trailing space is confirmed and allowed as the token it is.
        /// </summary>
        public static string Normalize(string token) => token == null ? string.Empty : token.Trim();

        /// <summary>True when <paramref name="confirmation"/> is complete, open, heartbeat-scoped and its digest is the (normalised) token's.</summary>
        public static bool Confirms(BuildGuardConfirmation confirmation, string token)
        {
            token = Normalize(token);
            return confirmation != null
                && !string.IsNullOrEmpty(token)
                && confirmation.scope == HeartbeatScope
                && confirmation.registrationMode == OpenRegistrationMode
                && !string.IsNullOrWhiteSpace(confirmation.confirmedAt)
                && confirmation.appPublicId != null && confirmation.appPublicId.StartsWith(PublicIdPrefix, StringComparison.Ordinal)
                && string.Equals(confirmation.tokenDigest, Digest(token), StringComparison.Ordinal);
        }

        /// <summary>Allows each configured token that a confirmation matches; reports every other one as <c>secret_literal</c>.</summary>
        public static BuildGuardTokenResolution Resolve(IEnumerable<BuildGuardConfiguredToken> configured, IReadOnlyList<BuildGuardConfirmation> confirmations)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            var findings = new List<BuildGuardFinding>();
            if (configured == null)
            {
                return BuildGuardTokenResolution.None;
            }

            foreach (BuildGuardConfiguredToken entry in configured)
            {
                string token = Normalize(entry.Token);
                if (token.Length == 0)
                {
                    continue;
                }

                bool confirmed = false;
                if (confirmations != null)
                {
                    foreach (BuildGuardConfirmation confirmation in confirmations)
                    {
                        confirmed |= Confirms(confirmation, token);
                    }
                }

                if (confirmed)
                {
                    allowed.Add(token);
                }
                else
                {
                    findings.Add(new BuildGuardFinding(BuildGuardReason.SecretLiteral, entry.AssetPath, UnconfirmedDetail));
                }
            }

            return new BuildGuardTokenResolution(allowed, findings);
        }

        /// <summary>
        /// Builds a confirmation for a token the PingCore API reported as open-registration and
        /// heartbeat-scoped. For the Editor plugin's confirmation writer and tests only.
        /// </summary>
        internal static BuildGuardConfirmation Create(string token, string appPublicId, DateTime confirmedAtUtc) =>
            new BuildGuardConfirmation
            {
                appPublicId = appPublicId,
                tokenDigest = Digest(Normalize(token)),
                scope = HeartbeatScope,
                registrationMode = OpenRegistrationMode,
                confirmedAt = confirmedAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };
    }

    /// <summary>Reads and writes <c>ProjectSettings/PingCoreBuildGuard.json</c>.</summary>
    public static class BuildGuardConfirmationFile
    {
        public const string RelativePath = "ProjectSettings/PingCoreBuildGuard.json";

        public static string GetPath(string projectRoot) => Path.Combine(projectRoot, RelativePath);

        /// <summary>The recorded confirmations; empty when the file is missing or unreadable, so every token stays unconfirmed.</summary>
        public static IReadOnlyList<BuildGuardConfirmation> Read(string projectRoot)
        {
            string path = GetPath(projectRoot);
            if (!File.Exists(path))
            {
                return Array.Empty<BuildGuardConfirmation>();
            }

            try
            {
                BuildGuardConfirmationRecord record = JsonUtility.FromJson<BuildGuardConfirmationRecord>(File.ReadAllText(path));
                return record?.confirmations ?? Array.Empty<BuildGuardConfirmation>();
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning("[PingCore build guard] " + RelativePath + " could not be read (" + e.GetType().Name
                    + "); every configured heartbeat token is treated as unconfirmed.");
                return Array.Empty<BuildGuardConfirmation>();
            }
        }

        /// <summary>Writes the record. It carries digests only. For the Editor plugin's confirmation writer and tests.</summary>
        internal static void Write(string projectRoot, BuildGuardConfirmationRecord record)
        {
            string path = GetPath(projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(record ?? new BuildGuardConfirmationRecord(), true));
        }
    }
}
