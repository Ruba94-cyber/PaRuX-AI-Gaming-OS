using System;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Holder of the discovery policy client used by Settings. The production default talks to PaRuX Core;
    /// <see cref="StubDiscoveryPolicyClient"/> is for tests and design-time only and is installed explicitly.
    /// </summary>
    public static class DiscoveryPolicyClient
    {
        private static IDiscoveryPolicyClient? _current;
        private static readonly object Gate = new();

        public static IDiscoveryPolicyClient Current
        {
            get
            {
                lock (Gate)
                {
                    return _current ??= CreateProductionClient();
                }
            }
            set
            {
                lock (Gate)
                {
                    _current = value ?? throw new ArgumentNullException(nameof(value));
                }
            }
        }

        private static IDiscoveryPolicyClient CreateProductionClient() =>
            new CoreDiscoveryPolicyClient(
                PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(10)),
                async ct =>
                {
                    ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
                    return (server.Endpoint, server.Token);
                });
    }
}
