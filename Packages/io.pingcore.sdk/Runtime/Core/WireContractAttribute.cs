using System;

namespace PingCore.Core
{
    /// <summary>
    /// Ties a wire DTO to one operation of a pinned contract snapshot under <c>contracts/</c>.
    /// The Editor DTO dump emits these tuples and a contract checker compares the dump with the
    /// pinned specs, walking the matching schema, so a DTO without this attribute is invisible to drift detection.
    /// A DTO that models several operations carries one attribute per operation.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class WireContractAttribute : Attribute
    {
        /// <summary>Contract source for Discovery (<c>contracts/discovery/openapi.json</c>).</summary>
        public const string Discovery = "discovery";

        /// <summary>Contract source for the local SDK endpoint (<c>contracts/supervisor/sdk-openapi.json</c>).</summary>
        public const string Supervisor = "supervisor";

        /// <param name="source"><see cref="Discovery"/> or <see cref="Supervisor"/>.</param>
        /// <param name="method">HTTP method in upper case, for example <c>GET</c>.</param>
        /// <param name="path">The path exactly as the spec writes it, for example <c>/v1/apps/{publicId}/tickets</c>.</param>
        /// <param name="direction">Request, success response or error body.</param>
        /// <param name="status">HTTP status of a response or error body; 0 for a request body.</param>
        public WireContractAttribute(string source, string method, string path, WireDirection direction, int status)
        {
            Source = source;
            Method = method;
            Path = path;
            Direction = direction;
            Status = status;
        }

        /// <summary><see cref="Discovery"/> or <see cref="Supervisor"/>.</summary>
        public string Source { get; }

        /// <summary>HTTP method in upper case.</summary>
        public string Method { get; }

        /// <summary>The spec path template.</summary>
        public string Path { get; }

        /// <summary>Request, success response or error body.</summary>
        public WireDirection Direction { get; }

        /// <summary>HTTP status; 0 for a request body.</summary>
        public int Status { get; }
    }
}
