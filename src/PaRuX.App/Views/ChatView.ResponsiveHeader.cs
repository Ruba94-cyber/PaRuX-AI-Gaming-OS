using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        private const double ChatHeaderTitleMinimumWidth = 148;
        private bool _profileSelectorIsStacked = true;

        private void ChatHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Use the viewport cap, not the child DesiredSize: while this selector is scrollable
            // its content can report a clipped/zero desired width during an initial measure pass.
            double inlineWidth = ChatHeaderTitleMinimumWidth
                + ProfileSelectorScrollViewer.MaxWidth
                + ChatHeaderGrid.ColumnSpacing;
            bool stackSelector = e.NewSize.Width < inlineWidth;
            if (_profileSelectorIsStacked == stackSelector) return;

            _profileSelectorIsStacked = stackSelector;
            Grid.SetRow(ChatHeaderTitlePanel, 0);
            Grid.SetColumn(ChatHeaderTitlePanel, 0);
            Grid.SetColumnSpan(ChatHeaderTitlePanel, stackSelector ? 2 : 1);

            Grid.SetRow(ProfileSelectorScrollViewer, stackSelector ? 1 : 0);
            Grid.SetColumn(ProfileSelectorScrollViewer, stackSelector ? 0 : 1);
            Grid.SetColumnSpan(ProfileSelectorScrollViewer, stackSelector ? 2 : 1);
            ProfileSelectorScrollViewer.HorizontalAlignment = HorizontalAlignment.Left;
        }

        private void OnChatViewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
        }

        private const double ComposerToolsBelowWidth = 760;
        private bool _composerToolsBelow;

        /// <summary>
        /// In a narrow window the tool buttons (attach, voice, prompts, generation...) used to take most of the row and squeeze
        /// the message box into a four-line sliver. They now drop to their own row under the box, like mobile chat apps.
        /// </summary>
        private void ApplyComposerToolsPlacement(double width)
        {
            if (ComposerGrid == null || ComposerTools == null || InputBox == null) return;
            bool below = width < ComposerToolsBelowWidth;
            if (below == _composerToolsBelow) return;
            _composerToolsBelow = below;
            if (below)
            {
                Grid.SetRow(InputBox, 0); Grid.SetColumn(InputBox, 0); Grid.SetColumnSpan(InputBox, 2);
                Grid.SetRow(ComposerTools, 1); Grid.SetColumn(ComposerTools, 0); Grid.SetColumnSpan(ComposerTools, 3);
            }
            else
            {
                Grid.SetRow(InputBox, 0); Grid.SetColumn(InputBox, 1); Grid.SetColumnSpan(InputBox, 1);
                Grid.SetRow(ComposerTools, 0); Grid.SetColumn(ComposerTools, 0); Grid.SetColumnSpan(ComposerTools, 1);
            }
        }

        internal void ApplyResponsiveLayout(double width, double height)
        {
            if (width <= 0 || height <= 0) return;
            var profile = WindowResponsivenessPolicy.Evaluate(width, height);

            if (InputBox != null)
            {
                InputBox.MaxHeight = profile.ComposerMaxInputHeight;
            }

            ApplyComposerToolsPlacement(width);

            if (CodeFolderButton != null)
            {
                CodeFolderButton.MaxWidth = profile.CodeFolderButtonMaxWidth;
            }

            if (ModelSelector != null)
            {
                ModelSelector.MinWidth = profile.ModelSelectorMinWidth;
            }

            if (SkillSelector != null)
            {
                SkillSelector.MinWidth = profile.SkillSelectorMinWidth;
            }
        }
    }
}
