using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX
{
    public sealed partial class MainWindow
    {
        private bool _isProgrammaticPaneChange;
        private bool _userExplicitlyToggledPane;
        private bool _paneEventsSubscribed;

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            EnsurePaneEventsSubscribed();
            ApplyWindowResponsiveness(e.NewSize.Width, e.NewSize.Height);
        }

        private void EnsurePaneEventsSubscribed()
        {
            if (_paneEventsSubscribed || NavView == null) return;
            _paneEventsSubscribed = true;
            NavView.PaneOpening += (s, e) =>
            {
                UpdateContentStudioModeControlsVisibility(isPaneOpen: true);
                if (!_isProgrammaticPaneChange) _userExplicitlyToggledPane = true;
            };
            NavView.PaneClosing += (s, e) =>
            {
                UpdateContentStudioModeControlsVisibility(isPaneOpen: false);
                if (!_isProgrammaticPaneChange) _userExplicitlyToggledPane = true;
            };
            UpdateContentStudioModeControlsVisibility(NavView.IsPaneOpen);
        }

        /// <summary>
        /// Shows or hides the whole navigation pane. Hiding also closes it: XAML starts the pane
        /// open, and a pane that is open while <see cref="NavigationView.IsPaneVisible"/> is false
        /// is left painted as a dark 280 px slab over the page (first launch: it covered the left
        /// of the Welcome/setup screen and never went away). Showing it again re-applies the
        /// width-based policy so an expanded window gets its pane back, a narrow one a rail.
        /// </summary>
        internal void SetNavPaneVisible(bool visible)
        {
            if (NavView == null) return;
            _isProgrammaticPaneChange = true;
            try
            {
                if (!visible && NavView.IsPaneOpen) NavView.IsPaneOpen = false;
                if (NavView.IsPaneVisible != visible) NavView.IsPaneVisible = visible;
            }
            finally
            {
                _isProgrammaticPaneChange = false;
            }

            UpdateContentStudioModeControlsVisibility(NavView.IsPaneOpen);
            if (visible && RootGrid != null) ApplyWindowResponsiveness(RootGrid.ActualWidth, RootGrid.ActualHeight);
        }

        private void UpdateContentStudioModeControlsVisibility(bool isPaneOpen)
        {
            // The top-of-content Advanced switch was a second doorway into
            // unrelated AI controls when Corporate or Gaming-only hid the pane
            // switch. Both copies must obey the same setup-profile policy.
            bool allowAdvanced = !PaRuX.Contracts.CorporateMode.IsActive
                && SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId) != SetupProfile.GamingOnly
                && !IsGamingModeTab;
            Visibility visibility = !isPaneOpen && allowAdvanced
                ? Visibility.Visible : Visibility.Collapsed;
            if (ContentStudioModeToggle != null) ContentStudioModeToggle.Visibility = visibility;
        }

        internal void ApplyWindowResponsiveness(double width, double height)
        {
            if (width <= 0 || height <= 0 || NavView == null) return;

            // While the pane is hidden (first-run welcome screen) it must stay closed. Re-opening it
            // here made WinUI paint the 288 px pane as an overlay slab over the welcome page on the
            // very first SizeChanged; SetNavPaneVisible(true) re-runs this policy when it returns.
            if (!NavView.IsPaneVisible)
            {
                if (NavView.IsPaneOpen)
                {
                    _isProgrammaticPaneChange = true;
                    try { NavView.IsPaneOpen = false; }
                    finally { _isProgrammaticPaneChange = false; }
                }
                UpdateContentStudioModeControlsVisibility(isPaneOpen: false);
                return;
            }

            var profile = WindowResponsivenessPolicy.Evaluate(width, height, NavView.IsPaneOpen);
            NavigationViewPaneDisplayMode displayMode = profile.RecommendedPaneDisplayMode switch
            {
                NavigationViewPaneDisplayModeRecommendation.LeftCompact => NavigationViewPaneDisplayMode.LeftCompact,
                _ => NavigationViewPaneDisplayMode.Left
            };

            if (NavView.PaneDisplayMode != displayMode)
            {
                _isProgrammaticPaneChange = true;
                try
                {
                    NavView.PaneDisplayMode = displayMode;
                }
                finally
                {
                    _isProgrammaticPaneChange = false;
                }
            }

            NavView.OpenPaneLength = profile.RecommendedOpenPaneLength;

            if (profile.ShouldAutoClosePane)
            {
                if (NavView.IsPaneOpen && !_userExplicitlyToggledPane)
                {
                    _isProgrammaticPaneChange = true;
                    try
                    {
                        NavView.IsPaneOpen = false;
                    }
                    finally
                    {
                        _isProgrammaticPaneChange = false;
                    }
                }
            }
            else if (profile.WidthBreakpoint >= WindowWidthBreakpoint.Expanded)
            {
                if (!NavView.IsPaneOpen && !_userExplicitlyToggledPane)
                {
                    _isProgrammaticPaneChange = true;
                    try
                    {
                        NavView.IsPaneOpen = true;
                    }
                    finally
                    {
                        _isProgrammaticPaneChange = false;
                    }
                }
            }

            UpdateContentStudioModeControlsVisibility(NavView.IsPaneOpen);
        }
    }
}
