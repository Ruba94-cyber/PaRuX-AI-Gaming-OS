using System.Net;
using System.Net.Http.Headers;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Downloads;

/// <summary>HTTP(S) transfer with resume, redirects that obey the same URL rules, and size/space limits.</summary>
internal sealed class HttpGameDownloadTransport : IGameDownloadTransport
{
    private const int MaxRedirects = 5;
    private readonly HttpClient _http;

    public HttpGameDownloadTransport(HttpClient http) => _http = http;

    public Task AbandonAsync(GameDownloadJob job, CancellationToken ct) => Task.CompletedTask;

    public async Task FetchAsync(GameDownloadTransferContext context, CancellationToken ct)
    {
        GameDownloadJob e = context.Job;
        Uri url = e.Url ?? throw new GameDownloadException("Enter a valid web address.");
        // Never follow a user-placed symbolic link as a partial download target.
        // The destination directory was validated at enqueue, but the part file
        // may have been replaced since then.
        GameDownloadManager.EnsureSafeDownloadPaths(e);
        long existing = File.Exists(e.PartPath) ? new FileInfo(e.PartPath).Length : 0;
        // A filename is not a source identity. Never append an old partial from a
        // different URL or checksum, even when its server happens to accept Range.
        if (existing > 0 && !GameDownloadManager.ResumeIdentityMatches(e))
        {
            GameDownloadManager.CleanPartial(e);
            existing = 0;
        }
        if (existing == 0) GameDownloadManager.WriteResumeIdentity(e);
        GameDownloadManager.EnsureSafeDownloadPaths(e);
        EntityTagHeaderValue? validator = ResumeValidator.Read(e.PartPath);

        using HttpResponseMessage response = await SendFollowingRedirectsAsync(url, existing, validator, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            // Our partial file no longer matches the server: start again from zero.
            GameDownloadManager.CleanPartial(e);
            throw new HttpRequestException("range not satisfiable");
        }
        if (!response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode;
            // Timeouts, rate limits and server errors are temporary: retry. Anything else (404, 403...) will not change.
            if (code is 408 or 429 or >= 500)
                throw new HttpRequestException($"The server answered HTTP {code}.", null, response.StatusCode);
            throw new GameDownloadException($"The server answered HTTP {code}.");
        }

        bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
        // HTTP 206 is valid only in response to OUR own nonzero Range request.
        // A server supplying unsolicited partial bytes must never cause us to
        // append or publish a truncated file as if it were a complete download.
        if (resumed && (existing == 0 ||
            !ResumeValidator.ContentRangeMatches(response.Content.Headers.ContentRange, existing)))
        {
            GameDownloadManager.CleanPartial(e);
            throw new GameDownloadException("The server supplied an unexpected or misaligned partial response.");
        }
        if (!resumed && existing > 0) { GameDownloadManager.CleanPartial(e); existing = 0; } // server ignored the range or the file changed
        if (!resumed) ResumeValidator.Write(e.PartPath, response.Headers.ETag);

        long? total = resumed
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;
        if (total is > 0 && total > context.MaxFileBytes) throw new GameDownloadException("The file is larger than the allowed maximum.");
        long needed = (total ?? 0) - existing;
        if (needed > 0 && context.FreeBytes(e.Directory) < needed + GameDownloadManager.FreeSpaceMarginBytes) throw new GameDownloadException("Not enough free disk space.");
        context.Report(existing, total);

        GameDownloadManager.EnsureSafeDownloadPaths(e);
        await using (var target = new FileStream(e.PartPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true))
        await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            var buffer = new byte[1 << 16];
            long written = existing;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (context.GamingActive()) throw new OperationCanceledException(); // no transfer competes with a game
                written += read;
                if (written > context.MaxFileBytes) throw new GameDownloadException("The file is larger than the allowed maximum.");
                await context.Throttle(read, ct).ConfigureAwait(false);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                context.Report(written, null);
            }
            await target.FlushAsync(ct).ConfigureAwait(false);
            if (total is not null && written != total)
                throw new HttpRequestException("connection closed before the end of the file");
        }
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(Uri url, long rangeFrom, EntityTagHeaderValue? validator, CancellationToken ct)
    {
        Uri current = url;
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (rangeFrom > 0)
            {
                request.Headers.Range = new RangeHeaderValue(rangeFrom, null);
                if (validator is not null) request.Headers.IfRange = new RangeConditionHeaderValue(validator);
            }
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                GameDownloadManager.ValidateUrl(next.ToString()); // every hop obeys the same rules: no downgrade, no credentials
                current = next;
                continue;
            }
            return response;
        }
        throw new GameDownloadException("Too many redirects.");
    }
}
