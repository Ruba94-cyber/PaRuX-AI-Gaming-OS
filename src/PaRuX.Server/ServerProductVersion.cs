using System.Reflection;

namespace PaRuX.Server;

/// <summary>The product version this Server was built as (informational version without build metadata).</summary>
internal static class ServerProductVersion
{
    internal static string Current { get; } = Resolve(typeof(ServerProductVersion).Assembly);

    internal static string Resolve(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+');
            return (plus >= 0 ? informational[..plus] : informational).Trim();
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
