using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// Readable plain-text form of conversations (Markdown), shared by the single-chat export and "Export all chats". Pure text and
    /// file-name logic, no UI: a chat never leaves the PC and an exported file name never escapes the chosen folder.
    /// </summary>
    public static class ConversationMarkdown
    {
        public static string ToMarkdown(Conversation conversation)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# " + (string.IsNullOrWhiteSpace(conversation.Title) ? "PaRuX chat" : conversation.Title.Replace('\n', ' ').Trim())).AppendLine();
            foreach (ChatMessage message in conversation.Messages)
            {
                builder.AppendLine("## " + (message.Role == "user" ? "You" : "PaRuX"));
                builder.AppendLine().AppendLine(message.Content ?? string.Empty).AppendLine();
                foreach (ChatAttachment attachment in message.Attachments ?? new List<ChatAttachment>())
                    builder.AppendLine("- Attachment: `" + (attachment.FileName ?? string.Empty).Replace("`", "") + "`");
                if (message.Attachments != null && message.Attachments.Count > 0) builder.AppendLine();
            }
            return builder.ToString();
        }

        public static string SafeFileName(string? value)
        {
            string name = string.IsNullOrWhiteSpace(value) ? "PaRuX chat" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            name = name.TrimEnd('.', ' ');
            if (name.Length == 0) name = "PaRuX chat";
            // Windows reserves a few device names even with an extension.
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3])))
                name = "_" + name;
            return name.Length > 80 ? name.Substring(0, 80).TrimEnd('.', ' ') : name;
        }

        /// <summary>One file name per conversation: "&lt;date&gt; &lt;title&gt;.md", with " (2)", " (3)"... for clashes. Case-insensitive like NTFS.</summary>
        public static IReadOnlyList<(Conversation Conversation, string FileName)> PlanFileNames(IEnumerable<Conversation> conversations)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plan = new List<(Conversation, string)>();
            foreach (Conversation c in conversations.OrderBy(x => x.CreatedUtc))
            {
                string baseName = c.CreatedUtc.ToString("yyyy-MM-dd") + " " + SafeFileName(c.Title);
                string candidate = baseName + ".md";
                for (int n = 2; !used.Add(candidate); n++) candidate = baseName + " (" + n + ").md";
                plan.Add((c, candidate));
            }
            return plan;
        }
    }
}
