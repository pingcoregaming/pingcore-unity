using System;
using PingCore.Core;

namespace PingCore.Discovery.Client
{
    /// <summary>Which kind of player token a client holds.</summary>
    public enum PlayerTokenKind
    {
        /// <summary>Issued by Discovery (<c>POST .../player-tokens</c>), HS256, player id <c>anon:&lt;uuid&gt;</c>.</summary>
        Anonymous,

        /// <summary>Studio-signed ES256, set with <see cref="PlayerTokenCache.SetSignedToken"/> or a provider.</summary>
        Signed,
    }

    /// <summary>
    /// A player token. The token itself is a bearer secret and is not public: <see cref="ToString"/>
    /// shows the kind, player id and expiry only.
    /// </summary>
    public sealed class PlayerToken
    {
        internal PlayerToken(PlayerTokenKind kind, string value, string playerId, DateTimeOffset expiresAt)
        {
            Kind = kind;
            Value = value;
            PlayerId = playerId;
            ExpiresAt = expiresAt;
        }

        /// <summary>Anonymous or studio-signed.</summary>
        public PlayerTokenKind Kind { get; }

        /// <summary>The token's <c>sub</c>: the player id Discovery sees.</summary>
        public string PlayerId { get; }

        /// <summary>When the token expires.</summary>
        public DateTimeOffset ExpiresAt { get; }

        /// <summary>The compact JWT. Internal: it goes only into the <c>Authorization</c> header.</summary>
        internal string Value { get; }

        /// <inheritdoc />
        public override string ToString() => $"PlayerToken({Kind}, playerId={PlayerId}, expiresAt={ExpiresAt.ToUnixTimeMilliseconds()})";
    }

    /// <summary>What changed in a <see cref="PlayerTokenCache"/>.</summary>
    public enum PlayerTokenChange
    {
        /// <summary>The first anonymous token this cache issued.</summary>
        Issued,

        /// <summary>An anonymous token loaded from the token store.</summary>
        Loaded,

        /// <summary>A new anonymous token replacing an expired or rejected one.</summary>
        Refreshed,

        /// <summary>Discovery rejected the token (401 with a player-token reason); it was dropped.</summary>
        Rejected,

        /// <summary>A studio-signed token was set, directly or from the provider.</summary>
        SignedSet,

        /// <summary>
        /// The new token is for another player id than the one before it (raised after the Issued, Refreshed,
        /// Loaded or SignedSet that brought it; <see cref="PlayerTokenEvent.PreviousPlayerId"/> is the old one).
        /// A re-issued anonymous token always is: Discovery mints a new <c>anon:</c> id for it. What the old
        /// player id held, its reservations and its queued tickets, the new one cannot read, release, poll or
        /// cancel; the SDK does not resend such a call after a 401 (<c>DiscoveryCallResult.IdentityChanged</c>).
        /// </summary>
        IdentityChanged,
    }

    /// <summary>A <see cref="PlayerTokenCache.Changed"/> notification. Never carries the token.</summary>
    public sealed class PlayerTokenEvent
    {
        internal PlayerTokenEvent(PlayerTokenChange change, PlayerTokenKind kind, string playerId, DateTimeOffset? expiresAt, DiscoveryReason reason, string previousPlayerId = null)
        {
            Change = change;
            Kind = kind;
            PlayerId = playerId;
            ExpiresAt = expiresAt;
            Reason = reason;
            PreviousPlayerId = previousPlayerId;
        }

        /// <summary>What changed.</summary>
        public PlayerTokenChange Change { get; }

        /// <summary>The kind of the token concerned.</summary>
        public PlayerTokenKind Kind { get; }

        /// <summary>The player id of the token concerned.</summary>
        public string PlayerId { get; }

        /// <summary>Its expiry, or null.</summary>
        public DateTimeOffset? ExpiresAt { get; }

        /// <summary>For <see cref="PlayerTokenChange.Rejected"/>, Discovery's reason; otherwise Unknown.</summary>
        public DiscoveryReason Reason { get; }

        /// <summary>For <see cref="PlayerTokenChange.IdentityChanged"/>, the player id before the change; otherwise null.</summary>
        public string PreviousPlayerId { get; }

        /// <inheritdoc />
        public override string ToString() => $"{Change} {Kind} playerId={PlayerId}"
            + (PreviousPlayerId != null ? " previousPlayerId=" + PreviousPlayerId : string.Empty)
            + (Reason != DiscoveryReason.Unknown ? " reason=" + DiscoveryReasons.ToWireValue(Reason) : string.Empty);
    }
}
