using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

/// <summary>A present HID gaming device as listed by the HidHide command-line tool.</summary>
public sealed record HidGamingDevice(string InstancePath, ushort VendorId, ushort ProductId);

public sealed record PadBindingResult(bool Proven, string? InstancePath, string Reason);

/// <summary>
/// Decides whether a Windows RawGameController (known only by VID/PID: its NonRoamableId is opaque) can be tied to
/// exactly one HID device instance path. It FAILS CLOSED: any ambiguity (two pads with the same VID/PID, several HID
/// collections, nothing present, unparseable output) means "not proven", and then nothing may be hidden.
/// Pure and deterministic, so it is fully testable without a driver.
/// </summary>
public static class PadBindingVerifier
{
    public static PadBindingResult Verify(ushort vendorId, ushort productId, int sameIdentityControllers,
        IReadOnlyList<HidGamingDevice> hidDevices)
    {
        if (vendorId == 0 && productId == 0)
            return new(false, null, "The controller does not report a vendor/product identity, so it cannot be tied to a HID device.");
        if (sameIdentityControllers != 1)
            return new(false, null, sameIdentityControllers <= 0
                ? "The controller is no longer listed by Windows."
                : "More than one connected controller has the same vendor/product identity; PaRuX cannot tell which one to hide.");
        if (hidDevices is null || hidDevices.Count == 0)
            return new(false, null, "HidHide listed no gaming devices.");
        var matches = hidDevices
            .Where(d => d.VendorId == vendorId && d.ProductId == productId)
            .GroupBy(d => d.InstancePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).ToList();
        if (matches.Count == 0)
            return new(false, null, "No HID device with this controller's vendor/product identity is present.");
        if (matches.Count > 1)
            return new(false, null, "Several HID devices match this controller; the binding is ambiguous, so nothing is hidden.");
        return new(true, matches[0].InstancePath, "The controller is bound to exactly one HID device.");
    }
}

/// <summary>Parses the JSON printed by <c>HidHideCLI --dev-gaming</c>; anything unexpected yields null (fail closed).</summary>
public static class HidGamingDeviceListParser
{
    private static readonly Regex VidPid = new(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<HidGamingDevice>? Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output) || output.Length > 512 * 1024) return null;
        try
        {
            using var doc = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 16 });
            var found = new List<HidGamingDevice>();
            Walk(doc.RootElement, found, 0);
            return found;
        }
        catch (Exception ex) when (ex is JsonException or RegexMatchTimeoutException) { return null; }
    }

    private static void Walk(JsonElement element, List<HidGamingDevice> found, int depth)
    {
        if (depth > 16 || found.Count > 256) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Walk(item, found, depth + 1);
                break;
            case JsonValueKind.Object:
                bool present = true;
                if (element.TryGetProperty("present", out var p) && p.ValueKind == JsonValueKind.False) present = false;
                if (element.TryGetProperty("deviceInstancePath", out var path) && path.ValueKind == JsonValueKind.String
                    && path.GetString() is { Length: > 4 and <= 300 } instance && present)
                {
                    var m = VidPid.Match(instance);
                    if (m.Success)
                        found.Add(new HidGamingDevice(instance,
                            Convert.ToUInt16(m.Groups[1].Value, 16), Convert.ToUInt16(m.Groups[2].Value, 16)));
                }
                foreach (var property in element.EnumerateObject())
                    if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                        Walk(property.Value, found, depth + 1);
                break;
        }
    }
}
