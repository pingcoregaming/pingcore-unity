using System;

namespace PingCore.Discovery.Client
{
    /// <summary>A typed meta comparison on the server list and in quick-join filters.</summary>
    public enum MetaOp
    {
        /// <summary><c>eq</c>: case-insensitive string equality (the explicit spelling of the exact match).</summary>
        Eq,

        /// <summary><c>ne</c>: numeric-aware inequality.</summary>
        Ne,

        /// <summary><c>gt</c>: numeric greater-than.</summary>
        Gt,

        /// <summary><c>ge</c>: numeric greater-or-equal.</summary>
        Ge,

        /// <summary><c>lt</c>: numeric less-than.</summary>
        Lt,

        /// <summary><c>le</c>: numeric less-or-equal.</summary>
        Le,

        /// <summary><c>contains</c>: case-insensitive substring.</summary>
        Contains,

        /// <summary><c>in</c>: one of a comma-separated list.</summary>
        In,
    }

    /// <summary>The server list's built-in sort keys.</summary>
    public enum ServerSort
    {
        /// <summary><c>players</c> (the default, descending).</summary>
        Players,

        /// <summary><c>name</c>.</summary>
        Name,

        /// <summary><c>updatedAt</c>.</summary>
        UpdatedAt,

        /// <summary><c>latency</c>: by the caller's measured latency; needs <see cref="ServerListQuery.WithLatency"/>.</summary>
        Latency,
    }

    /// <summary>Wire spellings of <see cref="MetaOp"/>.</summary>
    internal static class MetaOps
    {
        public static string Wire(MetaOp op)
        {
            switch (op)
            {
                case MetaOp.Eq: return "eq";
                case MetaOp.Ne: return "ne";
                case MetaOp.Gt: return "gt";
                case MetaOp.Ge: return "ge";
                case MetaOp.Lt: return "lt";
                case MetaOp.Le: return "le";
                case MetaOp.Contains: return "contains";
                case MetaOp.In: return "in";
                default: throw new ArgumentOutOfRangeException(nameof(op));
            }
        }
    }
}
