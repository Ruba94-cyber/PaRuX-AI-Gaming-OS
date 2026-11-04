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

        public static void SetEnabled(bool enabled) { lock (Gate) Enabled = enabled; }

        public static void SetSource(string id, bool on)
        {
            lock (Gate) { if (on) Selected.Add(id); else Selected.Remove(id); }
        }

        public static bool IsSelected(string id) { lock (Gate) return Selected.Contains(id); }

        /// <summary>Drops folders that no longer exist; an empty selection means every enabled folder.</summary>
        public static void Prune(IEnumerable<string> existingIds)
        {
            lock (Gate) Selected.RemoveWhere(id => !existingIds.Contains(id));
        }

        public static ChatDocumentScope? ToRequest()
        {
            lock (Gate)
                return Enabled ? new ChatDocumentScope { Enabled = true, SourceIds = Selected.ToList() } : null;
        }
    }
}
