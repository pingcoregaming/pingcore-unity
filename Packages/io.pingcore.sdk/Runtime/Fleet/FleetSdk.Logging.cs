using System;

namespace PingCore.Fleet
{
    /// <summary>Logging through <see cref="FleetSdkOptions.Log"/> and event raising that a throwing sink or handler cannot break.</summary>
    public sealed partial class FleetSdk
    {
        private void LogAnswer(string call, LocalSdkAnswer answer, bool logSuccess)
        {
            switch (answer.Outcome)
            {
                case FleetCallOutcome.Ok:
                    if (logSuccess)
                    {
                        Log(FleetLogLevel.Info, call, "accepted", answer.Status, answer.Outcome);
                    }

                    break;
                case FleetCallOutcome.Cancelled:
                    break;
                case FleetCallOutcome.EndpointClosed:
                    Log(FleetLogLevel.Info, call, "refused: the local SDK endpoint is closed (expected while the container stops)", answer.Status, answer.Outcome);
                    break;
                case FleetCallOutcome.Unreachable:
                    Log(FleetLogLevel.Error, call, answer.Message, answer.Status, answer.Outcome);
                    break;
                default:
                    Log(FleetLogLevel.Warning, call, answer.Message, answer.Status, answer.Outcome);
                    break;
            }
        }

        private void Log(FleetLogLevel level, string call, string message, int status, FleetCallOutcome? outcome)
        {
            SafeLog(log, new FleetLogEntry(level, call, message, status, outcome));
        }

        private static void SafeLog(Action<FleetLogEntry> sink, FleetLogEntry entry)
        {
            try
            {
                sink?.Invoke(entry);
            }
            catch (Exception)
            {
                // A throwing log sink must never break the shim.
            }
        }

        private void Raise<T>(Action<T> handler, T value, string name)
        {
            if (handler == null)
            {
                return;
            }

            foreach (Delegate single in handler.GetInvocationList())
            {
                try
                {
                    ((Action<T>)single)(value);
                }
                catch (Exception e)
                {
                    Log(FleetLogLevel.Error, "event", "a " + name + " handler threw " + e.GetType().Name + ": " + e.Message, 0, null);
                }
            }
        }
    }
}
