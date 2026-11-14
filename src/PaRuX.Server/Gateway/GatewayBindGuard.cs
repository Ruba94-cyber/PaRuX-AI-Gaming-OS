using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PaRuX.Server.Gateway;

/// <summary>
/// Keeps the Core alive when the optional Local AI Gateway port cannot be bound. <see cref="GatewaySettings.IsPortFree"/> probes the
/// port before Kestrel starts, but another program can take it in the gap; Kestrel would then fail the whole host start and the Core
/// would exit. This wraps the socket transport so that only a failed bind of the gateway port is absorbed: the endpoint gets an idle
/// listener, the failure is reported once through a typed reason, and every other endpoint (the Core port, remote access) still
/// fails loudly. Nothing listens on the gateway port after a failure, so the gateway is honestly off.
/// </summary>
internal static class GatewayBindGuard
{
    /// <summary>Typed reason shown in the gateway status when the port was free at the probe but could not be bound.</summary>
    internal const string BindFailedReason = "bind_failed";

    /// <summary>Typed reason when the probe already found the port taken.</summary>
    internal const string PortInUseReason = "port_in_use";

    internal static void Install(IServiceCollection services, int gatewayPort, Action<string> onUnavailable)
    {
        services.AddSingleton<IConnectionListenerFactory>(provider => new TolerantListenerFactory(
            new SocketTransportFactory(provider.GetRequiredService<IOptions<SocketTransportOptions>>(),
                provider.GetRequiredService<ILoggerFactory>()),
            gatewayPort, onUnavailable));
    }

    internal sealed class TolerantListenerFactory(IConnectionListenerFactory inner, int gatewayPort, Action<string> onUnavailable)
        : IConnectionListenerFactory, IConnectionListenerFactorySelector
    {
        public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.BindAsync(endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && endpoint is IPEndPoint { } ip && ip.Port == gatewayPort)
            {
                // The gateway is reached on 127.0.0.1; a failed ::1 bind alone does not take it down, but is absorbed the same way.
                if (Equals(ip.Address, IPAddress.Loopback)) onUnavailable(BindFailedReason);
                return new IdleListener(endpoint);
            }
        }

        public bool CanBind(EndPoint endpoint) =>
            inner is not IConnectionListenerFactorySelector selector || selector.CanBind(endpoint);
    }

    /// <summary>A listener that never accepts anything; it ends when Kestrel unbinds or disposes it.</summary>
    private sealed class IdleListener(EndPoint endpoint) : IConnectionListener
    {
        private readonly TaskCompletionSource<ConnectionContext?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EndPoint EndPoint { get; } = endpoint;

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            try { return await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            _closed.TrySetResult(null);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _closed.TrySetResult(null);
            return ValueTask.CompletedTask;
        }
    }
}
