using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// The guided half of Training Lab: check the run before committing to it, expose every
    /// PaRuX-managed training method and keep method-specific parameters attached to the same Core
    /// job boundary used by the historical Lifecycle Lab start button.
    ///
    /// A training run loads a model, allocates VRAM and trains for minutes or hours. Every way it
    /// fails for a knowable reason - file shape, model family, memory fit, invalid GRPO reward mode,
    /// media paths or sequence budget - should therefore be decided before allocating the GPU.
    /// Blocking findings and warnings remain intentionally distinct.
    /// </summary>
    public sealed partial class LifecycleLabView
    {
        private readonly StackPanel _planFindings = new() { Spacing = 4 };
        private readonly TextBlock _planSummary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        private readonly TextBlock _planRecommendation = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        private readonly Button _applyRecommended = new() { Content = "Use recommended settings", IsEnabled = false };

        private readonly NumberBox _learningRate = new()
        {
            Value = 0.0002, Minimum = 0.00000001, Maximum = 0.01,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 150
        };
        private readonly NumberBox _maxSequenceLength = new()
        {
            Value = 2048, Minimum = 128, Maximum = 1_048_576,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 145
        };
        private readonly NumberBox _dpoBeta = new()
        {
            Value = 0.1, Minimum = 0.000001, Maximum = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 120
        };
        private readonly ComboBox _grpoReward = new() { MinWidth = 190 };
        private readonly ComboBox _trainingPrecision = new() { MinWidth = 155 };
        private readonly ComboBox _trainingTargets = new() { MinWidth = 160 };
        private readonly CheckBox _trainingPacking = new()
        {
            Content = "Pack short SFT samples (better token utilization)"
        };
        private readonly NumberBox _grpoGenerations = new()
        {
            Value = 4, Minimum = 2, Maximum = 16,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 110
        };
        private readonly TextBox _textField = new() { Text = "text", Header = "Text field", MinWidth = 150 };
        private readonly TextBox _imageField = new() { Text = "image", Header = "Image field", MinWidth = 150 };
        private readonly TextBox _audioField = new() { Text = "audio", Header = "Audio field", MinWidth = 150 };
        private readonly TextBlock _methodRequirements = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };

        private readonly Button _quickTrainButton = new()
        {
            Content = "Quick train · 4-bit QLoRA",
            MinHeight = 42
        };
        private readonly Button _startTrainingButton = new() { Content = "Start local training" };
        private bool _trainingSubmissionInProgress;
        private TrainingPlanResult? _lastPlan;

        private UIElement BuildTrainingWizardControls()
        {
            EnsureAllTrainingMethods();
            EnsureGrpoRewards();
            EnsureTrainingVariants();
            _trainingPath.PlaceholderText = "JSONL: messages | prompt/chosen/rejected | text | prompt/reference | image+messages | audio+text";

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = "METHOD-SPECIFIC SETTINGS",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.72
            });

            var general = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            general.Children.Add(Caption("Learning rate"));
            general.Children.Add(_learningRate);
            general.Children.Add(Caption("Max sequence"));
            general.Children.Add(_maxSequenceLength);
            panel.Children.Add(general);

            var variants = new StackPanel { Orientation = Orientation.Vertical, Spacing = 8 };
            var precisionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            precisionRow.Children.Add(Caption("Base precision"));
            precisionRow.Children.Add(_trainingPrecision);
            precisionRow.Children.Add(Caption("LoRA layers"));
            precisionRow.Children.Add(_trainingTargets);
            variants.Children.Add(precisionRow);
            variants.Children.Add(_trainingPacking);
            variants.Children.Add(new TextBlock
            {
                Text = "4-bit QLoRA uses less GPU memory. 16-bit LoRA loads full-precision base weights and requires significantly more VRAM. All-linear adds MLP adapters when the model supports those projection names.",
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.72
            });
            panel.Children.Add(variants);

            var preference = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            preference.Children.Add(Caption("DPO/ORPO beta"));
            preference.Children.Add(_dpoBeta);
            preference.Children.Add(Caption("GRPO reward"));
            preference.Children.Add(_grpoReward);
            preference.Children.Add(Caption("Generations"));
            preference.Children.Add(_grpoGenerations);
            panel.Children.Add(preference);

            var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            fields.Children.Add(_textField);
            fields.Children.Add(_imageField);
            fields.Children.Add(_audioField);
            panel.Children.Add(fields);
            panel.Children.Add(_methodRequirements);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var check = new Button { Content = "Check this run" };
            Id(check, "LifecycleLab_CheckRun_Button");
            check.Click += async (_, _) => await CheckTrainingRunAsync();
            actions.Children.Add(check);

            _applyRecommended.Click += (_, _) => ApplyRecommendedSettings();
            actions.Children.Add(_applyRecommended);
            panel.Children.Add(actions);

            panel.Children.Add(_planSummary);
            panel.Children.Add(_planRecommendation);
            panel.Children.Add(_planFindings);

            _trainingMethod.SelectionChanged += (_, _) => RefreshMethodControlsAndDraft();
            _trainingPrecision.SelectionChanged += (_, _) => SyncInteractiveDraft();
            _trainingTargets.SelectionChanged += (_, _) => SyncInteractiveDraft();
            _trainingPacking.Checked += (_, _) => SyncInteractiveDraft();
            _trainingPacking.Unchecked += (_, _) => SyncInteractiveDraft();
            _learningRate.ValueChanged += (_, _) => SyncInteractiveDraft();
            _maxSequenceLength.ValueChanged += (_, _) => SyncInteractiveDraft();
            _dpoBeta.ValueChanged += (_, _) => SyncInteractiveDraft();
            _grpoReward.SelectionChanged += (_, _) => SyncInteractiveDraft();
            _grpoGenerations.ValueChanged += (_, _) => SyncInteractiveDraft();
            _textField.TextChanged += (_, _) => SyncInteractiveDraft();
            _imageField.TextChanged += (_, _) => SyncInteractiveDraft();
            _audioField.TextChanged += (_, _) => SyncInteractiveDraft();

            RefreshMethodControlsAndDraft();
            return panel;
        }

        private static TextBlock Caption(string text) => new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.78
        };

        private void EnsureAllTrainingMethods()
        {
            var existing = _trainingMethod.Items
                .OfType<ComboBoxItem>()
                .Select(item => item.Tag?.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal);

            void Add(string title, string tag)
            {
                if (!existing.Contains(tag))
                    _trainingMethod.Items.Add(new ComboBoxItem { Content = title, Tag = tag });
            }

            Add("SFT", TrainingMethod.Sft);
            Add("DPO", TrainingMethod.Dpo);
            Add("ORPO", TrainingMethod.Orpo);
            Add("Continued pretraining", TrainingMethod.ContinuedPretraining);
            Add("GRPO", TrainingMethod.Grpo);
            Add("Vision SFT", TrainingMethod.VisionSft);
            Add("TTS SFT", TrainingMethod.TtsSft);
            if (_trainingMethod.SelectedIndex < 0) _trainingMethod.SelectedIndex = 0;
        }

        private void EnsureGrpoRewards()
        {
            if (_grpoReward.Items.Count > 0) return;
            _grpoReward.Items.Add(new ComboBoxItem { Content = "Exact reference", Tag = GrpoRewardMode.ExactReference });
            _grpoReward.Items.Add(new ComboBoxItem { Content = "Contains reference", Tag = GrpoRewardMode.ContainsReference });
            _grpoReward.Items.Add(new ComboBoxItem { Content = "XML format", Tag = GrpoRewardMode.XmlFormat });
            _grpoReward.Items.Add(new ComboBoxItem { Content = "JSON format", Tag = GrpoRewardMode.JsonFormat });
            _grpoReward.SelectedIndex = 0;
        }

        private void EnsureTrainingVariants()
        {
            if (_trainingPrecision.Items.Count == 0)
            {
                _trainingPrecision.Items.Add(new ComboBoxItem
                {
                    Content = "QLoRA · 4-bit (less VRAM)", Tag = TrainingPrecision.Qlora4Bit
                });
                _trainingPrecision.Items.Add(new ComboBoxItem
                {
                    Content = "LoRA · 16-bit", Tag = TrainingPrecision.Lora16Bit
                });
                _trainingPrecision.SelectedIndex = 0;
            }
            if (_trainingTargets.Items.Count == 0)
            {
                _trainingTargets.Items.Add(new ComboBoxItem
                {
                    Content = "Attention", Tag = TrainingTargetPreset.Attention
                });
                _trainingTargets.Items.Add(new ComboBoxItem
                {
                    Content = "Attention + MLP", Tag = TrainingTargetPreset.AllLinear
                });
                _trainingTargets.SelectedIndex = 0;
            }
        }

        private void RefreshMethodControlsAndDraft()
        {
            string method = SelectedTag(_trainingMethod) ?? TrainingMethod.Sft;
            bool textOnly = method is not TrainingMethod.VisionSft and not TrainingMethod.TtsSft;
            if (!textOnly && SelectedTag(_trainingPrecision) == TrainingPrecision.Lora16Bit)
                _trainingPrecision.SelectedIndex = 0;
            if (!textOnly && SelectedTag(_trainingTargets) == TrainingTargetPreset.AllLinear)
                _trainingTargets.SelectedIndex = 0;
            _trainingPrecision.IsEnabled = textOnly;
            _trainingTargets.IsEnabled = textOnly;
            _trainingPacking.IsEnabled = method is TrainingMethod.Sft or TrainingMethod.ContinuedPretraining;
            if (!_trainingPacking.IsEnabled) _trainingPacking.IsChecked = false;
            _dpoBeta.IsEnabled = method is TrainingMethod.Dpo or TrainingMethod.Orpo;
            _grpoReward.IsEnabled = method == TrainingMethod.Grpo;
            _grpoGenerations.IsEnabled = method == TrainingMethod.Grpo;
            _textField.IsEnabled = method is TrainingMethod.ContinuedPretraining or TrainingMethod.TtsSft;
            _imageField.IsEnabled = method == TrainingMethod.VisionSft;
            _audioField.IsEnabled = method == TrainingMethod.TtsSft;

            _methodRequirements.Text = method switch
            {
                TrainingMethod.Dpo => "DPO · every row needs prompt, chosen and rejected. Beta controls preference strength.",
                TrainingMethod.Orpo => "ORPO · prompt/chosen/rejected preference pairs; trains chosen answers and penalizes rejected ones without a separate reference model. Beta controls odds-ratio weight.",
                TrainingMethod.ContinuedPretraining => "Continued pretraining · every row needs the selected text field. Use conservative learning rates for domain adaptation.",
                TrainingMethod.Grpo => "GRPO · prompt is required. Reference reward modes also require reference; XML/JSON rewards are deterministic format checks and never execute dataset code.",
                TrainingMethod.VisionSft => "Vision SFT · each row needs the selected image field plus messages (or prompt/response); the base model must be vision-language capable.",
                TrainingMethod.TtsSft => "TTS SFT · each row needs the selected audio + text fields. The current real PaRuX trainer is SpeechT5-family only.",
                _ => "SFT · each row needs a messages[] conversation."
            };
            SyncInteractiveDraft();
        }

        private void SyncInteractiveDraft()
        {
            try
            {
                TrainingLabClient.Current.SetInteractiveOptions(new TrainingInteractiveOptions
                {
                    Method = SelectedTag(_trainingMethod) ?? TrainingMethod.Sft,
                    LearningRate = SafeDouble(_learningRate.Value, 0.0002),
                    DpoBeta = SafeDouble(_dpoBeta.Value, 0.1),
                    MaxSequenceLength = SafeWholeNumber(_maxSequenceLength.Value, 2048),
                    Precision = SelectedTag(_trainingPrecision) ?? TrainingPrecision.Qlora4Bit,
                    TargetPreset = SelectedTag(_trainingTargets) ?? TrainingTargetPreset.Attention,
                    PackSequences = _trainingPacking.IsChecked == true,
                    GrpoRewardMode = SelectedTag(_grpoReward) ?? GrpoRewardMode.ExactReference,
                    GrpoGenerations = SafeWholeNumber(_grpoGenerations.Value, 4),
                    Precision = SelectedTag(_trainingPrecision) ?? TrainingPrecision.Qlora4Bit,
                    TargetPreset = SelectedTag(_trainingTargets) ?? TrainingTargetPreset.Attention,
                    PackSequences = _trainingPacking.IsChecked == true,
                    TextField = (_textField.Text ?? "text").Trim(),
                    ImageField = (_imageField.Text ?? "image").Trim(),
                    AudioField = (_audioField.Text ?? "audio").Trim()
                });
            }
            catch (InvalidDataException ex)
            {
                _planSummary.Text = "Training settings need attention: " + ex.Message;
            }
        }

        private async Task CheckTrainingRunAsync()
        {
            string path = (_trainingPath.Text ?? string.Empty).Trim();
            string model = (_baseModel.Text ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                _planSummary.Text = "Enter the training file first.";
                return;
            }

            _planFindings.Children.Clear();
            _planSummary.Text = "Checking…";
            _planRecommendation.Text = string.Empty;
            _applyRecommended.IsEnabled = false;

            try
            {
                SyncInteractiveDraft();
                TrainingPlanResult plan = await TrainingLabClient.Current.PlanAsync(new TrainingPlanRequest
                {
                    TrainJsonlPath = path,
                    BaseModel = model,
                    Method = SelectedTag(_trainingMethod) ?? TrainingMethod.Sft,
                    MaxSteps = SafeWholeNumber(_steps.Value, 60),
                    LoraRank = SafeWholeNumber(_rank.Value, 16),
                    LearningRate = SafeDouble(_learningRate.Value, 0.0002),
                    MaxSequenceLength = SafeWholeNumber(_maxSequenceLength.Value, 2048),
                    GrpoRewardMode = SelectedTag(_grpoReward) ?? GrpoRewardMode.ExactReference
                }, CancellationToken.None);

                _lastPlan = plan;
                RenderPlan(plan);
            }
            catch (Exception ex)
            {
                _planSummary.Text = "Could not check this run: " + ex.Message;
            }
        }

        private async Task PickTrainingDatasetAsync()
        {
            if (App.MainWin == null)
            {
                _status.Text = "Choose a JSONL dataset after the PaRuX window is ready.";
                return;
            }
            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    ViewMode = PickerViewMode.List
                };
                picker.FileTypeFilter.Add(".jsonl");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile? chosen = await picker.PickSingleFileAsync();
                if (chosen == null) return;
                if (!File.Exists(chosen.Path))
                    throw new FileNotFoundException("The selected JSONL file is not accessible.", chosen.Path);
                _trainingPath.Text = chosen.Path;
                _lastPlan = null;
                _planSummary.Text = "Dataset selected. Quick train will inspect it before any training starts.";
                _status.Text = "Selected training dataset: " + chosen.Name;
            }
            catch (Exception ex)
            {
                _status.Text = "Unable to select a training dataset: " + ex.Message;
            }
        }

        /// <summary>
        /// Real, owner-triggered quick fine-tune. The Core's planner and runtime admission
        /// are authoritative. This is not a second training engine and never installs
        /// dependencies or starts GPU work when the page opens.
        /// </summary>
        private async Task StartQuickTrainingAsync()
        {
            if (_trainingSubmissionInProgress) return;
            string path = (_trainingPath.Text ?? string.Empty).Trim();
            string model = (_baseModel.Text ?? string.Empty).Trim();
            if (path.Length == 0 || model.Length == 0)
            {
                _status.Text = "Quick training: choose a JSONL dataset and a trainable base model first.";
                return;
            }

            _trainingSubmissionInProgress = true;
            _quickTrainButton.IsEnabled = false;
            _startTrainingButton.IsEnabled = false;
            try
            {
                _status.Text = "Quick training: checking the local training runtime and dataset…";
                TrainingEngineStatus engine = await TrainingLabClient.Current
                    .GetEngineStatusAsync(CancellationToken.None);
                if (!engine.Ready)
                {
                    _status.Text = "Quick training unavailable: " + engine.Detail
                        + " Use Install/repair managed Training runtime above.";
                    return;
                }

                // SFT first: do not infer or manufacture rejected answers to force DPO.
                TrainingPlanResult sample = await TrainingLabClient.Current.PlanAsync(new TrainingPlanRequest
                {
                    TrainJsonlPath = path, BaseModel = model, Method = TrainingMethod.Sft,
                    LoraRank = 16, MaxSteps = 60, LearningRate = 0.0002,
                    MaxSequenceLength = 2048
                }, CancellationToken.None);
                string? method = QuickTrainingPolicy.ChooseMethod(
                    sample.DatasetSupportsSupervised, sample.DatasetSupportsPreference);
                if (method == null)
                {
                    _lastPlan = sample;
                    RenderPlan(sample);
                    _status.Text = "Quick training requires valid SFT messages[] or genuine DPO preference pairs. "
                        + "For other dataset types, expand Advanced training.";
                    return;
                }
                if (!engine.AvailableMethods.Contains(method, StringComparer.Ordinal))
                {
                    _status.Text = "Quick training: this managed runtime does not support "
                        + method.ToUpperInvariant() + " on the detected hardware. " + engine.Detail;
                    return;
                }

                if (method == TrainingMethod.Dpo)
                    sample = await TrainingLabClient.Current.PlanAsync(new TrainingPlanRequest
                    {
                        TrainJsonlPath = path, BaseModel = model, Method = TrainingMethod.Dpo,
                        LoraRank = 16, MaxSteps = 60, LearningRate = 5e-6,
                        MaxSequenceLength = 2048
                    }, CancellationToken.None);

                var profile = QuickTrainingPolicy.Create(method,
                    sample.RecommendedLoraRank, sample.RecommendedMaxSteps,
                    sample.RecommendedLearningRate, sample.RecommendedMaxSequenceLength);

                // Check the ACTUAL capped job settings, not merely the recommendation.
                TrainingPlanResult checkedRun = await TrainingLabClient.Current.PlanAsync(new TrainingPlanRequest
                {
                    TrainJsonlPath = path, BaseModel = model, Method = profile.Method,
                    LoraRank = profile.LoraRank, MaxSteps = profile.MaxSteps,
                    LearningRate = profile.LearningRate, MaxSequenceLength = profile.MaxSequenceLength
                }, CancellationToken.None);
                _lastPlan = checkedRun;
                RenderPlan(checkedRun);
                int validMethodRecords = profile.Method == TrainingMethod.Dpo
                    ? checkedRun.DatasetValidPreferenceRecords
                    : checkedRun.DatasetValidChatRecords;
                string? refusal = QuickTrainingPolicy.Refusal(
                    checkedRun.CanStart, checkedRun.DatasetExamples, validMethodRecords,
                    checkedRun.DatasetUnparseableLines, checkedRun.DatasetSampleTruncated);
                if (refusal != null)
                {
                    _status.Text = "Quick training blocked: " + refusal;
                    return;
                }

                var warnings = checkedRun.Findings
                    .Where(finding => string.Equals(finding.Severity, "Warning", StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .Select(finding => "• " + finding.Message)
                    .ToArray();
                if (warnings.Length > 0)
                {
                    var confirmation = new ContentDialog
                    {
                        Title = "Review quick fine-tune warnings",
                        Content = new TextBlock
                        {
                            Text = "Method " + profile.Method.ToUpperInvariant()
                                + " · rank " + profile.LoraRank
                                + " · " + profile.MaxSteps + " steps"
                                + " · sequence " + profile.MaxSequenceLength
                                + "\n\n" + string.Join("\n", warnings)
                                + "\n\nStart this local GPU job anyway?",
                            TextWrapping = TextWrapping.Wrap
                        },
                        PrimaryButtonText = "Start local training",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot
                    };
                    if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
                    {
                        _status.Text = "Quick training cancelled. No training job was submitted.";
                        return;
                    }
                }

                // Bypass the shared Advanced-draft override: the request sent must match the
                // exact settings approved by the preflight and any warning confirmation.
                TrainingJobStatus job = await TrainingLabClient.Current.CreateJobExplicitAsync(
                    new TrainingJobRequest
                    {
                        TrainJsonlPath = path, BaseModel = model, Method = profile.Method,
                        LoraRank = profile.LoraRank, MaxSteps = profile.MaxSteps,
                        LearningRate = profile.LearningRate, MaxSequenceLength = profile.MaxSequenceLength
                    }, CancellationToken.None);
                _status.Text = "Quick " + profile.Method.ToUpperInvariant() + " job " + job.Id
                    + " queued with " + profile.MaxSteps + " steps and rank "
                    + profile.LoraRank + ". Track progress below; the completed adapter is registered locally.";
                await RefreshTrainingAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _status.Text = "Quick training failed before or during submission: " + ex.Message;
            }
            finally
            {
                _trainingSubmissionInProgress = false;
                _quickTrainButton.IsEnabled = true;
                _startTrainingButton.IsEnabled = true;
            }
        }

        private void RenderPlan(TrainingPlanResult plan)
        {
            _planSummary.Text = (plan.CanStart ? "Ready to start. " : "This run cannot succeed as configured. ")
                + plan.DatasetSummary
                + (plan.DatasetSampleTruncated
                    ? " (Only the bounded shape sample was read; the runner still validates the real file.)"
                    : string.Empty)
                + (plan.EstimatedGib > 0
                    ? $" Needs roughly {plan.EstimatedGib:0.#} GB against {plan.AvailableGib:0.#} GB available."
                    : string.Empty);

            int recommendedSequence = plan.RecommendedMaxSequenceLength > 0
                ? plan.RecommendedMaxSequenceLength
                : RecommendedSequenceFor(plan.DetectedMethod);
            _planRecommendation.Text = plan.RecommendedMaxSteps > 0
                ? $"Suggested: rank {plan.RecommendedLoraRank}, {plan.RecommendedMaxSteps:N0} steps, "
                    + $"learning rate {plan.RecommendedLearningRate:G}, max sequence {recommendedSequence:N0}. "
                    + plan.RecommendationRationale
                : string.Empty;
            _applyRecommended.IsEnabled = plan.RecommendedMaxSteps > 0;

            _planFindings.Children.Clear();
            foreach (TrainingPlanFinding finding in plan.Findings
                .OrderBy(item => item.Severity == "Blocking" ? 0 : 1))
            {
                bool blocking = finding.Severity == "Blocking";
                _planFindings.Children.Add(new TextBlock
                {
                    Text = (blocking ? "Must fix · " : "Worth knowing · ") + finding.Field + ": " + finding.Message,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = blocking
                        ? Microsoft.UI.Text.FontWeights.SemiBold
                        : Microsoft.UI.Text.FontWeights.Normal,
                    Opacity = blocking ? 1.0 : 0.75
                });
            }
        }

        private void ApplyRecommendedSettings()
        {
            if (_lastPlan == null) return;
            _steps.Value = _lastPlan.RecommendedMaxSteps;
            _rank.Value = _lastPlan.RecommendedLoraRank;
            if (_lastPlan.RecommendedLearningRate > 0) _learningRate.Value = _lastPlan.RecommendedLearningRate;
            _maxSequenceLength.Value = _lastPlan.RecommendedMaxSequenceLength > 0
                ? _lastPlan.RecommendedMaxSequenceLength
                : RecommendedSequenceFor(_lastPlan.DetectedMethod);
            SyncInteractiveDraft();
            _planSummary.Text = "Applied the suggested settings. Check the run again to confirm.";
        }

        private static int RecommendedSequenceFor(string? method) => method switch
        {
            TrainingMethod.ContinuedPretraining => 4096,
            TrainingMethod.VisionSft => 2048,
            TrainingMethod.TtsSft => 1024,
            TrainingMethod.Grpo => 2048,
            TrainingMethod.Dpo => 2048,
            _ => 2048
        };

        private static double SafeDouble(double value, double fallback) =>
            double.IsFinite(value) && value > 0 ? value : fallback;

        /// <summary>The live numbers a run is judged by. Loss alone is not enough: a gradient norm
        /// spiking is divergence and shows there before the loss does, and a learning rate at zero
        /// explains a loss that has stopped moving without anything being wrong.</summary>
        private static string DescribeTrainingMetrics(TrainingJobStatus job)
        {
            var parts = new List<string>();
            if (job.LastLoss.HasValue) parts.Add("loss " + job.LastLoss.Value.ToString("0.####"));
            if (job.LastGradientNorm.HasValue) parts.Add("grad " + job.LastGradientNorm.Value.ToString("0.###"));
            if (job.LastLearningRate.HasValue) parts.Add("lr " + job.LastLearningRate.Value.ToString("G3"));
            if (job.LastEpoch.HasValue) parts.Add("epoch " + job.LastEpoch.Value.ToString("0.##"));
            return parts.Count == 0 ? string.Empty : " · " + string.Join(" · ", parts);
        }
    }
}
