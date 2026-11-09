using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.UI;

namespace PaRuX.Views
{
    /// <summary>
    /// The Model Hub "Start" tab: one tile per capability (chat, images, editing, video, voice, music, 3D) that says in plain
    /// words what is ready, what is missing (the engine, the model, or both) and offers exactly one next step. It reuses the
    /// existing engine setup, consent-gated download and activation paths; it adds no new way to download or install anything.
    /// </summary>
    public sealed partial class ModelHubView
    {
        private static bool StartItalian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";

        private readonly VariableSizedWrapGrid _startCards = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 400,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        private readonly TextBlock _startSummary = new TextBlock { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        private readonly TextBlock _startHardware = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78 };
        private readonly Dictionary<string, MediaSetupStatus?> _startEngineStatuses = new Dictionary<string, MediaSetupStatus?>(StringComparer.Ordinal);
        private readonly Dictionary<string, RecommendedCheckpoint> _startMediaOffers = new Dictionary<string, RecommendedCheckpoint>(StringComparer.Ordinal);
        private IReadOnlyList<RecommendedModel> _startRecommended = Array.Empty<RecommendedModel>();
        private List<MediaCheckpointStatus> _startMediaStatuses = new List<MediaCheckpointStatus>();
        private RecommendedModel? _startTextOffer;
        private bool _startHubLoaded;
        private bool _startModelsKnown;
        private SimpleModeRuntimeHealthInfo? _startRuntime;
        private int _startCheckingRetries;
        private readonly HashSet<string> _startInFlight = new HashSet<string>(StringComparer.Ordinal);
        private DispatcherTimer? _startPoll;
        private RoleProfilePanel? _roleProfilePanel;

