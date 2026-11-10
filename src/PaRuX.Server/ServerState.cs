using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Knowledge;

namespace PaRuX.Server;

public sealed class ServerState
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public const string DefaultEndpoint = "http://127.0.0.1:51123";

    public const int PreferredPort = 51123;

    /// <summary>Port Core actually listens on. 51123 unless Windows reserved it (Hyper-V/WSL2/Docker excluded
    /// port ranges) or another program owns it; then a free loopback port is used and published in the bootstrap file.</summary>
    public static int CorePort { get; private set; } = PreferredPort;

    public string Endpoint => "http://127.0.0.1:" + CorePort.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Picks the Core port by really binding it on both loopback families, which also detects
    /// Windows excluded port ranges that an active-listener scan cannot see.</summary>
    public static int ChooseCorePort()
    {
        CorePort = CanBindLoopback(PreferredPort) ? PreferredPort : FindFreeLoopbackPort();
        return CorePort;
    }

    private static bool CanBindLoopback(int port)
    {
        foreach (System.Net.IPAddress address in new[] { System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback })
        {
            try
            {
                using var probe = new System.Net.Sockets.TcpListener(address, port);
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

    private static int FindFreeLoopbackPort()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            if (CanBindLoopback(port)) return port;
        }
        return PreferredPort;
    }
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string StateDirectory => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "server");

    public static string BootstrapPath => Path.Combine(StateDirectory, "bootstrap.json");

    public void Publish()
    {
        Directory.CreateDirectory(StateDirectory);
        // Corporate portable data may be on a shared path rather than the protected
        // per-user profile. Never publish a bearer token into a world-readable folder.
        // On volumes without per-user ACLs (e.g. FAT/exFAT), Corporate refuses
        // startup with an actionable message instead of leaking the Core credential.
        if (CorporateMode.IsActive && OperatingSystem.IsWindows() &&
            (!LocalFileProtection.RestrictToCurrentUser(PaRuXDataRoot.Path)
                || !LocalFileProtection.RestrictToCurrentUser(StateDirectory)))
        {
            throw new UnauthorizedAccessException(
                "Corporate cannot protect its local server data. Move the extracted PaRuX folder " +
                "to an NTFS directory owned by your Windows account and try again.");
        }
        var bootstrap = new ServerBootstrap
        {
            Endpoint = Endpoint,
            Token = Token,
            ProcessId = Environment.ProcessId,
            StartedUtc = DateTime.UtcNow
        };

        PublishAtomically(BootstrapPath, bootstrap);
        if (CorporateMode.IsActive && OperatingSystem.IsWindows() &&
            !LocalFileProtection.RestrictToCurrentUser(BootstrapPath))
        {
            RemoveIfOwned();
            throw new UnauthorizedAccessException(
                "Corporate could not protect bootstrap.json. The local Core token must never " +
                "be exposed to other Windows users.");
        }
        // Remove the pre-1.5.1 fixed-name staging file if a prior interrupted promotion left it.
        try { File.Delete(BootstrapPath + ".tmp"); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void PublishAtomically(string destinationPath, ServerBootstrap bootstrap)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporary = destinationPath + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(bootstrap, JsonOptions));

        // Desktop polls the previous bootstrap while Core starts. Antivirus/indexers can also
        // hold a transient read handle. Promotion must remain atomic, but a single sharing
        // violation must not leave a healthy Core paired with stale credentials/PID forever.
        const int attempts = 20;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(destinationPath))
                    File.Replace(temporary, destinationPath, destinationBackupFileName: null);
                else
                    File.Move(temporary, destinationPath);
                break;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(25);
            }
        }
    }

    public void RemoveIfOwned()
    {
        try
        {
            if (!File.Exists(BootstrapPath)) return;
            ServerBootstrap? value = JsonSerializer.Deserialize<ServerBootstrap>(File.ReadAllText(BootstrapPath));
            if (value?.ProcessId == Environment.ProcessId) File.Delete(BootstrapPath);
        }
        catch
        {
            // A stale bootstrap is harmless and will be replaced on the next start.
        }
    }
}
