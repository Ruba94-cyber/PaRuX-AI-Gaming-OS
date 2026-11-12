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
        private readonly Button _applyRecommended = new() { Content = T("Use recommended settings", "Usa le impostazioni consigliate"), IsEnabled = false };

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
            Content = T("Pack short SFT samples (better token utilization)", "Raggruppa campioni SFT brevi (migliore uso dei token)")
        };
        private readonly NumberBox _grpoGenerations = new()
        {
            Value = 4, Minimum = 2, Maximum = 16,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 110
        };
        private readonly TextBox _textField = new() { Text = "text", Header = T("Text field", "Campo di testo"), MinWidth = 150 };
        private readonly TextBox _imageField = new() { Text = "image", Header = T("Image field", "Campo immagine"), MinWidth = 150 };
        private readonly TextBox _audioField = new() { Text = "audio", Header = T("Audio field", "Campo audio"), MinWidth = 150 };
        private readonly TextBlock _methodRequirements = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };

        private readonly Button _quickTrainButton = new()
        {
            Content = T("Quick train · 4-bit QLoRA", "Training rapido · QLoRA a 4 bit"),
            MinHeight = 42
        };
        private readonly Button _startTrainingButton = new() { Content = T("Start local training", "Avvia il training locale") };
        private bool _trainingSubmissionInProgress;
        private CancellationTokenSource? _quickPreflightCts;
        private TrainingPlanResult? _lastPlan;
        private string? _previousTrainingMethod;

        private UIElement BuildTrainingWizardControls()
        {
            EnsureAllTrainingMethods();
            EnsureGrpoRewards();
            EnsureTrainingVariants();
            _trainingPath.PlaceholderText = T("JSONL: messages | prompt/chosen/rejected | text | prompt/reference | image+messages | audio+text", "JSONL: messages | prompt/chosen/rejected | text | prompt/reference | image+messages | audio+text");

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = T("METHOD-SPECIFIC SETTINGS", "IMPOSTAZIONI SPECIFICHE DEL METODO"),
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
                Text = T("4-bit QLoRA uses less VRAM. For 16-bit LoRA, choose an unquantized Hugging Face model (not a *-bnb-4bit repository) and allow substantially more VRAM. Attention + MLP requires compatible projections in the base model.", "QLoRA a 4 bit usa meno VRAM. Per LoRA a 16 bit scegli un modello Hugging Face non quantizzato (non un repository *-bnb-4bit) e consenti molta più VRAM. Attenzione + MLP richiede proiezioni compatibili nel modello base."),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.72
            });
            panel.Children.Add(variants);

            var preference = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            preference.Children.Add(Caption("Preference beta"));
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
            var check = new Button { Content = T("Check this run", "Controlla questa esecuzione") };
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
            Add("KTO · binary feedback", TrainingMethod.Kto);
            Add("Continued pretraining", TrainingMethod.ContinuedPretraining);
            Add("GRPO", TrainingMethod.Grpo);
            Add("Vision SFT", TrainingMethod.VisionSft);
            Add("TTS SFT", TrainingMethod.TtsSft);
            if (_trainingMethod.SelectedIndex < 0) _trainingMethod.SelectedIndex = 0;
        }

        private void EnsureGrpoRewards()
        {
            if (_grpoReward.Items.Count > 0) return;
            _grpoReward.Items.Add(new ComboBoxItem { Content = T("Exact reference", "Riferimento esatto"), Tag = GrpoRewardMode.ExactReference });
            _grpoReward.Items.Add(new ComboBoxItem { Content = T("Contains reference", "Contiene il riferimento"), Tag = GrpoRewardMode.ContainsReference });
            _grpoReward.Items.Add(new ComboBoxItem { Content = T("XML format", "Formato XML"), Tag = GrpoRewardMode.XmlFormat });
            _grpoReward.Items.Add(new ComboBoxItem { Content = T("JSON format", "Formato JSON"), Tag = GrpoRewardMode.JsonFormat });
            _grpoReward.SelectedIndex = 0;
        }

        private void EnsureTrainingVariants()
        {
            if (_trainingPrecision.Items.Count == 0)
            {
                _trainingPrecision.Items.Add(new ComboBoxItem
                {
                    Content = T("QLoRA · 4-bit (less VRAM)", "QLoRA · 4 bit (meno VRAM)"), Tag = TrainingPrecision.Qlora4Bit
                });
                _trainingPrecision.Items.Add(new ComboBoxItem
                {
                    Content = T("LoRA · 16-bit", "LoRA · 16 bit"), Tag = TrainingPrecision.Lora16Bit
                });
                _trainingPrecision.SelectedIndex = 0;
            }
            if (_trainingTargets.Items.Count == 0)
            {
                _trainingTargets.Items.Add(new ComboBoxItem
                {
                    Content = T("Attention", "Attenzione"), Tag = TrainingTargetPreset.Attention
                });
                _trainingTargets.Items.Add(new ComboBoxItem
                {
                    Content = T("Attention + MLP", "Attenzione + MLP"), Tag = TrainingTargetPreset.AllLinear
                });
                _trainingTargets.SelectedIndex = 0;
            }
        }

        private void RefreshMethodControlsAndDraft()
        {
            string method = SelectedTag(_trainingMethod) ?? TrainingMethod.Sft;
            if (!string.Equals(method, _previousTrainingMethod, StringComparison.Ordinal))
            {
                // Do not inherit a much higher SFT default learning rate when the
                // user switches to KTO: TRL recommends roughly 1e-6 at beta 0.1.
                if (method == TrainingMethod.Kto)
                    _learningRate.Value = 0.000001;
                _previousTrainingMethod = method;
            }
            bool textOnly = method is not TrainingMethod.VisionSft and not TrainingMethod.TtsSft;
            if (!textOnly && SelectedTag(_trainingPrecision) == TrainingPrecision.Lora16Bit)
                _trainingPrecision.SelectedIndex = 0;
            if (!textOnly && SelectedTag(_trainingTargets) == TrainingTargetPreset.AllLinear)
                _trainingTargets.SelectedIndex = 0;
            _trainingPrecision.IsEnabled = textOnly;
            _trainingTargets.IsEnabled = textOnly;
            _trainingPacking.IsEnabled = method is TrainingMethod.Sft or TrainingMethod.ContinuedPretraining;
            if (!_trainingPacking.IsEnabled) _trainingPacking.IsChecked = false;
            _dpoBeta.IsEnabled = method is TrainingMethod.Dpo or TrainingMethod.Orpo or TrainingMethod.Kto;
            _grpoReward.IsEnabled = method == TrainingMethod.Grpo;
            _grpoGenerations.IsEnabled = method == TrainingMethod.Grpo;
            _textField.IsEnabled = method is TrainingMethod.ContinuedPretraining or TrainingMethod.TtsSft;
            _imageField.IsEnabled = method == TrainingMethod.VisionSft;
            _audioField.IsEnabled = method == TrainingMethod.TtsSft;

            _methodRequirements.Text = method switch
            {
                TrainingMethod.Dpo => T("DPO · every row needs prompt, chosen and rejected. Beta controls preference strength.", "DPO · ogni riga richiede prompt, chosen e rejected. Beta controlla l'intensità della preferenza."),
                TrainingMethod.Orpo => T("ORPO · prompt/chosen/rejected preference pairs; trains chosen answers and penalizes rejected ones without a separate reference model. Beta controls odds-ratio weight.", "ORPO · coppie di preferenza prompt/chosen/rejected; addestra le risposte scelte e penalizza quelle rifiutate senza un modello di riferimento separato. Beta controlla il peso del rapporto di odds."),
                TrainingMethod.Kto => T("KTO · each JSONL line needs prompt and completion text plus a real boolean label (true=desirable, false=undesirable). Include both classes and at least four examples. The GPU must fit four concurrent examples (effective batch 16). The default learning rate is 1e-6; beta controls feedback strength.", "KTO · ogni riga JSONL richiede il testo di prompt e completion più una vera etichetta booleana (true=desiderabile, false=indesiderabile). Includi entrambe le classi e almeno quattro esempi. La GPU deve contenere quattro esempi contemporanei (batch effettivo 16). Il learning rate predefinito è 1e-6; beta controlla l'intensità del feedback."),
                TrainingMethod.ContinuedPretraining => T("Continued pretraining · every row needs the selected text field. Use conservative learning rates for domain adaptation.", "Pre-addestramento continuato · ogni riga richiede il campo di testo selezionato. Usa learning rate prudenti per l'adattamento al dominio."),
                TrainingMethod.Grpo => T("GRPO · prompt is required. Reference reward modes also require reference; XML/JSON rewards are deterministic format checks and never execute dataset code.", "GRPO · il prompt è obbligatorio. Le modalità di reward con riferimento richiedono anche reference; i reward XML/JSON sono controlli di formato deterministici e non eseguono mai codice del dataset."),
                TrainingMethod.VisionSft => T("Vision SFT · each row needs the selected image field plus messages (or prompt/response); the base model must be vision-language capable.", "SFT visione · ogni riga richiede il campo immagine selezionato più messages (o prompt/response); il modello base deve supportare visione e linguaggio."),
                TrainingMethod.TtsSft => T("TTS SFT · each row needs the selected audio + text fields. The current real PaRuX trainer is SpeechT5-family only.", "SFT TTS · ogni riga richiede i campi audio + testo selezionati. L'attuale trainer reale di PaRuX supporta solo la famiglia SpeechT5."),
                _ => T("SFT · each row needs a messages[] conversation.", "SFT · ogni riga richiede una conversazione messages[].")
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
                    TextField = (_textField.Text ?? "text").Trim(),
                    ImageField = (_imageField.Text ?? "image").Trim(),
                    AudioField = (_audioField.Text ?? "audio").Trim()
                });
            }
            catch (InvalidDataException ex)
            {
                _planSummary.Text = T("Training settings need attention: ", "Le impostazioni di training richiedono attenzione: ") + ex.Message;
            }
        }

        private async Task CheckTrainingRunAsync()
        {
            string path = (_trainingPath.Text ?? string.Empty).Trim();
            string model = (_baseModel.Text ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                _planSummary.Text = T("Enter the training file first.", "Inserisci prima il file di training.");
                return;
            }

            _planFindings.Children.Clear();
            _planSummary.Text = T("Checking…", "Controllo in corso…");
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
                _planSummary.Text = T("Could not check this run: ", "Impossibile controllare questa esecuzione: ") + ex.Message;
            }
        }

        private async Task PickTrainingDatasetAsync()
        {
            if (App.MainWin == null)
            {
                _status.Text = T("Choose a JSONL dataset after the PaRuX window is ready.", "Scegli un dataset JSONL dopo che la finestra di PaRuX è pronta.");
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
                _planSummary.Text = T("Dataset selected. Quick train will inspect it before any training starts.", "Dataset selezionato. Il training rapido lo ispezionerà prima di avviare qualsiasi training.");
                _status.Text = T("Selected training dataset: ", "Dataset di training selezionato: ") + chosen.Name;
            }
            catch (Exception ex)
            {
                _status.Text = T("Unable to select a training dataset: ", "Impossibile selezionare un dataset di training: ") + ex.Message;
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
                _status.Text = T("Quick training: choose a JSONL dataset and a trainable base model first.", "Training rapido: scegli prima un dataset JSONL e un modello base addestrabile.");
                return;
            }

            _trainingSubmissionInProgress = true;
            var preflight = new CancellationTokenSource();
            _quickPreflightCts = preflight;
            _quickTrainButton.IsEnabled = false;
            _startTrainingButton.IsEnabled = false;
            try
            {
                _status.Text = T("Quick training: checking runtime, every dataset row and SHA-256 identity…", "Training rapido: controllo di runtime, ogni riga del dataset e identità SHA-256…");
                TrainingEngineStatus engine = await TrainingLabClient.Current
                    .GetEngineStatusAsync(preflight.Token);
                if (!engine.Ready)
                {
                    _status.Text = T("Quick training unavailable: ", "Training rapido non disponibile: ") + engine.Detail
                        + T(" Use Install/repair managed Training runtime above.", " Usa Installa/ripara il runtime di training gestito qui sopra.");
                    return;
                }

                // SFT first: do not infer or manufacture rejected answers to force DPO.
                TrainingPlanResult sample = await TrainingLabClient.Current.PlanAsync(new TrainingPlanRequest
                {
                    TrainJsonlPath = path, BaseModel = model, Method = TrainingMethod.Sft,
                    LoraRank = 16, MaxSteps = 60, LearningRate = 0.0002,
                    MaxSequenceLength = 2048
                }, preflight.Token);
                string? method = QuickTrainingPolicy.ChooseMethod(
                    sample.DatasetSupportsSupervised, sample.DatasetSupportsPreference);
                if (method == null)
                {
                    _lastPlan = sample;
                    RenderPlan(sample);
                    _status.Text = T("Quick training requires valid SFT messages[] or genuine DPO preference pairs. ", "Il training rapido richiede messages[] SFT validi o vere coppie di preferenza DPO. ")
                        + T("For other dataset types, expand Advanced training.", "Per altri tipi di dataset, espandi Training avanzato.");
                    return;
                }
                if (!engine.AvailableMethods.Contains(method, StringComparer.Ordinal))
                {
                    _status.Text = T("Quick training: this managed runtime does not support ", "Training rapido: questo runtime gestito non supporta ")
                        + method.ToUpperInvariant() + T(" on the detected hardware. ", " sull'hardware rilevato. ") + engine.Detail;
                    return;
                }

                if (method == TrainingMethod.Dpo)
                    sample = await TrainingLabClient.Current.PlanAsync(new TrainingPlanRequest
                    {
                        TrainJsonlPath = path, BaseModel = model, Method = TrainingMethod.Dpo,
                        LoraRank = 16, MaxSteps = 60, LearningRate = 5e-6,
                        MaxSequenceLength = 2048
                    }, preflight.Token);

                var profile = QuickTrainingPolicy.Create(method,
                    sample.RecommendedLoraRank, sample.RecommendedMaxSteps,
                    sample.RecommendedLearningRate, sample.RecommendedMaxSequenceLength);

                // Check the ACTUAL capped job settings, not merely the recommendation.
                TrainingPlanResult checkedRun = await TrainingLabClient.Current.PlanAsync(new TrainingPlanRequest
                {
                    TrainJsonlPath = path, BaseModel = model, Method = profile.Method,
                    LoraRank = profile.LoraRank, MaxSteps = profile.MaxSteps,
                    LearningRate = profile.LearningRate, MaxSequenceLength = profile.MaxSequenceLength,
                    ValidateEntireDataset = true
                }, preflight.Token);
                _lastPlan = checkedRun;
                RenderPlan(checkedRun);
                string? refusal = QuickTrainingPolicy.RefusalAfterFullVerification(
                    checkedRun.CanStart, checkedRun.DatasetFullyValidated,
                    checkedRun.DatasetVerifiedRecords, checkedRun.DatasetSha256);
                if (refusal != null)
                {
                    _status.Text = T("Quick training blocked: ", "Training rapido bloccato: ") + refusal;
                    return;
                }

                var warnings = checkedRun.Findings
                    .Where(finding => string.Equals(finding.Severity, "Warning", StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .Select(finding => "• " + finding.Message)
                    .ToArray();
                // Every Quick run requires an explicit final consent after checking
                // ALL records. The original quick-button click only starts preflight.
                {
                    var confirmation = new ContentDialog
                    {
                        Title = T("Confirm local Quick Training", "Conferma il training rapido locale"),
                        Content = new TextBlock
                        {
                            Text = T("Method ", "Metodo ") + profile.Method.ToUpperInvariant()
                                + T(" · rank ", " · rango ") + profile.LoraRank
                                + " · " + profile.MaxSteps + T(" steps", " passaggi")
                                + T(" · sequence ", " · sequenza ") + profile.MaxSequenceLength
                                + T("\nVerified ", "\nVerificati ") + checkedRun.DatasetVerifiedRecords.ToString("N0")
                                + T(" records · SHA-256 ", " record · SHA-256 ") + checkedRun.DatasetSha256![..12] + "…"
                                + (warnings.Length > 0 ? T("\n\nWarnings:\n", "\n\nAvvisi:\n") + string.Join("\n", warnings) : "")
                                + T("\n\nStart this local GPU job?", "\n\nAvviare questo job GPU locale?"),
                            TextWrapping = TextWrapping.Wrap
                        },
                        PrimaryButtonText = T("Start local training", "Avvia il training locale"),
                        CloseButtonText = T("Cancel", "Annulla"),
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot
                    };
                    if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
                    {
                        _status.Text = T("Quick training cancelled. No training job was submitted.", "Training rapido annullato. Nessun job di training è stato inviato.");
                        return;
                    }
                }

                // Bypass the shared Advanced-draft override: the request sent must match the
                // exact settings approved by the preflight and any warning confirmation.
                preflight.Token.ThrowIfCancellationRequested();
                TrainingJobStatus job = await TrainingLabClient.Current.CreateJobExplicitAsync(
                    new TrainingJobRequest
                    {
                        TrainJsonlPath = path, BaseModel = model, Method = profile.Method,
                        ExpectedDatasetSha256 = checkedRun.DatasetSha256,
                        LoraRank = profile.LoraRank, MaxSteps = profile.MaxSteps,
                        LearningRate = profile.LearningRate, MaxSequenceLength = profile.MaxSequenceLength
                    }, CancellationToken.None);
                _status.Text = T("Quick ", "Rapido ") + profile.Method.ToUpperInvariant() + T(" job ", " job ") + job.Id
                    + T(" queued with ", " messo in coda con ") + profile.MaxSteps + T(" steps and rank ", " passaggi e rango ")
                    + profile.LoraRank + T(". Track progress below; the completed adapter is registered locally.", ". Segui l'avanzamento qui sotto; l'adapter completato viene registrato in locale.");
                await RefreshTrainingAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                _status.Text = T("Quick dataset verification was cancelled; no new GPU job was requested.", "La verifica rapida del dataset è stata annullata; nessun nuovo job GPU è stato richiesto.");
            }
            catch (Exception ex)
            {
                _status.Text = T("Quick training failed before or during submission: ", "Training rapido non riuscito prima o durante l'invio: ") + ex.Message;
            }
            finally
            {
                if (ReferenceEquals(_quickPreflightCts, preflight)) _quickPreflightCts = null;
                preflight.Dispose();
                _trainingSubmissionInProgress = false;
                _quickTrainButton.IsEnabled = true;
                _startTrainingButton.IsEnabled = true;
            }
        }

        private void RenderPlan(TrainingPlanResult plan)
        {
            _planSummary.Text = (plan.CanStart ? T("Ready to start. ", "Pronto per partire. ") : T("This run cannot succeed as configured. ", "Questa esecuzione non può riuscire così com'è configurata. "))
                + plan.DatasetSummary
                + (plan.DetectedMethod == TrainingMethod.Kto
                    ? T($" KTO feedback: {plan.DatasetValidKtoRecords:N0} labelled,", $" Feedback KTO: {plan.DatasetValidKtoRecords:N0} etichettati,")
                      + T($" {plan.DatasetDesirableKtoRecords:N0} desirable,", $" {plan.DatasetDesirableKtoRecords:N0} desiderabili,")
                      + T($" {plan.DatasetUndesirableKtoRecords:N0} undesirable.", $" {plan.DatasetUndesirableKtoRecords:N0} indesiderabili.")
                    : string.Empty)
                + (plan.DatasetSampleTruncated
                    ? T(" (Preflight samples only the first 2,000 records; KTO/ORPO perform full-row checks before GPU admission.)", " (Il controllo preliminare campiona solo i primi 2.000 record; KTO/ORPO eseguono controlli su tutte le righe prima dell'ammissione alla GPU.)")
                    : string.Empty)
                + (plan.EstimatedGib > 0
                    ? T($" Needs roughly {plan.EstimatedGib:0.#} GB against {plan.AvailableGib:0.#} GB available.", $" Servono circa {plan.EstimatedGib:0.#} GB a fronte di {plan.AvailableGib:0.#} GB disponibili.")
                    : string.Empty);

            int recommendedSequence = plan.RecommendedMaxSequenceLength > 0
                ? plan.RecommendedMaxSequenceLength
                : RecommendedSequenceFor(plan.DetectedMethod);
            _planRecommendation.Text = plan.RecommendedMaxSteps > 0
                ? T($"Suggested: rank {plan.RecommendedLoraRank}, {plan.RecommendedMaxSteps:N0} steps, ", $"Suggerito: rango {plan.RecommendedLoraRank}, {plan.RecommendedMaxSteps:N0} passaggi, ")
                    + T($"learning rate {plan.RecommendedLearningRate:G}, max sequence {recommendedSequence:N0}. ", $"learning rate {plan.RecommendedLearningRate:G}, sequenza massima {recommendedSequence:N0}. ")
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
                    Text = (blocking ? T("Must fix · ", "Da correggere · ") : T("Worth knowing · ", "Da sapere · ")) + finding.Field + ": " + finding.Message,
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
            _planSummary.Text = T("Applied the suggested settings. Check the run again to confirm.", "Impostazioni suggerite applicate. Controlla di nuovo l'esecuzione per confermare.");
        }

        private static int RecommendedSequenceFor(string? method) => method switch
        {
            TrainingMethod.ContinuedPretraining => 4096,
            TrainingMethod.VisionSft => 2048,
            TrainingMethod.TtsSft => 1024,
            TrainingMethod.Grpo => 2048,
            TrainingMethod.Dpo => 2048,
            TrainingMethod.Kto => 2048,
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
