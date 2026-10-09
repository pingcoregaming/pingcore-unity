using System;
using System.Threading.Tasks;
using BeaconRush.Client.Models;
using PingCore.Discovery.Client;
using UnityEngine;

namespace BeaconRush.Client.UI
{
    /// <summary>
    /// The missing-infrastructure banner: the SDK's <see cref="InfrastructureCheck"/> on the fleet app at launch, before
    /// any join, on Check again, and after a failed join (<see cref="InfrastructureBanner"/>). One check at a time; a
    /// caller that arrives while one runs waits for it.
    /// </summary>
    public sealed partial class ClientUi
    {
        private Task<InfrastructureReport> infrastructureCheck;

        /// <summary>The last check's report, or null before the first one finished.</summary>
        internal InfrastructureReport Infrastructure { get; private set; }

        /// <summary>True while a check runs.</summary>
        internal bool CheckingInfrastructure => infrastructureCheck != null && !infrastructureCheck.IsCompleted;

        /// <summary>Runs the check again (the menu's Check again).</summary>
        internal void CheckInfrastructure() => _ = CheckInfrastructureAsync();

        private Task<InfrastructureReport> CheckInfrastructureAsync()
        {
            if (!CheckingInfrastructure)
            {
                infrastructureCheck = RunInfrastructureCheckAsync();
            }

            return infrastructureCheck;
        }

        private async Task<InfrastructureReport> RunInfrastructureCheckAsync()
        {
            try
            {
                string appId = InfrastructureBanner.AppIdToCheck(services.PublicIdFor(ClientApp.Fleet));
                DiscoveryClient client = services.IsConfigured(ClientApp.Fleet) ? services.For(ClientApp.Fleet) : null;
                InfrastructureReport report = await InfrastructureCheck.RunAsync(appId, client, null, lifetime.Token);
                Infrastructure = report;
                if (!report.IsOk)
                {
                    Debug.LogWarning("[ClientUi] PingCore check: " + report.State + ": " + report);
                }

                return report;
            }
            catch (OperationCanceledException)
            {
                return Infrastructure;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ClientUi] the PingCore check failed: " + e.GetType().Name);
                return Infrastructure;
            }
        }

        /// <summary>A failed join's text: the check's message when it explains the failure, else <paramref name="joinText"/>.</summary>
        private async Task<string> JoinFailureTextAsync(string joinText)
        {
            InfrastructureReport report = await CheckInfrastructureAsync();
            return InfrastructureBanner.JoinFailure(report, joinText);
        }
    }
}
