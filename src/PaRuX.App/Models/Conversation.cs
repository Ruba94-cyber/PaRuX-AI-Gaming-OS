using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Models
{
    public class Conversation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>What an untitled conversation shows. Named so importers and the history store
        /// fall back to the same thing rather than each repeating the literal.</summary>
        public const string DefaultTitle = "New chat";

        public string Title { get; set; } = DefaultTitle;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public bool IsFavorite { get; set; }

        /// <summary>Permission preset for coding tasks started from this conversation. A NEW conversation
        /// defaults to ask; a stored document that lacks the field was created under the old contract and
        /// resolves to bypass (see the JSON constructor).</summary>
        public string CodingRunPermissionMode { get; set; } = CodingRunPermissionModes.Default;

        public Conversation() { }

        /// <summary>Used by System.Text.Json when loading a persisted conversation: a missing value keeps its
        /// legacy meaning (bypass); an explicit value is preserved.</summary>
        [JsonConstructor]
        private Conversation(string? codingRunPermissionMode)
        {
            CodingRunPermissionMode = CodingRunPermissionModes.NormalizeStored(codingRunPermissionMode);
        }

        /// <summary>Never written to parux.db, never pushed to the server, never shown in the
        /// sidebar's Recent list - see ChatSession.Persist. Not itself persisted (an incognito
        /// conversation is never saved at all, so there is nothing to round-trip this flag
        /// through), but kept on the model rather than tracked separately in ChatSession so every
        /// existing "is this the current conversation" check stays correct without change.</summary>
        public bool IsIncognito { get; set; }

        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

        /// <summary>
        /// Superseded message paths retained when regeneration/edit-and-continue diverges from a
        /// previously durable path. Missing JSON from older PaRuX versions naturally initializes to
        /// an empty list, so this remains backward compatible with the existing document_json store.
        /// </summary>
        /// <summary>Set when this conversation was branched/duplicated from another one. Additive: absent in older documents.</summary>
        public string? ParentConversationId { get; set; }

        /// <summary>The <see cref="ChatMessage.MessageId"/> this conversation was branched at (null for a full duplicate).</summary>
        public string? ParentMessageId { get; set; }

        public List<ConversationBranch> Branches { get; set; } = new List<ConversationBranch>();
    }
}
