using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// SEC-F01. Short-lived cache of the managed runtime's physical context window. The window is fetched through a
    /// caller-supplied source (production: PaRuX Core's authenticated /api/v1/runtime/context-window), never from the
    /// runtime port: the runtime is keyed per launch and only Core holds the key. A failed source is not cached and
    /// yields 0 ("unknown", conservative default budget). <see cref="Invalidate"/> also makes the next read ask Core to
    /// drop its own cached value, so a model swap or reload is seen immediately.
    /// </summary>
    internal sealed class CodingContextBudgetCache
    {
        private readonly Func<bool, CancellationToken, Task<int>> _fetch;
        private readonly TimeSpan _ttl;
        private readonly Func<DateTime> _utcNow;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _sync = new();
        private (int Tokens, DateTime CheckedUtc)? _cache;
        private bool _refreshNext;

        internal CodingContextBudgetCache(Func<bool, CancellationToken, Task<int>> fetch, TimeSpan ttl, Func<DateTime>? utcNow = null)
        {
            _fetch = fetch;
            _ttl = ttl;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal void Invalidate()
        {
            lock (_sync) { _cache = null; _refreshNext = true; }
        }

        internal async Task<int> GetTokensAsync(CancellationToken ct)
        {
            if (TryGetCached(out int cached)) return cached;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (TryGetCached(out cached)) return cached;
                bool refresh;
                lock (_sync) { refresh = _refreshNext; _refreshNext = false; }
                int tokens;
                try { tokens = await _fetch(refresh, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch
                {
                    lock (_sync) { if (refresh) _refreshNext = true; }
                    return 0;
                }
                if (tokens < 0) tokens = 0;
                lock (_sync) { _cache = (tokens, _utcNow()); }
                return tokens;
            }
            finally { _gate.Release(); }
        }

        private bool TryGetCached(out int tokens)
        {
            lock (_sync)
            {
                if (_cache is { } entry && _utcNow() - entry.CheckedUtc < _ttl)
                {
                    tokens = entry.Tokens;
                    return true;
                }
            }
            tokens = 0;
            return false;
        }
    }
}