        private UIElement BuildStartPanel()
        {
            bool it = StartItalian;
            var panel = new StackPanel { Spacing = 14 };
            AutomationProperties.SetAutomationId(panel, "ModelHub_Start_Panel");

            var intro = new StackPanel { Spacing = 4 };
            var heading = new TextBlock
            {
                Text = it ? "Cosa vuoi fare?" : "What do you want to do?",
                FontSize = 22,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            intro.Children.Add(heading);
            intro.Children.Add(new TextBlock
            {
                Text = it
                    ? "Ogni funzione ha bisogno di un motore e di un modello. Qui vedi cosa è pronto e cosa manca, un passo alla volta. Tutto gira sul tuo PC e nulla viene scaricato senza il tuo OK."
                    : "Every ability needs an engine and a model. Here you see what is ready and what is missing, one step at a time. Everything runs on your PC and nothing downloads without your OK.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.82
            });
            if (CorporateMode.IsActive)
            {
                // No generic multi-engine dashboard, duplicated headers, media checks or
                // hardware/benchmark detail in the CPU-only corporate model manager.
                panel.Children.Add(BuildCorporateModelCard());
                _startCards.Visibility = Visibility.Collapsed;
                _startSummary.Visibility = Visibility.Collapsed;
                _startHardware.Visibility = Visibility.Collapsed;
            }
            else
            {
                panel.Children.Add(intro);
            }

            AutomationProperties.SetAutomationId(_startSummary, "ModelHub_Start_Summary");
            AutomationProperties.SetAutomationId(_startHardware, "ModelHub_Start_Hardware");
            panel.Children.Add(_startSummary);
            panel.Children.Add(_startHardware);
            panel.Children.Add(_startCards);
            // "Use for": which installed model serves Chat / Coding / Reasoning / Vision / Documents (reads the existing role store).
            _roleProfilePanel = new RoleProfilePanel(startAdvanced: _isAdvancedMode);
            panel.Children.Add(_roleProfilePanel);

            var more = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            more.Children.Add(new TextBlock
            {
                Text = CorporateMode.IsActive
                    ? (it ? "Libreria:" : "Library:")
                    : it ? "Cerca altri modelli:" : "Browse more models:",
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.78
            });
            foreach ((ModelHubTab tab, string en, string itLabel) in new[]
            {
                (ModelHubTab.Text, "Chat models", "Modelli di chat"),
                (ModelHubTab.Media, "Image, video & audio", "Immagini, video e audio"),
                (ModelHubTab.Downloads, "Downloads", "Download")
            })
            {
                if (CorporateMode.IsActive && (tab == ModelHubTab.Media
                    || (tab == ModelHubTab.Downloads && !CorporateMode.ModelDownloadsAllowed))) continue;
                ModelHubTab target = tab;
                var button = new Button { Content = it ? itLabel : en };
                AutomationProperties.SetAutomationId(button, "ModelHub_Start_Browse_" + tab);
                button.Click += (_, _) => ShowHubTab(target);
                more.Children.Add(button);
            }
            panel.Children.Add(more);

            Unloaded += (_, _) => _startPoll?.Stop();
            RenderStart(ModelHubStartPresenter.Build(BuildStartInputs(null, false), it));
            return panel;
        }

        /// <summary>Selects the "Chat models" tab. Used by the debug UI-test entry point that opens the hub directly.</summary>
        internal void ShowChatModelsTab() => ShowHubTab(ModelHubTab.Text);

        private void ShowHubTab(ModelHubTab tab)
        {
            if (_tabBar != null && _tabItems.TryGetValue(tab, out SelectorBarItem? item)) _tabBar.SelectedItem = item;
            else SelectHubTab(tab);
        }

        // ---- Data ----------------------------------------------------------------------------------------------------

        /// <summary>Refreshes the board. Each card is updated as soon as its own data arrives: a slow engine check (image-to-3D
        /// verifies a Python environment and can take many seconds) never keeps the other cards on "Checking…".</summary>
        private Task RefreshStartAsync()
        {
            RefreshStart(all: true);
            return Task.CompletedTask;
        }

        private int _startAutoAssignedModelCount = -1;

        private void RefreshStart(bool all)
        {
            if (CorporateMode.IsActive)
            {
                // No media service/network requests in the CPU-only corporate profile.
                _ = RefreshStartTextAsync();
                return;
            }
            foreach (MediaEngineDefinition engine in MediaEngineHubPresenter.Engines)
            {
                bool needsLook = all
                    || !_startEngineStatuses.TryGetValue(engine.Type, out MediaSetupStatus? known) || known == null
                    || string.Equals(known.State, "running", StringComparison.OrdinalIgnoreCase);
                if (needsLook) _ = RefreshStartEngineAsync(engine.Type);
            }
            _ = RefreshStartTextAsync();
        }

        private async Task RefreshStartEngineAsync(string type)
        {
            if (!_startInFlight.Add("engine:" + type)) return;
            try
            {
                MediaSetupStatus status = await MediaSetupClient.Current.GetAsync(type, CancellationToken.None);
                _startEngineStatuses[type] = status;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write("Model Hub Start: engine status for '" + type + "' unavailable: " + ex.Message);
            }
            finally { _startInFlight.Remove("engine:" + type); }
            RenderStartNow();
        }

        private async Task RefreshStartTextAsync()
        {
            if (!_startInFlight.Add("text")) return;
            try
            {
                Task<SimpleModeRuntimeHealthInfo?> runtimeTask = TryGetStartRuntimeAsync();
                Task<List<ModelDownloadStatus>?> jobsTask = TryGetStartJobsAsync();
                Task<List<ManagedModelInfo>?> modelsTask = TryGetStartModelsAsync();
                await Task.WhenAll(runtimeTask, jobsTask, modelsTask);
                _startRuntime = runtimeTask.Result ?? _startRuntime;
                if (jobsTask.Result is { } jobs) _downloadJobs = jobs;
                if (modelsTask.Result is { } models) { _managedModels = models; _startModelsKnown = true; }

                // A freshly downloaded model must be usable at once: without a role assignment Chat still points at the
                // factory default file (not downloaded) and fails with HTTP 503 while this board says "Ready". The server
                // only fills roles the person has not chosen; it never replaces a choice they made.
                if (_startModelsKnown && _managedModels.Count > 0 && _managedModels.Count != _startAutoAssignedModelCount)
                {
                    _startAutoAssignedModelCount = _managedModels.Count;
                    try { await ModelDownloadClient.Current.AutoAssignRolesAsync(CancellationToken.None); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { Log.Write("Model Hub Start: role auto-assign after model change failed: " + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write("Model Hub Start refresh failed: " + ex.Message);
            }
            finally { _startInFlight.Remove("text"); }
            RenderStartNow();
            if (_roleProfilePanel != null) _ = _roleProfilePanel.RefreshAsync();
        }

        private void RenderStartNow()
        {
            IReadOnlyList<StartCard> cards = ModelHubStartPresenter.Build(BuildStartInputs(_startRuntime, _startModelsKnown), StartItalian);
            RenderStart(cards);
            UpdateStartHardware();
            UpdateStartPolling(cards);
        }

        private static async Task<SimpleModeRuntimeHealthInfo?> TryGetStartRuntimeAsync()
        {
            try
            {
                List<ComponentStatus> components = await RuntimeManagerClient.Current.GetComponentsAsync(CancellationToken.None);
                return ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(components);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }

        private static async Task<List<ModelDownloadStatus>?> TryGetStartJobsAsync()
        {
            try { return await ModelDownloadClient.Current.ListDownloadsAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }

        private static async Task<List<ManagedModelInfo>?> TryGetStartModelsAsync()
        {
            try { return await ModelDownloadClient.Current.ListManagedModelsAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }

        private void UpdateStartHardware()
        {
            string line = _hardwareSummary.Text;
            if (StartItalian) line = line.Replace("This PC:", "Il tuo PC:", StringComparison.Ordinal).Replace(" dedicated (", " dedicati (", StringComparison.Ordinal);
            if (_storageBudget?.DestinationFreeBytes is long free && free > 0)
                line += (StartItalian ? " · spazio libero per i modelli " : " · free space for models ") + ModelHubStartPresenter.FormatSize(free);
            _startHardware.Text = line;
        }

        private IReadOnlyList<StartCapabilityInput> BuildStartInputs(SimpleModeRuntimeHealthInfo? runtime, bool modelsKnown)
        {
            bool it = StartItalian;
            var result = new List<StartCapabilityInput>();
            _startMediaOffers.Clear();

            // Chat: the text runtime (managed in Settings) plus a GGUF model.
            var chatEngine = new StartEngineInput(
                it ? "Motore di chat (llama.cpp)" : "Chat engine (llama.cpp)",
                runtime != null, runtime?.IsInstalled == true, "idle", 0, 0, false, null);
            result.Add(new StartCapabilityInput("chat", chatEngine, BuildStartTextModel(modelsKnown && _startHubLoaded)));

            // Media: engine status from the setup API, model facts from the catalogue.
            foreach (MediaEngineDefinition engine in MediaEngineHubPresenter.Engines)
            {
                string type = engine.Type;
                _startEngineStatuses.TryGetValue(type, out MediaSetupStatus? status);
                bool known = _startEngineStatuses.ContainsKey(type) && status != null;
                long pending = status == null ? 0 : status.Steps.Where(s => s.State != "done").Sum(s => s.SizeBytes);
                double progress = status is { TotalBytes: > 0 } ? Math.Clamp((double)status.BytesDownloaded / status.TotalBytes, 0, 1) : 0;
                bool running = string.Equals(status?.State, "running", StringComparison.OrdinalIgnoreCase);
                string? error = status == null ? null : CustomCheckpointPresenter.ExtractServerError(status.Error ?? string.Empty);
                if (string.IsNullOrWhiteSpace(error)) error = null;
                var engineInput = new StartEngineInput(
                    StartEngineTitle(type, engine.Title, it), known, status?.Ready == true, status?.State ?? "idle", pending,
                    progress, running && (status?.TotalBytes ?? 0) <= 0, error);
                result.Add(new StartCapabilityInput(type, engineInput, BuildStartMediaModel(type)));
            }

            // Present in the board's own order (chat, image, edit, video, speech, music, 3d).
            return ModelHubStartPresenter.CapabilityOrder
                .Select(id => result.First(r => r.Id == id)).ToList();
        }

        private static string StartEngineTitle(string type, string fallback, bool it) => type switch
        {
            "image" => it ? "Motore immagini (ComfyUI)" : "Image engine (ComfyUI)",
            "edit" => it ? "Motore modifica immagini (ComfyUI)" : "Image-edit engine (ComfyUI)",
            "video" => it ? "Motore video (ComfyUI)" : "Video engine (ComfyUI)",
            "speech" => it ? "Motore voce" : "Speech engine",
            "music" => it ? "Motore musica" : "Music engine",
            "3d" => it ? "Motore da immagine a 3D" : "Image-to-3D engine",
            _ => fallback
        };

        private StartModelInput BuildStartTextModel(bool known)
        {
            bool it = StartItalian;
            if (!known) return new StartModelInput(false, null, null, 0, null, false, false, 0, false);

            string? active = null;
            if (_managedModels.Count > 0)
            {
                string? loaded = string.IsNullOrWhiteSpace(_hardware?.ActiveModelPath)
                    ? null
                    : Path.GetFileNameWithoutExtension(_hardware!.ActiveModelPath!);
                ManagedModelInfo first = _managedModels.OrderByDescending(m => m.LastWriteTimeUtc).First();
                active = loaded ?? ModelHubRowPresentation.CompactManualLabel(first.Alias, first.FileName);
                if (_managedModels.Count > 1)
                    active += it ? $" (+{_managedModels.Count - 1} altri)" : $" (+{_managedModels.Count - 1} more)";
            }

            RecommendedModel? offer = null;
            string? fitWords = null;
            foreach (RecommendedModel candidate in _startRecommended)
            {
                ModelHardwareFitEstimate fit = ModelHardwareFitEstimator.Estimate(candidate.EstimatedSizeBytes, _hardware);
                if (fit.Verdict == ModelFitVerdict.Big) continue;
                offer = candidate;
                fitWords = fit.Verdict switch
                {
                    ModelFitVerdict.Fits => it ? "ci sta bene nel tuo PC" : "fits your PC",
                    ModelFitVerdict.Tight => it ? "al limite per il tuo PC" : "a tight fit for your PC",
                    _ => null
                };
                break;
            }
            _startTextOffer = offer;

            ModelDownloadStatus? running = _downloadJobs.FirstOrDefault(j => IsActive(j)
                && j.FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));
            double progress = running is { TotalBytes: > 0 } ? Math.Clamp((double)running.BytesDownloaded / running.TotalBytes, 0, 1) : 0;
            return new StartModelInput(true, active, offer?.Title, offer?.EstimatedSizeBytes ?? 0, fitWords,
                offer != null, running != null, progress, false);
        }

        /// <summary>Null when the capability has no model of its own to choose (image-to-3D).</summary>
        private StartModelInput? BuildStartMediaModel(string type)
        {
            bool it = StartItalian;
            string? capabilityId = type switch
            {
                "image" => "image.generate",
                "edit" => "image.edit",
                "video" => "video.generate",
                "speech" => "audio.speak",
                "music" => "audio.music",
                _ => null
            };
            if (capabilityId == null) return null; // 3D has an engine but no separately chosen model.
            if (_mediaCatalogue == null) return new StartModelInput(false, null, null, 0, null, false, false, 0, false);

            MediaCatalogueCapabilityDto? capability = _mediaCatalogue.Capabilities
                .FirstOrDefault(c => string.Equals(c.CapabilityId, capabilityId, StringComparison.OrdinalIgnoreCase));
            if (capability == null) return null;

            // What is in use: the pinned/automatic checkpoint file, else an installed catalogue entry.
            MediaCheckpointStatus? current = _startMediaStatuses.FirstOrDefault(s => string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase));
            string? active = null;
            if (!string.IsNullOrWhiteSpace(current?.CurrentFile))
            {
                active = capability.Entries.FirstOrDefault(e => string.Equals(e.FileName, current!.CurrentFile, StringComparison.OrdinalIgnoreCase))?.Title
                    ?? current!.CurrentFile;
            }
            else
            {
                MediaCatalogueEntryDto? installed = capability.Entries.FirstOrDefault(e => e.Installed && e.UsedAutomatically)
                    ?? capability.Entries.FirstOrDefault(e => e.Installed);
                active = installed?.Title;
            }

            // What PaRuX would download: the best-fit card of the lane the person is looking at, only if it can really run.
            MediaHubCard? best = MediaHubPresenter.Build(_mediaCatalogue, _mediaLane)
                .FirstOrDefault(c => string.Equals(c.CapabilityId, capabilityId, StringComparison.OrdinalIgnoreCase))?.Best;
            RecommendedCheckpoint? download = best is { Action: MediaHubAction.Download } ? MediaHubPresenter.ToDownload(best.Entry) : null;
            if (download != null) _startMediaOffers[type] = download;

            string? offerFile = download?.FileName ?? best?.Entry.FileName;
            ModelDownloadStatus? job = offerFile == null ? null : _downloadJobs
                .Where(j => string.Equals(j.FileName, offerFile, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.CreatedUtc).FirstOrDefault();
            bool downloading = job != null && IsActive(job);
            double progress = job is { TotalBytes: > 0 } ? Math.Clamp((double)job.BytesDownloaded / job.TotalBytes, 0, 1) : 0;
            bool downloadedNotInstalled = download != null && job != null
                && string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase);

            return new StartModelInput(true, active, best?.Title, best?.Entry.SizeBytes ?? 0,
                best == null ? null : ModelHubStartPresenter.FitWords(best.Entry.FitClass, it),
                download != null, downloading, progress, downloadedNotInstalled);
        }

        private void UpdateStartPolling(IReadOnlyList<StartCard> cards)
        {
            if (CorporateMode.IsActive)
            {
                _startPoll?.Stop();
                return; // No media-install/check loops in the CPU-only Corporate Home.
            }
            bool installing = cards.Any(c => c.Readiness == StartReadiness.Installing);
            bool checking = cards.Any(c => c.Readiness == StartReadiness.Checking);
            if (!checking) _startCheckingRetries = 0;
            // Keep looking while something installs; while a state could not be read yet, retry a few times and then stop
            // rather than polling a server that is not answering forever.
            bool keepGoing = installing || (checking && _startCheckingRetries < 10);
            if (!keepGoing || _activeTab != ModelHubTab.Start)
            {
                _startPoll?.Stop();
                return;
            }
            if (_startPoll == null)
            {
                _startPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _startPoll.Tick += (_, _) =>
                {
                    if (_activeTab != ModelHubTab.Start) { _startPoll?.Stop(); return; }
                    _startCheckingRetries++;
                    RefreshStart(all: false);
                };
            }
            if (!_startPoll.IsEnabled) _startPoll.Start();
        }

        // ---- Rendering -----------------------------------------------------------------------------------------------

        private void RenderStart(IReadOnlyList<StartCard> cards)
        {
            if (CorporateMode.IsActive)
            {
                _startSummary.Text = StartItalian
                    ? "Motore CPU incluso · importa o seleziona un modello GGUF per Chat e documenti."
                    : "CPU runtime included · import or select a GGUF model for Chat and documents.";
                _startCards.Children.Clear();
                return;
            }
            _startSummary.Text = ModelHubStartPresenter.Summarize(cards, StartItalian).Line;
            _startCards.Children.Clear();
            foreach (StartCard card in cards) _startCards.Children.Add(BuildStartCard(card));
        }

        private static Brush StartBrush(string key, Color fallback)
        {
            try { if (Application.Current.Resources[key] is Brush brush) return brush; }
            catch (Exception) { /* theme resource not present: fall back */ }
            return new SolidColorBrush(fallback);
        }

        private UIElement BuildStartCard(StartCard card)
        {
            var root = new Border
            {
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = StartBrush("CardStrokeColorDefaultBrush", Color.FromArgb(40, 255, 255, 255)),
                Background = StartBrush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(12, 255, 255, 255))
            };
            AutomationProperties.SetAutomationId(root, card.AutomationId);
            AutomationProperties.SetName(root, card.Title + ": " + card.StatusText);

            var stack = new StackPanel { Spacing = 10 };

            var head = new Grid { ColumnSpacing = 12 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var iconBox = new Border
            {
                Width = 42, Height = 42, CornerRadius = new CornerRadius(10),
                Background = StartBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(24, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new FontIcon { Glyph = card.Glyph, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            Grid.SetColumn(iconBox, 0);
            head.Children.Add(iconBox);

            var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = card.Title, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level3);
            titles.Children.Add(title);
            titles.Children.Add(new TextBlock { Text = card.Tagline, FontSize = 12, Opacity = 0.78, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(titles, 1);
            head.Children.Add(titles);

            var pill = new Border
            {
                Padding = new Thickness(10, 3, 10, 3),
                CornerRadius = new CornerRadius(12),
                VerticalAlignment = VerticalAlignment.Top,
                Background = card.Readiness switch
                {
                    StartReadiness.Ready => StartBrush("SystemFillColorSuccessBackgroundBrush", Color.FromArgb(60, 0, 160, 80)),
                    StartReadiness.NeedsSetup => StartBrush("SystemFillColorCautionBackgroundBrush", Color.FromArgb(60, 230, 160, 0)),
                    StartReadiness.Problem => StartBrush("SystemFillColorCriticalBackgroundBrush", Color.FromArgb(60, 220, 50, 50)),
                    _ => StartBrush("SystemFillColorNeutralBackgroundBrush", Color.FromArgb(30, 255, 255, 255))
                },
                Child = new TextBlock { Text = card.StatusText, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }
            };
            AutomationProperties.SetAutomationId(pill, card.AutomationId + "_Status");
            Grid.SetColumn(pill, 2);
            head.Children.Add(pill);
            stack.Children.Add(head);

            foreach (StartStep step in card.Steps)
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Brush stepBrush = step.State switch
                {
                    StartStepState.Done => StartBrush("SystemFillColorSuccessBrush", Color.FromArgb(255, 80, 200, 120)),
                    StartStepState.Failed or StartStepState.Blocked => StartBrush("SystemFillColorCriticalBrush", Color.FromArgb(255, 240, 90, 90)),
                    StartStepState.Running => StartBrush("AccentFillColorDefaultBrush", Color.FromArgb(255, 255, 120, 80)),
                    _ => StartBrush("TextFillColorSecondaryBrush", Color.FromArgb(200, 255, 255, 255))
                };
                FrameworkElement glyph = step.State == StartStepState.Todo
                    // A hollow circle for "still to do": the icon font's ring glyph renders filled, which reads as "selected".
                    ? new TextBlock { Text = "○", FontSize = 15, Foreground = stepBrush, VerticalAlignment = VerticalAlignment.Top }
                    : new FontIcon
                    {
                        Glyph = step.State switch
                        {
                            StartStepState.Done => "",
                            StartStepState.Running => "",
                            StartStepState.Failed => "",
                            StartStepState.Blocked => "",
                            _ => ""
                        },
                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 2, 0, 0),
                        Foreground = stepBrush
                    };
                Grid.SetColumn(glyph, 0);
                row.Children.Add(glyph);
                var text = new StackPanel { Spacing = 0 };
                text.Children.Add(new TextBlock { Text = step.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                text.Children.Add(new TextBlock { Text = step.Detail, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
                AutomationProperties.SetName(text, step.Name + ": " + step.Detail);
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                stack.Children.Add(row);
            }

            if (card.Progress.HasValue || card.Indeterminate)
            {
                var bar = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = card.Indeterminate };
                if (!card.Indeterminate) bar.Value = card.Progress ?? 0;
                AutomationProperties.SetAutomationId(bar, card.AutomationId + "_Progress");
                stack.Children.Add(bar);
            }

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
            if (card.Primary.Kind != StartActionKind.None || card.Readiness != StartReadiness.Ready)
                actions.Children.Add(MakeStartButton(card, card.Primary, primary: true));
            if (card.Secondary != null) actions.Children.Add(MakeStartButton(card, card.Secondary, primary: false));
            if (actions.Children.Count > 0) stack.Children.Add(actions);

            root.Child = stack;
            return root;
        }

        private Button MakeStartButton(StartCard card, StartAction action, bool primary)
        {
            var button = new Button { Content = action.Label, IsEnabled = action.Enabled, MinHeight = 36 };
            if (primary && action.Enabled && action.Kind != StartActionKind.None)
            {
                try { button.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; }
                catch (Exception) { /* fall back to the default button style */ }
            }
            AutomationProperties.SetAutomationId(button, card.AutomationId + (primary ? "_Primary" : "_Secondary"));
            AutomationProperties.SetName(button, action.Label + " · " + card.Title);
            string id = card.Id;
            StartActionKind kind = action.Kind;
            button.Click += async (_, _) => await RunStartActionAsync(id, kind, button);
            return button;
        }

        // ---- Actions -------------------------------------------------------------------------------------------------

        private async Task RunStartActionAsync(string id, StartActionKind kind, Button button)
        {
            try
            {
                switch (kind)
                {
                    case StartActionKind.InstallEngine:
                        await InstallStartEngineAsync(id, button);
                        break;
                    case StartActionKind.CancelEngine:
                        button.IsEnabled = false;
                        await MediaSetupClient.Current.CancelAsync(id, CancellationToken.None);
                        await RefreshStartAsync();
                        break;
                    case StartActionKind.DownloadModel:
                        if (id == "chat")
                        {
                            if (_startTextOffer != null) await ChooseRecommendedFileAsync(_startTextOffer);
                        }
                        else if (_startMediaOffers.TryGetValue(id, out RecommendedCheckpoint? offer))
                        {
                            await DownloadMediaCheckpointAsync(offer, button);
                        }
                        await RefreshStartAsync();
                        break;
                    case StartActionKind.UseDownloadedModel:
                        if (_startMediaOffers.TryGetValue(id, out RecommendedCheckpoint? downloaded))
                            await InstallMediaCheckpointForAutomaticAsync(downloaded);
                        await RefreshStartAsync();
                        break;
                    case StartActionKind.OpenModels:
                        ShowHubTab(id == "chat" ? ModelHubTab.Text : ModelHubTab.Media);
                        break;
                    case StartActionKind.OpenRuntimeSettings:
                        // The chat engine is installed from the Runtime Manager, not from the general Settings page.
                        App.MainWin?.ShowRuntimeManager();
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _status.Text = (StartItalian ? "Operazione non riuscita: " : "That did not work: ")
                    + CustomCheckpointPresenter.ExtractServerError(ex.Message);
                button.IsEnabled = true;
            }
        }

        /// <summary>Engines are large downloads with their own licences, so the person confirms them first, with the sizes and
        /// licences in front of them. Cancelling changes nothing.</summary>
        private async Task InstallStartEngineAsync(string type, Button button)
        {
            bool it = StartItalian;
            _startEngineStatuses.TryGetValue(type, out MediaSetupStatus? status);
            MediaEngineDefinition? engine = MediaEngineHubPresenter.Engines.FirstOrDefault(e => e.Type == type);
            string name = StartEngineTitle(type, engine?.Title ?? type, it);
            long pending = status == null ? 0 : status.Steps.Where(s => s.State != "done").Sum(s => s.SizeBytes);
            string size = ModelHubStartPresenter.FormatSize(pending);
            string licence = status == null ? string.Empty : MediaEngineHubPresenter.LicenseLine(status);

            var body = new StackPanel { Spacing = 8, MaxWidth = 480 };
            body.Children.Add(new TextBlock
            {
                Text = it
                    ? name + " verrà installato sul tuo PC" + (size.Length > 0 ? " (circa " + size + " da scaricare)" : string.Empty)
                        + ". Funziona in locale: nulla viene inviato al cloud."
                    : name + " will be installed on your PC" + (size.Length > 0 ? " (about " + size + " to download)" : string.Empty)
                        + ". It runs locally: nothing is sent to the cloud.",
                TextWrapping = TextWrapping.Wrap
            });
            if (licence.Length > 0) body.Children.Add(new TextBlock { Text = licence, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 });
            body.Children.Add(new TextBlock
            {
                Text = it ? "Puoi annullare l'installazione in qualsiasi momento." : "You can cancel the installation at any time.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8
            });

            var dialog = new ContentDialog
            {
                Title = it ? "Installare il motore?" : "Install this engine?",
                Content = body,
                PrimaryButtonText = it ? "Installa" : "Install",
                CloseButtonText = it ? "Annulla" : "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            button.IsEnabled = false;
            await MediaSetupClient.Current.StartAsync(type, CancellationToken.None);
            await RefreshStartAsync();
        }
    }
}
