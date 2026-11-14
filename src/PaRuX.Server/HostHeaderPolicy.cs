using System.Net;

namespace PaRuX.Server;

/// <summary>
/// SEC-F10: Host-header allow-list that stops DNS rebinding against the Core API. A page on an attacker's domain whose
/// name is rebound to 127.0.0.1 (or to this PC's LAN address) still sends <c>Host: attacker.example</c>, so a request
/// whose Host is not on this list is refused before any authentication, health or sign-in route runs.
/// Allowed: the loopback names, an IP literal that is a loopback address or an address this machine has bound, the LAN
/// certificate name <c>parux.local</c>, and operator-configured names (reverse proxy, container host alias) from the
/// <c>PARUX_ALLOWED_HOSTS</c> environment variable (comma or semicolon separated) or one name per line in
/// <c>allowed-hosts.txt</c> in the server state folder. Ports are ignored: only the name identifies the origin that
/// rebinding abuses, and a reverse proxy normally drops the port.
/// </summary>
internal sealed class HostHeaderPolicy
{
    private static readonly string[] BuiltInNames = { "localhost", "parux.local" };
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly Func<IEnumerable<string>> _configuredNames;
    private readonly Func<IReadOnlyCollection<IPAddress>> _boundAddresses;
    private readonly Func<DateTime> _utcNow;
    // _gate guards the published sets and the refresh bookkeeping only. It is never held across a file or adapter read:
    // readers take it for a field read, so a slow refresh cannot stall the request path.
    private readonly object _gate = new();
    private HashSet<string> _configured = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<IPAddress> _bound = new();
    private DateTime _refreshedUtc = DateTime.MinValue;
    private bool _refreshing;

    public HostHeaderPolicy() : this(DefaultConfiguredNames, RemoteAccessService.GetPrivateIpv4Addresses, () => DateTime.UtcNow) { }

    internal HostHeaderPolicy(Func<IEnumerable<string>> configuredNames, Func<IReadOnlyCollection<IPAddress>> boundAddresses, Func<DateTime> utcNow)
    {
        _configuredNames = configuredNames;
        _boundAddresses = boundAddresses;
        _utcNow = utcNow;
    }

    /// <param name="hostHeader">The raw Host header value (with optional port).</param>
    /// <param name="localAddress">The local address the connection arrived on, when known.</param>
    internal bool IsAllowed(string? hostHeader, IPAddress? localAddress)
    {
        if (!TryGetHostName(hostHeader, out string name)) return false;
        if (Array.Exists(BuiltInNames, n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) || IsThisMachineName(name)) return true;

        if (IPAddress.TryParse(name, out IPAddress? literal))
        {
            if (literal.IsIPv4MappedToIPv6) literal = literal.MapToIPv4();
            if (IPAddress.IsLoopback(literal)) return true;
            if (localAddress is not null)
            {
                IPAddress local = localAddress.IsIPv4MappedToIPv6 ? localAddress.MapToIPv4() : localAddress;
                if (local.Equals(literal)) return true;
            }
            Refresh();
            lock (_gate) return _bound.Contains(literal);
        }

        Refresh();
        lock (_gate) return _configured.Contains(name);
    }

    internal static bool TryGetHostName(string? hostHeader, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(hostHeader) || hostHeader.Length > 255) return false;
        string value = hostHeader.Trim();
        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']');
            if (close < 0) return false;
            string inner = value[1..close];
            string rest = value[(close + 1)..];
            if (rest.Length > 0 && !(rest[0] == ':' && rest.Length > 1 && rest[1..].All(char.IsAsciiDigit))) return false;
            if (!IPAddress.TryParse(inner, out _)) return false;
            name = inner;
            return true;
        }
        int colon = value.LastIndexOf(':');
        if (colon >= 0)
        {
            if (value.IndexOf(':') != colon) return false; // a bare IPv6 literal must be bracketed
            string port = value[(colon + 1)..];
            if (port.Length == 0 || !port.All(char.IsAsciiDigit)) return false;
            value = value[..colon];
        }
        value = value.TrimEnd('.');
        if (value.Length == 0 || value.Any(c => c <= ' ' || c is '/' or '\\' or '@' or '?' or '#')) return false;
        name = value;
        return true;
    }

    /// <summary>This machine's NetBIOS/DNS name and its <c>.local</c> form, allowed by default so a LAN client can use them.</summary>
    private static bool IsThisMachineName(string name)
    {
        string machine = Environment.MachineName;
        if (string.IsNullOrEmpty(machine)) return false;
        return name.Equals(machine, StringComparison.OrdinalIgnoreCase)
            || name.Equals(machine + ".local", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rebuilds the sets at most once per interval. The file and adapter reads run outside <c>_gate</c>; only one refresh
    /// is in flight at a time, and any other caller returns at once and keeps using the previously published sets.
    /// </summary>
    private void Refresh()
    {
        DateTime now = _utcNow();
        lock (_gate)
        {
            if (_refreshing || now - _refreshedUtc < RefreshInterval) return;
            _refreshedUtc = now;
            _refreshing = true;
        }

        HashSet<string>? names = null;
        HashSet<IPAddress>? bound = null;
        try
        {
            try
            {
                var built = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in _configuredNames())
                    if (TryGetHostName(raw, out string configured) && !IPAddress.TryParse(configured, out _)) built.Add(configured);
                names = built;
            }
            catch (Exception) { /* keep the previous list: an unreadable file must not open the gate */ }
            try { bound = new HashSet<IPAddress>(_boundAddresses()); }
            catch (Exception) { /* keep the previous list */ }
        }
        finally
        {
            lock (_gate)
            {
                if (names is not null) _configured = names;
                if (bound is not null) _bound = bound;
                _refreshing = false;
            }
        }
    }

    private static IEnumerable<string> DefaultConfiguredNames()
    {
        foreach (string item in (Environment.GetEnvironmentVariable("PARUX_ALLOWED_HOSTS") ?? string.Empty)
                     .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return item;
        string path = Path.Combine(ServerState.StateDirectory, "allowed-hosts.txt");
        if (!File.Exists(path)) yield break;
        foreach (string line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#')) yield return trimmed;
        }
    }
}
