using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Visual gallery and browser for generated images and video outputs.
    /// Shows thumbnails, prompt, model, timestamp, and status. Provides actions to open,
    /// reveal in folder, reuse as edit source, and delete with explicit confirmation.
    /// Outputs are never shown only as a text list.
    /// </summary>
    public sealed class MediaGalleryView : UserControl
    {
        private readonly TextBox _searchBox = new()
        {
            PlaceholderText = "Search outputs by prompt, model, title, or file name…",
            MinWidth = 280
        };
        private readonly ComboBox _filterCombo = new();
        private readonly ComboBox _sortCombo = new();
        private readonly Button _refreshButton = new() { Content = "Refresh" };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        private readonly TextBlock _emptyState = new()
        {
            Text = "No media outputs found in Artifact Workspace or generated folders.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(8, 12, 8, 12)
        };
        private readonly StackPanel _cardsPanel = new() { Spacing = 12 };
        private readonly Button _previousPage = new() { Content = "Previous", MinHeight = 40 };
        private readonly Button _nextPage = new() { Content = "Next", MinHeight = 40 };
        private readonly TextBlock _pageStatus = new() { VerticalAlignment = VerticalAlignment.Center };
        private int _pageIndex;
        private readonly MediaGalleryDeleteCoordinator _deleteCoordinator = new();

        private readonly List<MediaGalleryItem> _allItems = new();
        private CancellationTokenSource? _refreshCts;

        public event EventHandler<MediaGalleryItem>? ItemReusedAsEditSource;
        public event EventHandler<MediaGalleryItem>? ItemDeleted;

        public MediaGalleryView()
        {
            Content = BuildUi();
            Loaded += async (_, _) => await RefreshAsync();
            Unloaded += (_, _) => _refreshCts?.Cancel();
        }

        private UIElement BuildUi()
        {
            AutomationProperties.SetAutomationId(this, "MediaGallery_View");

            var root = new StackPanel { Spacing = 12 };

            var headerPanel = new StackPanel { Spacing = 4 };
            var title = new TextBlock
            {
                Text = "Media Gallery",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold
            };
            AutomationProperties.SetAutomationId(title, "MediaGallery_Title");
            headerPanel.Children.Add(title);

            var subtitle = new TextBlock
            {
                Text = "Browse generated images and videos. Choose Preview for local playback, the full prompt and output details.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                FontSize = 13
            };
            headerPanel.Children.Add(subtitle);
            root.Children.Add(headerPanel);

            // Filter bar
            var filterBar = new Grid
            {
                ColumnSpacing = 10,
                RowSpacing = 8,
                Margin = new Thickness(0, 4, 0, 4)
            };
            filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            filterBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            filterBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AutomationProperties.SetAutomationId(_searchBox, "MediaGallery_Search_Box");
            _searchBox.TextChanged += (_, _) => { _pageIndex = 0; ApplyFilterAndSort(); };
            _searchBox.MinWidth = 0;
            Grid.SetColumnSpan(_searchBox, 2);
            filterBar.Children.Add(_searchBox);

            _filterCombo.Items.Add(new ComboBoxItem { Content = "All Media", Tag = MediaGalleryFilter.All });
            _filterCombo.Items.Add(new ComboBoxItem { Content = "Images Only", Tag = MediaGalleryFilter.ImagesOnly });
            _filterCombo.Items.Add(new ComboBoxItem { Content = "Videos Only", Tag = MediaGalleryFilter.VideosOnly });
            _filterCombo.Items.Add(new ComboBoxItem { Content = "Final Only", Tag = MediaGalleryFilter.FinalOnly });
            _filterCombo.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(_filterCombo, "MediaGallery_Filter_Combo");
            AutomationProperties.SetName(_filterCombo, "Filter media");
            _filterCombo.SelectionChanged += (_, _) => { _pageIndex = 0; ApplyFilterAndSort(); };
            Grid.SetRow(_filterCombo, 1);
            _filterCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
            filterBar.Children.Add(_filterCombo);

            _sortCombo.Items.Add(new ComboBoxItem { Content = "Newest First", Tag = MediaGallerySort.NewestFirst });
            _sortCombo.Items.Add(new ComboBoxItem { Content = "Oldest First", Tag = MediaGallerySort.OldestFirst });
            _sortCombo.Items.Add(new ComboBoxItem { Content = "Title (A-Z)", Tag = MediaGallerySort.TitleAscending });
            _sortCombo.Items.Add(new ComboBoxItem { Content = "File Size", Tag = MediaGallerySort.SizeDescending });
            _sortCombo.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(_sortCombo, "MediaGallery_Sort_Combo");
            AutomationProperties.SetName(_sortCombo, "Sort media");
            _sortCombo.SelectionChanged += (_, _) => { _pageIndex = 0; ApplyFilterAndSort(); };
            Grid.SetRow(_sortCombo, 1);
            Grid.SetColumn(_sortCombo, 1);
            filterBar.Children.Add(_sortCombo);

            AutomationProperties.SetAutomationId(_refreshButton, "MediaGallery_Refresh_Button");
            _refreshButton.Click += async (_, _) => await RefreshAsync();
            Grid.SetColumn(_refreshButton, 2);
            filterBar.Children.Add(_refreshButton);

            root.Children.Add(filterBar);

            AutomationProperties.SetAutomationId(_status, "MediaGallery_Status_Text");
            root.Children.Add(_status);

            AutomationProperties.SetAutomationId(_emptyState, "MediaGallery_EmptyState_Text");
            root.Children.Add(_emptyState);

            AutomationProperties.SetAutomationId(_cardsPanel, "MediaGallery_Cards_Panel");
            root.Children.Add(_cardsPanel);

            // Avoid materializing every thumbnail into WinUI/decoded-image
            // memory at once; one page of 24 stays fully keyboard accessible.
            AutomationProperties.SetAutomationId(_previousPage, "MediaGallery_PreviousPage_Button");
            AutomationProperties.SetAutomationId(_nextPage, "MediaGallery_NextPage_Button");
            AutomationProperties.SetAutomationId(_pageStatus, "MediaGallery_PageStatus_Text");
            AutomationProperties.SetName(_previousPage, "Previous media page");
            AutomationProperties.SetName(_nextPage, "Next media page");
            _previousPage.Click += (_, _) => { _pageIndex--; ApplyFilterAndSort(); };
            _nextPage.Click += (_, _) => { _pageIndex++; ApplyFilterAndSort(); };
            var pager = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            pager.Children.Add(_previousPage);
            pager.Children.Add(_pageStatus);
            pager.Children.Add(_nextPage);
            root.Children.Add(pager);

            return root;
        }

        public async Task RefreshAsync()
        {
            // The previous request owns its CTS until its own finally block.
            // Disposing it while its HTTP/file operations are still active
            // can race with cancellation registrations in those operations.
            _refreshCts?.Cancel();
            var request = new CancellationTokenSource();
            _refreshCts = request;
            CancellationToken ct = request.Token;

            try
            {
                _status.Text = "Loading media outputs…";
                List<MediaGalleryItem> discovered = await DiscoverMediaOutputsAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_refreshCts, request)) return;
                _allItems.Clear();
                _allItems.AddRange(discovered);
                ApplyFilterAndSort();
                _status.Text = $"Gallery updated: {_allItems.Count} media output(s) available.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (ReferenceEquals(_refreshCts, request) && !ct.IsCancellationRequested)
                    _status.Text = "Gallery refresh failed: " + ex.Message;
            }
            finally
            {
                if (ReferenceEquals(_refreshCts, request)) _refreshCts = null;
                request.Dispose();
            }
        }

        public void SetItems(IEnumerable<MediaGalleryItem> items)
        {
            _allItems.Clear();
            if (items != null) _allItems.AddRange(items);
            ApplyFilterAndSort();
        }

        private async Task<List<MediaGalleryItem>> DiscoverMediaOutputsAsync(CancellationToken ct)
        {
            var results = new List<MediaGalleryItem>();
            var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Query Artifact Workspace for image and video artifacts
            try
            {
                ArtifactListResponse response = await ArtifactWorkspaceClient.Current.ListAsync(null, ct);
                ArtifactDto[] artifacts = response.Artifacts.Where(a =>
                    !string.Equals(a.Status, "deleted", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(a.ArtifactType, "image", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a.ArtifactType, "video", StringComparison.OrdinalIgnoreCase))).ToArray();

                // Fixed-size batches (not one Task per archive item). This
                // allocates at most four concurrent metadata requests even
                // for a 10,000-output library, and cancellation is checked
                // between batches. Keep local ordering stable for paging.
                async Task<MediaGalleryItem> LoadArtifactAsync(ArtifactDto artifact)
                {
                    try
                    {
                        ArtifactVersionListResponse versions = await ArtifactWorkspaceClient.Current
                            .ListVersionsAsync(artifact.Id, ct).ConfigureAwait(false);
                        ArtifactVersionDto? latest = versions.Versions
                            .OrderByDescending(v => v.VersionNumber).FirstOrDefault();
                        return MediaGalleryItemMapper.FromArtifact(artifact, latest);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        return MediaGalleryItemMapper.FromArtifact(artifact, null);
                    }
                }

                for (int offset = 0; offset < artifacts.Length; offset += 4)
                {
                    ct.ThrowIfCancellationRequested();
                    ArtifactDto[] batch = artifacts.Skip(offset).Take(4).ToArray();
                    MediaGalleryItem[] loaded = await Task.WhenAll(batch.Select(LoadArtifactAsync));
                    ct.ThrowIfCancellationRequested();
                    foreach (MediaGalleryItem item in loaded)
                    {
                        if (!string.IsNullOrWhiteSpace(item.FilePath))
                            knownPaths.Add(Path.GetFullPath(item.FilePath));
                        results.Add(item);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _status.Text = "Artifact Workspace media query notice: " + ex.Message;
            }

            // 2. Directory enumeration and FileInfo metadata may involve
            // thousands of files (and slow drives). Scan off the UI thread,
            // but return to the caller's synchronization context afterwards.
            try
            {
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    AppPaths.EnsureCreated();
                    ScanDirectory(AppPaths.GeneratedImagesDir, ".png", ".jpg", ".jpeg", ".webp");
                    ScanDirectory(AppPaths.GeneratedVideosDir, ".mp4", ".webm", ".mov");
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Write("Media directory scan warning: " + ex.Message);
            }

            void ScanDirectory(string directory, params string[] extensions)
            {
                if (!Directory.Exists(directory)) return;
                var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    string ext = Path.GetExtension(file);
                    if (!extSet.Contains(ext)) continue;

                    string fullPath = Path.GetFullPath(file);
                    if (knownPaths.Add(fullPath))
                    {
                        results.Add(MediaGalleryItemMapper.FromFile(
                            fullPath,
                            prompt: Path.GetFileNameWithoutExtension(fullPath),
                            model: "Local Media Output",
                            createdUtc: File.GetCreationTimeUtc(fullPath),
                            status: "final"));
                    }
                }
            }

            return results;
        }

        private void ApplyFilterAndSort()
        {
            var filter = MediaGalleryFilter.All;
            if (_filterCombo.SelectedItem is ComboBoxItem fItem && fItem.Tag is MediaGalleryFilter fVal)
                filter = fVal;

            var sort = MediaGallerySort.NewestFirst;
            if (_sortCombo.SelectedItem is ComboBoxItem sItem && sItem.Tag is MediaGallerySort sVal)
                sort = sVal;

            string search = _searchBox.Text ?? string.Empty;

            IReadOnlyList<MediaGalleryItem> filtered = MediaGalleryBrowser.FilterAndSort(_allItems, filter, sort, search);
            MediaGalleryBrowser.Page page = MediaGalleryBrowser.Paginate(filtered, _pageIndex);
            _pageIndex = page.PageIndex;
            _pageStatus.Text = page.TotalMatches + " results · page "
                + (page.PageIndex + 1) + "/" + page.PageCount;
            _previousPage.IsEnabled = page.PageIndex > 0;
            _nextPage.IsEnabled = page.PageIndex + 1 < page.PageCount;
            _pageStatus.Visibility = page.TotalMatches == 0 ? Visibility.Collapsed : Visibility.Visible;

            _cardsPanel.Children.Clear();
            _emptyState.Visibility = page.TotalMatches == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (MediaGalleryItem item in page.Visible)
            {
                _cardsPanel.Children.Add(BuildCard(item));
            }
        }

        private UIElement BuildCard(MediaGalleryItem item)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.DimGray),
                Padding = new Thickness(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 255, 255, 255))
            };
            AutomationProperties.SetAutomationId(card, $"MediaGallery_Card_{item.Id}");

            var grid = new Grid { ColumnSpacing = 14 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Thumbnail container
            var thumbBox = new Border
            {
                Width = 160,
                Height = 110,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 160, 110) }
            };

            if (item.IsImage && item.FileExists)
            {
                try
                {
                    var image = new Image
                    {
                        // Decode near thumbnail resolution rather than
                        // materializing a full 4K/8K image for a 160x110 card.
                        Source = new BitmapImage(new Uri(item.FilePath))
                        {
                            DecodePixelWidth = 320,
                            DecodePixelHeight = 220
                        },
                        Stretch = Stretch.UniformToFill
                    };
                    thumbBox.Child = image;
                }
                catch
                {
                    thumbBox.Child = new TextBlock
                    {
                        Text = "🖼 IMAGE",
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Opacity = 0.7
                    };
                }
            }
            else if (item.IsVideo)
            {
                var videoStack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 4
                };
                videoStack.Children.Add(new TextBlock
                {
                    Text = "🎬 VIDEO",
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                if (!string.IsNullOrWhiteSpace(item.FormattedDimensions))
                {
                    videoStack.Children.Add(new TextBlock
                    {
                        Text = item.FormattedDimensions,
                        FontSize = 11,
                        Opacity = 0.75,
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                }
                thumbBox.Child = videoStack;
            }
            else
            {
                thumbBox.Child = new TextBlock
                {
                    Text = item.ArtifactType.ToUpperInvariant(),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Opacity = 0.7
                };
            }
            Grid.SetColumn(thumbBox, 0);
            grid.Children.Add(thumbBox);

            // Metadata & Actions
            var contentPanel = new StackPanel { Spacing = 4 };

            var promptText = new TextBlock
            {
                Text = item.Prompt,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTipService.SetToolTip(promptText, item.Prompt);
            AutomationProperties.SetAutomationId(promptText, $"MediaGallery_Prompt_{item.Id}");
            contentPanel.Children.Add(promptText);

            var detailsText = new TextBlock
            {
                Text = item.FormattedDetails,
                FontSize = 12,
                Opacity = 0.75,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            AutomationProperties.SetAutomationId(detailsText, $"MediaGallery_Details_{item.Id}");
            contentPanel.Children.Add(detailsText);

            // Action Buttons
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var openBtn = new Button
            {
                Content = "Open",
                IsEnabled = MediaGalleryBrowser.CanOpen(item)
            };
            AutomationProperties.SetAutomationId(openBtn, $"MediaGallery_Open_{item.Id}");
            openBtn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
                    _status.Text = $"Opened {item.Title}.";
                }
                catch (Exception ex)
                {
                    _status.Text = "Failed to open output: " + ex.Message;
                }
            };
            actions.Children.Add(openBtn);

            var detailsBtn = new Button { Content = "Preview" };
            AutomationProperties.SetAutomationId(detailsBtn, $"MediaGallery_ViewDetails_{item.Id}");
            AutomationProperties.SetName(detailsBtn, "Preview media and show full output details");
            detailsBtn.Click += async (_, _) => await ShowDetailsAsync(item);
            actions.Children.Add(detailsBtn);

            var revealBtn = new Button
            {
                Content = "Reveal in folder",
                IsEnabled = MediaGalleryBrowser.CanReveal(item)
            };
            AutomationProperties.SetAutomationId(revealBtn, $"MediaGallery_Reveal_{item.Id}");
            revealBtn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true });
                    _status.Text = $"Revealed {item.Title} in Explorer.";
                }
                catch (Exception ex)
                {
                    _status.Text = "Failed to reveal in folder: " + ex.Message;
                }
            };
            actions.Children.Add(revealBtn);

            var reuseBtn = new Button
            {
                Content = "Reuse as edit source",
                IsEnabled = MediaGalleryBrowser.CanReuseAsEditSource(item)
            };
            AutomationProperties.SetAutomationId(reuseBtn, $"MediaGallery_ReuseEdit_{item.Id}");
            reuseBtn.Click += (_, _) =>
            {
                SetEditSourceResult result = MediaEditSourceBridge.Current.SetEditSource(item.FilePath, item.Prompt);
                _status.Text = result.Message;
                if (result.Success)
                    ItemReusedAsEditSource?.Invoke(this, item);
            };
            actions.Children.Add(reuseBtn);

            var deleteBtn = new Button
            {
                Content = "Delete",
                IsEnabled = CanPermanentlyDelete(item)
            };
            AutomationProperties.SetAutomationId(deleteBtn, $"MediaGallery_Delete_{item.Id}");
            deleteBtn.Click += async (_, _) => await HandleDeleteWithConfirmationAsync(item);
            actions.Children.Add(deleteBtn);

            contentPanel.Children.Add(actions);

            Grid.SetColumn(contentPanel, 1);
            grid.Children.Add(contentPanel);

            // Window sizes and DPI scaling vary by PC. At narrow widths, the
            // thumbnail, metadata and action buttons stack instead of clipping
            // behind the gallery edge or creating a horizontal scrollbar.
            bool? compactLayout = null;
            grid.SizeChanged += (_, args) =>
            {
                bool compact = args.NewSize.Width < 620;
                if (compactLayout == compact) return;
                compactLayout = compact;
                grid.ColumnSpacing = compact ? 0 : 14;
                grid.RowSpacing = compact ? 10 : 0;
                grid.ColumnDefinitions[0].Width = compact
                    ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
                grid.ColumnDefinitions[1].Width = compact
                    ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
                Grid.SetRow(contentPanel, compact ? 1 : 0);
                Grid.SetColumn(contentPanel, compact ? 0 : 1);
                actions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
                foreach (UIElement child in actions.Children)
                    if (child is Button button)
                        button.HorizontalAlignment = compact
                            ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            };

            card.Child = grid;
            return card;
        }

        private async Task ShowDetailsAsync(MediaGalleryItem item)
        {
            // Instantiate a full preview only when the user asks for Details.
            // The gallery itself keeps at most one page of tiny image thumbnails,
            // and no video decoder resident while browsing.
            var details = new StackPanel { Spacing = 8 };
            MediaPlayerElement? previewPlayer = null;
            MediaGalleryPreviewKind preview = MediaGalleryPreviewPolicy.KindFor(item);
            bool gamingActive = GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive();
            if (preview == MediaGalleryPreviewKind.Video && gamingActive)
            {
                details.Children.Add(new TextBlock
                {
                    Text = "Video preview is paused while Gaming owns the GPU. Leave Gaming before playing this output.",
                    TextWrapping = TextWrapping.Wrap
                });
            }
            else if (preview != MediaGalleryPreviewKind.Unavailable)
            {
                details.Children.Add(new TextBlock
                {
                    Text = "Preview", FontWeight = FontWeights.SemiBold
                });
                try
                {
                    if (preview == MediaGalleryPreviewKind.Image)
                    {
                        var imagePreview = new Image
                        {
                            Source = new BitmapImage(new Uri(item.FilePath))
                            {
                                DecodePixelWidth = MediaGalleryPreviewPolicy.PreviewDecodeWidth
                            },
                            Stretch = Stretch.Uniform,
                            MaxHeight = 320,
                            MaxWidth = 600,
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        AutomationProperties.SetAutomationId(imagePreview, "MediaGallery_DetailImagePreview");
                        AutomationProperties.SetName(imagePreview, "Local image preview: " + item.Title);
                        details.Children.Add(imagePreview);
                    }
                    else
                    {
                        previewPlayer = new MediaPlayerElement
                        {
                            Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(item.FilePath)),
                            AreTransportControlsEnabled = true,
                            AutoPlay = false,
                            MaxWidth = 520,
                            Height = 294,
                            HorizontalAlignment = HorizontalAlignment.Stretch
                        };
                        AutomationProperties.SetAutomationId(previewPlayer, "MediaGallery_DetailVideoPreview");
                        AutomationProperties.SetName(previewPlayer, "Local video preview: " + item.Title);
                        details.Children.Add(previewPlayer);
                    }
                }
                catch (Exception ex)
                {
                    // Local decoders can also fail with WinRT/COM exceptions.
                    // Keep metadata accessible without crashing the gallery.
                    Log.Write("Media Gallery preview unavailable: " + ex.GetType().Name);
                    previewPlayer = null;
                    details.Children.Add(new TextBlock
                    {
                        Text = "Preview unavailable. You can still inspect the metadata and file location.",
                        TextWrapping = TextWrapping.Wrap
                    });
                }
            }
            else
            {
                details.Children.Add(new TextBlock
                {
                    Text = "No local preview is available for this file. The original output is not changed.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                });
            }

            // Keep complete technical metadata inspectable and copyable.
            details.Children.Add(new TextBlock { Text = "Prompt", FontWeight = FontWeights.SemiBold });
            var prompt = new TextBox
            {
                Text = item.Prompt ?? string.Empty,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 108
            };
            AutomationProperties.SetAutomationId(prompt, $"MediaGallery_FullPrompt_{item.Id}");
            details.Children.Add(prompt);
            details.Children.Add(new TextBlock { Text = "Metadata", FontWeight = FontWeights.SemiBold });
            details.Children.Add(new TextBlock { Text = item.FormattedDetails, TextWrapping = TextWrapping.Wrap });
            details.Children.Add(new TextBlock { Text = "File location", FontWeight = FontWeights.SemiBold });
            var path = new TextBox { Text = item.FilePath, IsReadOnly = true };
            AutomationProperties.SetAutomationId(path, $"MediaGallery_Path_{item.Id}");
            details.Children.Add(path);

            var dialog = new ContentDialog
            {
                Title = string.IsNullOrWhiteSpace(item.Title) ? "Media details" : item.Title,
                Content = new ScrollViewer { Content = details, MaxHeight = 420 },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            void ReleaseVideoPreview()
            {
                if (previewPlayer == null) return;
                try { previewPlayer.MediaPlayer?.Pause(); }
                catch (Exception ex) { Log.Write("Media Gallery preview pause: " + ex.GetType().Name); }
                try { previewPlayer.Source = null; }
                catch (Exception ex) { Log.Write("Media Gallery preview release: " + ex.GetType().Name); }
            }

            // Gaming can begin from another part of PaRuX while the dialog is open.
            // End video decoding as soon as that mode takes ownership; do not resume
            // automatically after the game exits.
            EventHandler? onGamingChange = null;
            if (previewPlayer != null)
            {
                onGamingChange = (_, _) =>
                {
                    if (!GamingManager.Current.IsActive && !LocalAiController.Current.IsGameActive()) return;
                    DispatcherQueue.TryEnqueue(ReleaseVideoPreview);
                };
                GamingManager.Current.StateChanged += onGamingChange;
            }
            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                if (onGamingChange != null) GamingManager.Current.StateChanged -= onGamingChange;
                // A video preview must not keep playing or holding decoder/GPU
                // resources after the dialog closes.
                ReleaseVideoPreview();
            }
        }

        private static bool CanPermanentlyDelete(MediaGalleryItem item)
        {
            // Only the app's generated folders or the owner's explicit output
            // folders qualify. Imported or external Artifact ContentRefs are
            // not owned merely because they are visible in this gallery.
            if (item.IsVideo)
                return MediaGalleryBrowser.CanDelete(item,
                    AppPaths.GeneratedVideosDir, SettingsStore.Current.Data.GeneratedVideosFolder);
            if (item.IsImage)
                return MediaGalleryBrowser.CanDelete(item,
                    AppPaths.GeneratedImagesDir, SettingsStore.Current.Data.GeneratedImagesFolder);
            return false;
        }

        private async Task HandleDeleteWithConfirmationAsync(MediaGalleryItem item)
        {
            if (!CanPermanentlyDelete(item))
            {
                _status.Text = "This file is outside the verified generated-media folders or has changed. "
                    + "PaRuX will not delete an external or linked file. Use your file manager if you own it.";
                return;
            }
            _deleteCoordinator.RequestDelete(item.Id, item.Title, item.FilePath);

            var confirmDialog = new ContentDialog
            {
                Title = "Delete Media Output",
                Content = $"Are you sure you want to permanently delete '{item.Title}'?\n\nPath: {item.FilePath}\n\nThis action cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            ContentDialogResult result = await confirmDialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                _deleteCoordinator.CancelDelete(item.Id);
                _status.Text = "Deletion cancelled.";
                return;
            }

            DeleteConfirmationResult delResult = _deleteCoordinator.ConfirmDelete(item.Id, path =>
            {
                // Re-check at the last possible moment: the configured output
                // folder, file identity or a directory junction can change
                // while the confirmation dialog is open.
                if (!CanPermanentlyDelete(item))
                    throw new InvalidOperationException(
                        "The output is no longer within a verified generated-media folder.");
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
                return false;
            });

            if (delResult.Success)
            {
                if (!string.IsNullOrWhiteSpace(item.ArtifactId))
                {
                    try
                    {
                        await ArtifactWorkspaceClient.Current.SetStatusAsync(item.ArtifactId, "deleted", CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Artifact status set to deleted notice: " + ex.Message);
                    }
                }

                _allItems.RemoveAll(i => i.Id == item.Id);
                ApplyFilterAndSort();
                _status.Text = $"Permanently deleted media output '{item.Title}'.";
                ItemDeleted?.Invoke(this, item);
            }
            else
            {
                _status.Text = "Delete failed: " + (delResult.Error ?? "unknown error");
            }
        }
    }
}
