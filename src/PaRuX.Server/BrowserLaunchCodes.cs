using System.Security.Cryptography;

namespace PaRuX.Server;

// Short-lived, single-use capabilities for transferring the desktop token to a local browser.
internal sealed class BrowserLaunchCodes
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _codes = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _utcNow;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private const int Limit = 32;

    public BrowserLaunchCodes() : this(() => DateTime.UtcNow) { }
    internal BrowserLaunchCodes(Func<DateTime> utcNow) => _utcNow = utcNow;

    internal string Issue()
    {
        lock (_gate)
        {
            DateTime now = _utcNow();
            foreach (string expired in _codes.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
                _codes.Remove(expired);
            if (_codes.Count >= Limit) throw new InvalidOperationException("Too many pending browser launches.");
            string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _codes.Add(code, now + Lifetime);
            return code;
        }
    }

    internal bool Consume(string code)
    {
        if (code.Length != 64) return false;
        lock (_gate)
            return _codes.Remove(code, out DateTime expires) && expires > _utcNow();
    }
}
