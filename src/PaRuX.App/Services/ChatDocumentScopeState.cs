using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// The chat composer's "answer from my documents" choice: on/off and which indexed folders may answer. Kept for the session
    /// only (a document answer is always an explicit choice of the person for that conversation), sent with every chat turn.
    /// </summary>
    public static class ChatDocumentScopeState
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Selected = new HashSet<string>();
        public static bool Enabled { get; private set; }

        public static int SelectedCount { get { lock (Gate) return Selected.Count; } }

        public static void SetEnabled(bool enabled) { lock (Gate) Enabled = enabled; }

        public static void SetSource(string id, bool on)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl)) return;
            lock (Gate)
            {
                if (on) Selected.Add(id);
                else
                {
                    bool hadExplicitSelection = Selected.Count > 0;
                    Selected.Remove(id);
                    // Empty is a wire-level alias for ALL enabled folders.
                    // Never let unchecking the final explicitly scoped folder
                    // silently broaden the user's knowledge exposure.
                    if (hadExplicitSelection && Selected.Count == 0) Enabled = false;
                }
            }
        }

        public static bool IsSelected(string id) { lock (Gate) return Selected.Contains(id); }

        /// <summary>
        /// Drops removed/disabled folders. Returns true if an explicit source
        /// selection became empty and document mode was disabled fail-closed.
        /// An intentional empty/all scope remains unchanged.
        /// </summary>
        public static bool Prune(IEnumerable<string> existingIds)
        {
            var existing = new HashSet<string>(existingIds, StringComparer.Ordinal);
            lock (Gate)
            {
                bool hadExplicitSelection = Selected.Count > 0;
                Selected.RemoveWhere(id => !existing.Contains(id));
                bool revoked = hadExplicitSelection && Selected.Count == 0;
                if (revoked) Enabled = false;
                return revoked;
            }
        }

        /// <summary>
        /// Sets EXACTLY a server-verified nonempty guide scope for an explicitly
        /// requested Chat draft. Never use the Chat default empty=all semantics.
        /// Preserves an existing selection if validation fails.
        /// </summary>
        public static bool TryUseExplicitSources(IReadOnlyCollection<string>? ids)
        {
            if (ids == null || ids.Count is < 1 or > 64 ||
                ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 200 ||
                    id.Any(char.IsControl)))
                return false;
            lock (Gate)
            {
                Selected.Clear();
                foreach (string id in ids) Selected.Add(id);
                Enabled = true;
            }
            return true;
        }

        public static ChatDocumentScope? ToRequest()
        {
            lock (Gate)
                return Enabled ? new ChatDocumentScope { Enabled = true, SourceIds = Selected.ToList() } : null;
        }
    }
}
