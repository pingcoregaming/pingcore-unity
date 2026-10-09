using PingCore.Core;

namespace PingCore.Fleet.Wire
{
    /// <summary>
    /// The empty object <c>{}</c> the local SDK endpoint answers a lifecycle write with, among them
    /// <c>POST /allocate</c> (the self-allocation, whose id arrives on the watch stream instead). The snapshot spec gives
    /// that answer no schema, so this type carries no <c>[WireContract]</c>; its body is pinned in
    /// <c>Tests/Editor/Fleet/Bodies/allocate.json</c>. A key the supervisor adds fails the strict round trip there.
    /// </summary>
    [Preserve]
    internal sealed class LocalSdkEmpty
    {
    }
}
