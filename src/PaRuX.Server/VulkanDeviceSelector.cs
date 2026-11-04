using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Picks the llama.cpp Vulkan device for the text runtime. Vulkan enumerates every adapter, and the
/// integrated GPU is often <c>Vulkan0</c> (measured: Radeon 780M as Vulkan0, RTX 5080 as Vulkan1,
/// about 25 vs about 200 tokens/s on the same model), so a hard-coded index silently runs on the
/// slow device. The choice is the adapter the Windows GPU inventory reports with the most dedicated
/// video memory; without a usable match the previous behaviour (Vulkan0) is kept.
/// </summary>
internal static partial class VulkanDeviceSelector
{
    internal const string DefaultDevice = "Vulkan0";

    internal readonly record struct VulkanDevice(int Index, string Name, long TotalMib);

    [GeneratedRegex(@"^\s*Vulkan(?<i>\d+):\s*(?<name>.+?)\s*\((?<total>\d+)\s*MiB", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();

    /// <summary>Parses <c>llama-server --list-devices</c> output; non-Vulkan and malformed lines are ignored.</summary>
    internal static List<VulkanDevice> ParseDevices(string? output)
    {
        var devices = new List<VulkanDevice>();
        if (string.IsNullOrWhiteSpace(output)) return devices;
        foreach (string line in output.Split('\n'))
        {
            Match m = DeviceLine().Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups["i"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) continue;
            long.TryParse(m.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long total);
            devices.Add(new VulkanDevice(index, m.Groups["name"].Value.Trim(), total));
        }
        return devices;
    }

    /// <summary>Chooses the device matching the inventory adapter with the most dedicated memory.</summary>
    internal static string Choose(IReadOnlyList<VulkanDevice> devices, GpuInventoryStatus? inventory)
    {
        if (devices.Count <= 1 || inventory is not { Available: true }) return DefaultDevice;
        GpuAdapterStatus? best = inventory.Adapters
            .Where(a => !a.IsSoftware && a.DedicatedVideoMemoryBytes > 0 && !string.IsNullOrWhiteSpace(a.Name))
            .OrderByDescending(a => a.DedicatedVideoMemoryBytes)
            .FirstOrDefault();
        if (best == null) return DefaultDevice;
        foreach (VulkanDevice device in devices)
        {
            if (NamesMatch(device.Name, best.Name)) return "Vulkan" + device.Index.ToString(CultureInfo.InvariantCulture);
        }
        // No name matched: the device whose memory is closest to the adapter's dedicated memory (within 15%) is the same card.
        long wantedMib = best.DedicatedVideoMemoryBytes / (1024 * 1024);
        VulkanDevice? close = devices
            .Where(d => d.TotalMib > 0 && Math.Abs(d.TotalMib - wantedMib) <= wantedMib * 0.15)
            .OrderBy(d => Math.Abs(d.TotalMib - wantedMib)).Cast<VulkanDevice?>().FirstOrDefault();
        return close is { } match ? "Vulkan" + match.Index.ToString(CultureInfo.InvariantCulture) : DefaultDevice;
    }

    /// <summary>Windows names carry trademark marks the Vulkan driver omits ("Radeon(TM) RX 7900" vs "Radeon RX 7900"), so both are
    /// compared as plain letters and digits.</summary>
    internal static string Plain(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.Replace("(TM)", "").Replace("(R)", "").Replace("\u2122", "").Replace("\u00ae", ""))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static bool NamesMatch(string vulkanName, string adapterName)
    {
        string a = Plain(vulkanName), b = Plain(adapterName);
        return a.Length > 0 && b.Length > 0 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }

    // A successful listing is kept for the life of the process; an empty one (timeout, antivirus scan) only for two minutes.
    private static readonly Dictionary<string, (List<VulkanDevice> Devices, DateTime ExpiresUtc)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    /// <summary>Resolves the device for the installed Vulkan runtime; never throws and never blocks longer than a few seconds.</summary>
    internal static async Task<string> ResolveAsync(GpuInventoryStatus? inventory, CancellationToken ct)
    {
        try
        {
            string exe = Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable);
            if (!File.Exists(exe)) return DefaultDevice;
            List<VulkanDevice>? devices = null;
            lock (CacheLock)
                if (Cache.TryGetValue(exe, out var cached) && cached.ExpiresUtc > DateTime.UtcNow) devices = cached.Devices;
            if (devices == null)
            {
                try { devices = ParseDevices(await ListDevicesAsync(exe, ct).ConfigureAwait(false)); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { devices = []; }
                catch (Exception) when (!ct.IsCancellationRequested) { devices = []; }
                lock (CacheLock) Cache[exe] = (devices, devices.Count > 0 ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(2));
            }
            return Choose(devices, inventory);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return DefaultDevice; }
    }

    private static async Task<string> ListDevicesAsync(string exe, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, "--list-devices")
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }
}
