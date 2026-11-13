using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// I/O side of coding context budgeting. Coding Agent always executes against PaRuX's managed
    /// runtime; competitor products are benchmark targets, never runtime dependencies. SEC-F01: the managed
    /// runtime is keyed per launch and only Core holds the key, so the physical window is read from Core's
    /// authenticated GET /api/v1/runtime/context-window (desktop owner), never from the runtime port.
    /// If a future PaRuX-native engine exposes context another way, it should be added as a PaRuX engine
    /// capability in Core, not by teaching this path engine-specific APIs.
    /// </summary>
    public static class CodingContextBudgetResolver
    {
        private static readonly CodingContextBudgetCache Cache = new(
            async (refresh, ct) => (await ModelRolesClient.Current.GetRuntimeContextWindowAsync(refresh, ct).ConfigureAwait(false)).ContextTokens,
            TimeSpan.FromSeconds(30));

        public sealed record Budget(int ContextTokens, int MaxCharacters, bool Observed);

        public static async Task<Budget> ResolveAsync(CancellationToken ct)
        {
            int tokens = await Cache.GetTokensAsync(ct).ConfigureAwait(false);
            return Convert(CodingContextBudgetPolicy.ForContextTokens(tokens));
        }

        private static Budget Convert(CodingContextBudgetPolicy.Budget value) =>
            new(value.ContextTokens, value.MaxCharacters, value.Observed);

        internal static void Invalidate() => Cache.Invalidate();
    }
}
