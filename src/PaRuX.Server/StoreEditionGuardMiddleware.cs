using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// The one Core entry-point guard of the Store edition: a request that would download or install
/// executable code, or use the torrent engine, is refused with 403 and a readable reason before the
/// endpoint (and therefore any network or disk activity) runs. Outside the Store edition it is a
/// pass-through, so MSI, Portable and Corporate behaviour is unchanged.
/// </summary>
internal sealed class StoreEditionGuardMiddleware(RequestDelegate next, Func<bool>? isStoreEdition = null)
{
    private readonly Func<bool> _isStoreEdition = isStoreEdition ?? (() => StoreEditionPolicy.IsActive);

    public async Task InvokeAsync(HttpContext context)
    {
        string? reason = StoreEditionPolicy.BlockedReason(
            _isStoreEdition(), context.Request.Method, context.Request.Path.Value);
        if (reason is null)
        {
            await next(context);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = reason, code = "store_edition_restricted" });
    }
}
