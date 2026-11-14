using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace PaRuX.Server.Gateway;

/// <summary>
/// The Universal Gateway listener: a loopback-only port that serves exactly the model APIs
/// (Anthropic Messages, OpenAI Responses, OpenAI Chat Completions, models) and nothing of
/// PaRuX's control plane. External agents (Claude Code, Codex, OpenCode, generic clients) get a
/// clean base URL; the Core port keeps serving the desktop app. LAN exposure is never automatic.
/// </summary>
public sealed class GatewaySettings
{
    public const int DefaultPort = 11435;

    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = DefaultPort;
    /// <summary>Model id used for vendor ids (claude-*, gpt-*) sent by clients: parux-auto by default.</summary>
    public string VendorModelDefault { get; set; } = GatewayModelAliases.Auto;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string FilePath(string stateDirectory) => Path.Combine(stateDirectory, "gateway.json");

    public static GatewaySettings Load(string stateDirectory)
    {
        try
        {
            string path = FilePath(stateDirectory);
            if (!File.Exists(path)) return new GatewaySettings();
            GatewaySettings? loaded = JsonSerializer.Deserialize<GatewaySettings>(File.ReadAllText(path), JsonOptions);
            return loaded == null ? new GatewaySettings() : loaded.Normalized();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new GatewaySettings();
        }
    }

    public void Save(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        string path = FilePath(stateDirectory);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Normalized(), JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public GatewaySettings Normalized()
    {
        if (Port is < 1024 or > 65535 || Port == 51123) Port = DefaultPort;
        if (GatewayModelAliases.Resolve(VendorModelDefault, GatewayModelAliases.Auto) == null
            || GatewayModelAliases.IsVendorModelId(VendorModelDefault))
            VendorModelDefault = GatewayModelAliases.Auto;
        return this;
    }

    /// <summary>True when Kestrel can bind the loopback port. The listener table alone is not enough: a socket that is
    /// bound but not yet (or not visibly) listening, for example another PaRuX Core starting or running under a
    /// different data root, is missing from it, and a failed Kestrel bind would then stop the whole Core. So the port
    /// is also probed with a real bind on both loopback families, the same way the Core port is chosen.</summary>
    public static bool IsPortFree(int port)
    {
        try
        {
            if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port))
                return false;
        }
        catch (NetworkInformationException)
        {
            // Fall through to the bind probe.
        }
        foreach (IPAddress address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            try
            {
                var probe = new System.Net.Sockets.TcpListener(address, port);
                probe.Start();
                probe.Stop();
            }
            catch (System.Net.Sockets.SocketException e)
                when (e.SocketErrorCode is System.Net.Sockets.SocketError.AddressFamilyNotSupported
                    or System.Net.Sockets.SocketError.AddressNotAvailable
                    or System.Net.Sockets.SocketError.ProtocolNotSupported)
            {
                // IPv6 loopback may be disabled; Kestrel's localhost binding tolerates that too.
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Paths reachable on the gateway port. Everything else, including the whole /api control plane
    /// and the legacy Ollama emulation, is answered with 404 so a client key or a mis-pointed tool
    /// can never reach PaRuX's settings, folders, LAN or Computer Use surfaces through this port.
    /// </summary>
    public static bool IsGatewayPath(PathString path) =>
        path.StartsWithSegments("/v1") || path == "/health";
}
