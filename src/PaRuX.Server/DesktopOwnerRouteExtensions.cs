using System.Net;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Desktop-owner gate for a mutating route that changes this machine: its runtimes and installed software, its
/// configuration, its keys and integrations, or the files it reads and writes. Only the desktop itself passes.
/// A browser session is refused even when a same-machine reverse proxy delivers it, because that request also
/// arrives from 127.0.0.1; paired devices and API keys are refused too. Routes that the browser client or the
/// paired-device allowlist already serves are deliberately not marked, so they behave exactly as before.
/// </summary>
internal static class DesktopOwnerRouteExtensions
{
    /// <summary>
    /// Refuses every principal except the desktop owner with 403 before the handler runs. The handler is untouched,
    /// so the desktop reaches it exactly as before.
    /// </summary>
    internal static RouteHandlerBuilder RequireDesktopOwner(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (invocation, next) =>
        {
            if (IsDesktopOwner(invocation.HttpContext)) return await next(invocation);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        });

    /// <summary>
    /// Keeps ordinary downloads available to paired devices while requiring the desktop owner before
    /// a request can discard a partial or replace a completed managed model.
    /// </summary>
    internal static RouteHandlerBuilder RequireDesktopOwnerForDestructiveModelDownload(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (invocation, next) =>
        {
            ModelDownloadRequest? request = invocation.Arguments.OfType<ModelDownloadRequest>().FirstOrDefault();
            if (request is not null
                && (request.DiscardPartial || request.ReplaceExisting)
                && !IsDesktopOwner(invocation.HttpContext))
            {
                return Results.Problem(
                    detail: "Discarding a partial download or replacing a managed model requires the desktop owner.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Desktop owner required",
                    type: "about:blank",
                    extensions: new Dictionary<string, object?> { ["code"] = "desktop_owner_required" });
            }

            return await next(invocation);
        });

    /// <summary>
    /// SEC-F04: turning the Internet switch ON is the strict-local boundary and is desktop-owner only. A paired
    /// device keeps the safe direction (turning it OFF).
    /// </summary>
    internal static RouteHandlerBuilder RequireDesktopOwnerToEnableInternet(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (invocation, next) =>
        {
            InternetEnabledRequest? request = invocation.Arguments.OfType<InternetEnabledRequest>().FirstOrDefault();
            if (request is not null && request.Enabled && !IsDesktopOwner(invocation.HttpContext))
            {
                return Results.Problem(
                    detail: "Turning the Internet switch on requires the desktop owner.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Desktop owner required",
                    type: "about:blank",
                    extensions: new Dictionary<string, object?> { ["code"] = "desktop_owner_required" });
            }

            return await next(invocation);
        });

    /// <summary>
    /// SEC-F04: an Autopilot run started by a paired device is always created in the supervised mode, which holds it in
    /// "pending approval" until the desktop owner approves it on the desktop. A requested balanced or autonomous mode is
    /// dropped; the desktop and CLI keep choosing their own mode.
    /// </summary>
    internal static RouteHandlerBuilder ForceSupervisedAutopilotForPairedDevice(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (invocation, next) =>
        {
            StartAutopilotRunRequest? request = invocation.Arguments.OfType<StartAutopilotRunRequest>().FirstOrDefault();
            if (request is not null && !IsDesktopOwner(invocation.HttpContext))
                request.PermissionMode = AutopilotPermissionMode.Supervised;

            return await next(invocation);
        });

    /// <summary>Capabilities a paired device may grant to a task it creates. Everything else (mcp.external,
    /// coding.package-restore*, agent.delegate, unknown names) is desktop-owner only.</summary>
    internal static readonly IReadOnlySet<string> RemoteGrantableCapabilities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "context.read", "web.research", "repository.read" };

    /// <summary>
    /// SEC-F04: a paired device may create agent tasks but cannot choose their authority. Capabilities outside
    /// <see cref="RemoteGrantableCapabilities"/> are rejected, and a root task's coding permission mode is forced
    /// to ask (a delegated child always inherits its parent's mode, so any requested value is dropped).
    /// </summary>
    internal static RouteHandlerBuilder ConstrainPairedDeviceTaskAuthority(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (invocation, next) =>
        {
            CreateAgentTaskRequest? request = invocation.Arguments.OfType<CreateAgentTaskRequest>().FirstOrDefault();
            if (request is not null && !IsDesktopOwner(invocation.HttpContext))
            {
                if (request.GrantedCapabilities?.Any(c => !RemoteGrantableCapabilities.Contains((c ?? string.Empty).Trim())) == true)
                {
                    return Results.Problem(
                        detail: "A paired device cannot grant this capability to a task. Allowed: "
                            + string.Join(", ", RemoteGrantableCapabilities.Order(StringComparer.Ordinal)) + ".",
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Desktop owner required",
                        type: "about:blank",
                        extensions: new Dictionary<string, object?> { ["code"] = "desktop_owner_required" });
                }

                request.CodingRunPermissionMode = string.IsNullOrWhiteSpace(request.ParentId)
                    ? CodingRunPermissionModes.Ask
                    : null;
            }

            return await next(invocation);
        });

    /// <summary>
    /// The one predicate for desktop-only authority. Loopback alone is not evidence of the desktop: a browser session
    /// that reaches PaRuX through a same-machine reverse proxy arrives from 127.0.0.1 too, so the owner that the global
    /// gate stamped must also be the desktop itself.
    /// </summary>
    internal static bool IsDesktopOwner(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address)
        && context.Items["PaRuXOwner"] as string == "desktop";
}
