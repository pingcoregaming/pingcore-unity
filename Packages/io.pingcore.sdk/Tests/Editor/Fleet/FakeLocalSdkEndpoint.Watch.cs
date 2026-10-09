using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PingCore.Fleet.Tests.Editor
{
    /// <summary>The fake's watch streams: chunked NDJSON, one <c>{"result": view}</c> line at once and one per change.</summary>
    internal sealed partial class FakeLocalSdkEndpoint
    {
        private readonly List<FakeHttpConnection> watchers = new List<FakeHttpConnection>();

        public int OpenWatches
        {
            get
            {
                lock (gate)
                {
                    watchers.RemoveAll(w => w.Ended);
                    return watchers.Count;
                }
            }
        }

        /// <summary>Writes the current view as one frame to every open watch stream.</summary>
        public void PushView()
        {
            string frame;
            lock (gate)
            {
                frame = Frame(BuildViewLocked());
            }

            WriteToWatchers(Encoding.UTF8.GetBytes(frame));
        }

        /// <summary>Writes a raw line (plus LF) to every open watch stream.</summary>
        public void InjectWatchLine(string raw) => WriteToWatchers(Encoding.UTF8.GetBytes(raw + "\n"));

        /// <summary>
        /// Ends every open watch stream, abortively (a connection reset) or with a clean end of stream, and
        /// keeps listening, so the shim reconnects. Not a container stop: see <see cref="CloseEndpoint"/>.
        /// </summary>
        public void DropWatchOnly(bool abort = true)
        {
            List<FakeHttpConnection> open;
            lock (gate)
            {
                open = watchers.ToList();
                watchers.Clear();
            }

            foreach (FakeHttpConnection watcher in open)
            {
                watcher.End(abort);
            }
        }

        private void OpenWatch(FakeHttpConnection connection)
        {
            connection.BeginStream("application/json");
            string first;
            lock (gate)
            {
                watchers.Add(connection);
                first = Frame(BuildViewLocked());
            }

            connection.WriteChunk(Encoding.UTF8.GetBytes(first));
        }

        private void WriteToWatchers(byte[] bytes)
        {
            List<FakeHttpConnection> open;
            lock (gate)
            {
                open = watchers.ToList();
            }

            foreach (FakeHttpConnection watcher in open)
            {
                watcher.WriteChunk(bytes);
            }

            lock (gate)
            {
                watchers.RemoveAll(w => w.Ended);
            }
        }

        private static string Frame(JObject view) => new JObject { ["result"] = view }.ToString(Formatting.None) + "\n";
    }
}
