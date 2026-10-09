namespace PingCore.Core
{
    /// <summary>Which side of an HTTP exchange a wire DTO models.</summary>
    public enum WireDirection
    {
        /// <summary>A body the SDK sends.</summary>
        Request = 0,

        /// <summary>A success body the SDK receives.</summary>
        Response = 1,

        /// <summary>An error body the SDK receives.</summary>
        Error = 2,
    }
}
