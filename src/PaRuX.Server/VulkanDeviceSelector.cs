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
        return DefaultDevice;
    }

    private static bool NamesMatch(string vulkanName, string adapterName) =>
        vulkanName.Contains(adapterName, StringComparison.OrdinalIgnoreCase) ||
        adapterName.Contains(vulkanName, StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, List<VulkanDevice>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    /// <summary>Resolves the device for the installed Vulkan runtime; never throws and never blocks longer than a few seconds.</summary>
    internal static async Task<string> ResolveAsync(GpuInventoryStatus? inventory, CancellationToken ct)
    {
        try
        {
            string exe = Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable);
            if (!File.Exists(exe)) return DefaultDevice;
            List<VulkanDevice>? devices;
            lock (CacheLock) Cache.TryGetValue(exe, out devices);
            if (devices == null)
            {
                devices = ParseDevices(await ListDevicesAsync(exe, ct).ConfigureAwait(false));
                if (devices.Count > 0) lock (CacheLock) Cache[exe] = devices;
            }
            return Choose(devices, inventory);
        }
        catch (OperationCanceledException) { throw; }
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
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
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
