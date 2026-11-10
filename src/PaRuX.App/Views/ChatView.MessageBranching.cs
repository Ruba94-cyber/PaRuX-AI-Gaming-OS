using System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        public const string MessageBranchAutomationId = "Chat_Message_Branch_Button";

        public const string DuplicateChatAutomationId = "Chat_Message_Duplicate_Item";

        /// <summary>Right-click menu on any message bubble (user or assistant): "Branch from here".</summary>
        private MenuFlyout BuildMessageBranchFlyout(ChatMessage message)
        {
            var item = new MenuFlyoutItem { Text = ChatItalian ? "Crea un ramo da qui" : "Branch from here" };
            AutomationProperties.SetAutomationId(item, MessageBranchAutomationId);
            AutomationProperties.SetName(item, ChatItalian ? "Crea un ramo da qui" : "Branch from here");
            item.Click += (_, _) => BranchFromMessage(message);
            var flyout = new MenuFlyout();
            flyout.Items.Add(item);

            var duplicate = new MenuFlyoutItem { Text = ChatItalian ? "Duplica chat" : "Duplicate chat" };
            AutomationProperties.SetAutomationId(duplicate, DuplicateChatAutomationId);
            AutomationProperties.SetName(duplicate, ChatItalian ? "Duplica chat" : "Duplicate chat");
            duplicate.Click += (_, _) => ShowTransientStatus(ChatSession.Current.DuplicateConversation()
                ? (ChatItalian ? "Aperta una copia di questa chat." : "Opened a copy of this chat.")
                : (ChatItalian ? "Ancora niente da duplicare." : "Nothing to duplicate yet."));
            flyout.Items.Add(duplicate);
            return flyout;
        }

        /// <summary>Visible per-message button next to Copy/Regenerate on assistant replies.</summary>
        private Button BuildBranchActionButton(ChatMessage message)
        {
            Button branch = BuildActionButton("", ChatItalian ? "Crea un ramo da qui" : "Branch from here");
            AutomationProperties.SetAutomationId(branch, MessageBranchAutomationId);
            branch.Click += (_, _) => BranchFromMessage(message);
            return branch;
        }

        private void BranchFromMessage(ChatMessage message)
        {
            if (message.IsStreaming) { ShowTransientStatus(ChatItalian ? "Attendi che la risposta sia completata prima di creare un ramo." : "Wait for the reply to finish before branching."); return; }
            try
            {
                ShowTransientStatus(ChatSession.Current.BranchFromMessage(message)
                    ? (ChatItalian ? "Aperto un nuovo ramo di questa chat." : "Opened a new branch of this chat.")
                    : (ChatItalian ? "Impossibile creare un ramo da questo messaggio." : "Couldn't branch from this message."));
            }
            catch (Exception ex)
            {
                Log.Write("Branch failed: " + ex.Message);
                ShowTransientStatus(ChatItalian ? "Impossibile creare un ramo da questo messaggio." : "Couldn't branch from this message.");
            }
        }
    }
}
