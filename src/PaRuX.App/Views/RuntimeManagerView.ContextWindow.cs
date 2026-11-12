using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// Controls the PHYSICAL attention window used when a model is launched. PaRuX Virtual Context
    /// is separate and automatic: conversations/projects may grow far beyond this number because
    /// older state is hierarchically compacted and task-relevant history is recalled back into the
    /// finite physical window. This partial also exposes the Core-owned Execution Profile and
    /// declarative Plugin Bundle surfaces beside runtime controls so policy is visible where model
    /// resources are configured instead of remaining an API-only capability.
    /// </summary>
    public sealed partial class RuntimeManagerView
    {
        private readonly ComboBox _contextLength = new ComboBox { MinWidth = 260 };
        private readonly TextBlock _contextSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
        private readonly TextBlock _contextCost = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.68 };
        private readonly CheckBox _allowBeyondTrained = new CheckBox
        {
            Content = T("Allow physical context beyond this model's trained window (experimental / lower far-context quality)", "Consenti una finestra di contesto fisica oltre quella di addestramento del modello (sperimentale / minore qualità sul contesto lontano)"),
            IsChecked = false
        };

        private readonly ComboBox _executionQuality = new ComboBox { MinWidth = 190 };
        private readonly TextBlock _executionProfileSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };

        private readonly TextBox _pluginBundlePath = new TextBox
        {
            PlaceholderText = T("Select a .parux-plugin bundle to inspect", "Seleziona un bundle .parux-plugin da ispezionare"),
            MinWidth = 430
        };
        private readonly TextBlock _pluginInspectionSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
        private readonly StackPanel _trustedPublishers = new StackPanel { Spacing = 6 };
        private readonly CheckBox _pluginAllowUnsigned = new CheckBox
        {
            Content = T("Allow unsigned bundle for this install (explicit unsafe exception)", "Consenti il bundle non firmato per questa installazione (eccezione esplicita non sicura)"),
            IsChecked = false
        };
        private readonly CheckBox _pluginTrustPublisher = new CheckBox
        {
            Content = T("Trust this verified publisher fingerprint for future installs", "Considera attendibile l'impronta di questo editore verificato per le installazioni future"),
            IsChecked = false
        };

        private ModelContextOptions? _contextOptions;
        private PluginBundleInspection? _lastPluginInspection;

        private UIElement BuildContextCard()
        {
            var surface = new StackPanel { Spacing = 16 };
            surface.Children.Add(BuildPhysicalContextSection());
            surface.Children.Add(BuildExecutionProfileSection());
            surface.Children.Add(BuildPluginBundleSection());
            return surface;
        }

        private UIElement BuildPhysicalContextSection()
        {
            var card = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
            var body = new StackPanel { Spacing = 10 };
            card.Child = body;

            body.Children.Add(new TextBlock
            {
                Text = T("Physical context window", "Finestra di contesto fisica"),
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock
            {
                Text = T("This is how many tokens the selected model can attend to simultaneously in one inference. ", "È il numero di token che il modello selezionato può considerare contemporaneamente in una singola inferenza. ")
                    + T("PaRuX derives launchable values from the model's attention geometry and this machine's memory. ", "PaRuX ricava i valori avviabili dalla geometria dell'attenzione del modello e dalla memoria di questa macchina. ")
                    + T("Choosing a larger physical window consumes more KV-cache memory; stretching beyond the trained ", "Scegliere una finestra fisica più grande consuma più memoria per la cache KV; andare oltre la finestra di ")
                    + T("window uses positional scaling and can reduce recall/coherence near the far end.", "addestramento usa lo scaling posizionale e può ridurre richiamo e coerenza verso l'estremo lontano."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            });

            var virtualContext = new Border
            {
                Padding = new Thickness(12),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };
            virtualContext.Child = new TextBlock
            {
                Text = T("Virtual Context · automatic\n", "Contesto virtuale · automatico\n")
                    + T("You do NOT need a native 128k model to keep working past 128k. PaRuX can maintain 128k, 256k, ", "NON serve un modello nativo da 128k per continuare a lavorare oltre i 128k. PaRuX può mantenere cronologie logiche da 128k, 256k, ")
                    + T("1M+ logical histories on any model by keeping recent turns verbatim, preserving durable constraints/", "1M+ su qualsiasi modello, tenendo i turni recenti alla lettera, conservando vincoli/")
                    + T("decisions/IDs/test results, and recalling task-relevant historical pages into the real window. ", "decisioni/ID/risultati dei test duraturi e richiamando nella finestra reale le pagine storiche rilevanti per l'attività. ")
                    + T("This is long-running memory, not a false claim that every historical token is simultaneously in attention.", "È una memoria di lunga durata, non l'affermazione falsa che ogni token storico sia contemporaneamente nell'attenzione."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.86
            };
            body.Children.Add(virtualContext);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            controls.Children.Add(new TextBlock
            {
                Text = T("Physical length", "Lunghezza fisica"),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.7
            });
            controls.Children.Add(_contextLength);

            var apply = new Button { Content = T("Apply", "Applica") };
            apply.Click += async (_, _) => await ApplyContextLengthAsync();
            controls.Children.Add(apply);

            var reset = new Button { Content = T("Let PaRuX choose", "Lascia scegliere a PaRuX") };
            reset.Click += async (_, _) => await ClearContextLengthAsync();
            controls.Children.Add(reset);

            body.Children.Add(controls);
            body.Children.Add(_allowBeyondTrained);
            body.Children.Add(_contextCost);
            body.Children.Add(_contextSummary);

            _contextLength.SelectionChanged += (_, _) => ShowSelectedCost();
            return card;
        }

        private UIElement BuildExecutionProfileSection()
        {
            var card = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
            var body = new StackPanel { Spacing = 10 };
            card.Child = body;
            body.Children.Add(new TextBlock
            {
                Text = T("Execution Profile", "Profilo di esecuzione"),
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock
            {
                Text = T("One Core-owned policy controls time, tool budget, evidence/context spend, verification and parallelism across PaRuX. The selected model keeps its explicit routing choice; this changes how much work PaRuX may spend, not which model you selected.", "Un'unica politica gestita da Core controlla tempo, budget degli strumenti, spesa di evidenze/contesto, verifica e parallelismo in tutto PaRuX. Il modello selezionato mantiene la sua scelta di instradamento esplicita; questo cambia quanto lavoro PaRuX può spendere, non il modello che hai scelto."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            });

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_executionQuality, T("Execution quality", "Qualità di esecuzione"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_contextLength, T("Context length", "Lunghezza del contesto"));
            foreach (string quality in ExecutionQualityProfile.All)
                _executionQuality.Items.Add(new ComboBoxItem { Content = ExecutionQualityLabel(quality), Tag = quality });
            _executionQuality.SelectedIndex = 1;

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            controls.Children.Add(new TextBlock { Text = T("Quality", "Qualità"), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
            controls.Children.Add(_executionQuality);
            var apply = new Button { Content = T("Apply profile", "Applica il profilo") };
            apply.Click += async (_, _) => await ApplyExecutionQualityAsync();
            controls.Children.Add(apply);
            var refresh = new Button { Content = T("Refresh", "Aggiorna") };
            refresh.Click += async (_, _) => await RefreshExecutionProfileAsync(CancellationToken.None);
            controls.Children.Add(refresh);
            body.Children.Add(controls);
            body.Children.Add(_executionProfileSummary);
            return card;
        }

        private UIElement BuildPluginBundleSection()
        {
            var card = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
            var body = new StackPanel { Spacing = 10 };
            card.Child = body;
            body.Children.Add(new TextBlock
            {
                Text = T("Plugin Bundles", "Bundle di plugin"),
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock
            {
                Text = T("Inspect declarative .parux-plugin bundles before installation. PaRuX validates the archive, signature/trust and requested permissions. Installed skills/automations remain disabled and MCP definitions remain staged/inert until separately enabled.", "Ispeziona i bundle .parux-plugin dichiarativi prima dell'installazione. PaRuX convalida l'archivio, la firma/attendibilità e i permessi richiesti. Skill/automazioni installate restano disattivate e le definizioni MCP restano preparate e inerti finché non vengono abilitate separatamente."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            });

            var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            pathRow.Children.Add(_pluginBundlePath);
            var browse = new Button { Content = T("Browse…", "Sfoglia…") };
            browse.Click += async (_, _) => await ChoosePluginBundleAsync();
            pathRow.Children.Add(browse);
            var inspect = new Button { Content = T("Inspect", "Ispeziona") };
            inspect.Click += async (_, _) => await InspectPluginBundleAsync();
            pathRow.Children.Add(inspect);
            body.Children.Add(pathRow);
            body.Children.Add(_pluginInspectionSummary);
            body.Children.Add(_pluginAllowUnsigned);
            body.Children.Add(_pluginTrustPublisher);

            var installRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var install = new Button { Content = T("Install inspected bundle", "Installa il bundle ispezionato") };
            install.Click += async (_, _) => await InstallPluginBundleAsync();
            installRow.Children.Add(install);
            var refreshTrust = new Button { Content = T("Refresh trusted publishers", "Aggiorna gli editori attendibili") };
            refreshTrust.Click += async (_, _) => await RefreshTrustedPublishersAsync(CancellationToken.None);
            installRow.Children.Add(refreshTrust);
            body.Children.Add(installRow);
            body.Children.Add(new TextBlock
            {
                Text = T("Trusted publishers", "Editori attendibili"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0)
            });
            body.Children.Add(_trustedPublishers);
            return card;
        }

        private async Task RefreshContextOptionsAsync(CancellationToken ct)
        {
            // These surfaces are independent from GGUF context selection, but RuntimeManagerView's
            // existing refresh path already calls this method after model discovery. Refresh them
            // here so the UI never displays stale Core policy/trust state.
            await RefreshExecutionProfileAsync(ct);
            await RefreshTrustedPublishersAsync(ct);

            _contextLength.Items.Clear();
            _contextOptions = null;
            _contextCost.Text = string.Empty;

            if (string.IsNullOrWhiteSpace(_selectedModelPath))
            {
                _contextSummary.Text = T("Choose a model above to see its physical context options. Virtual Context remains automatic.", "Scegli un modello qui sopra per vedere le sue opzioni di contesto fisico. Il contesto virtuale resta automatico.");
                return;
            }

            ModelContextOptions options;
            try
            {
                options = await ModelRolesClient.Current.GetContextOptionsAsync(_selectedModelPath!, ct);
            }
            catch (Exception ex)
            {
                _contextSummary.Text = T("Could not read this model's physical context options: ", "Impossibile leggere le opzioni di contesto fisico di questo modello: ") + ex.Message;
                return;
            }

            _contextOptions = options;

            if (!options.Known || options.Options.Count == 0)
            {
                _contextSummary.Text = options.Explanation
                    + T(" Virtual Context still allows histories larger than the model's native window without guessing a launch size.", " Il contesto virtuale consente comunque cronologie più grandi della finestra nativa del modello senza dover indovinare una dimensione di avvio.");
                return;
            }

            foreach (ModelContextOption option in options.Options)
            {
                _contextLength.Items.Add(new ComboBoxItem
                {
                    Content = DescribeLength(option.ContextLength) + "  ·  " + DescribeGigabytes(option.KvCacheBytes)
                        + T(" KV cache", " cache KV") + (option.RopeScale > 1.0 ? T("  ·  beyond trained window", " ·  oltre la finestra di addestramento") : string.Empty),
                    Tag = option
                });
            }

            long selected = options.Selected?.ContextLength ?? 0;
            int index = selected > 0
                ? options.Options.FindIndex(option => option.ContextLength == selected)
                : -1;
            _contextLength.SelectedIndex = index >= 0 ? index : options.Options.Count - 1;

            _contextSummary.Text = options.Selected != null
                ? T("Physical context is currently ", "Il contesto fisico attuale è ") + DescribeLength(options.Selected.ContextLength)
                    + T("; this model was trained for ", "; questo modello è stato addestrato per ") + DescribeLength(options.DeclaredMaximum)
                    + T(". Virtual Context continues beyond that physical window automatically.", ". Il contesto virtuale continua automaticamente oltre quella finestra fisica.")
                : T("PaRuX is choosing this model's physical context. It was trained for ", "PaRuX sta scegliendo il contesto fisico di questo modello. È stato addestrato per ")
                    + DescribeLength(options.DeclaredMaximum)
                    + T(". Virtual Context continues beyond that physical window automatically.", ". Il contesto virtuale continua automaticamente oltre quella finestra fisica.");
            ShowSelectedCost();
        }

        private async Task RefreshExecutionProfileAsync(CancellationToken ct)
        {
            try
            {
                ExecutionProfileStatus status = await ProductV2Client.Current
                    .GetExecutionProfileAsync(_selectedModelPath, ct);
                SelectExecutionQuality(status.ActiveQualityProfile);
                ModelExecutionProfile profile = status.Effective;
                ExecutionResourceBudget budget = profile.Budget;
                string scope = string.IsNullOrWhiteSpace(_selectedModelPath)
                    ? "Global policy"
                    : "Effective policy for " + System.IO.Path.GetFileName(_selectedModelPath);
                _executionProfileSummary.Text = scope
                    + " · " + ExecutionQualityLabel(status.ActiveQualityProfile)
                    + T("\nContext ", "\nContesto ") + profile.PreferredContextTokens.ToString("N0") + T(" tokens @ ", " token @ ") + budget.ContextFraction.ToString("P0")
                    + T(" · tools ", " · strumenti ") + budget.MaxToolSteps
                    + T(" · tool evidence ", " · evidenze degli strumenti ") + profile.ToolObservationCharacters.ToString("N0") + T(" chars", " caratteri")
                    + T(" · repository evidence ", " · evidenze del repository ") + profile.RepositoryEvidenceCharacters.ToString("N0") + T(" chars", " caratteri")
                    + T("\nVerification ", "\nVerifica ") + budget.VerificationPasses + T(" pass(es)", " passaggi")
                    + T(" · parallel tasks ", " · attività parallele ") + budget.MaxParallelTasks
                    + T(" · subagents ", " · subagenti ") + budget.MaxDirectSubagents + T(" / depth ", " / profondità ") + budget.MaxDelegationDepth
                    + " · " + profile.ToolStrategy + " · " + profile.StructuredOutputStrategy
                    + T("\nEvidence source: ", "\nFonte delle evidenze: ") + profile.EvidenceSource;
            }
            catch (Exception ex)
            {
                _executionProfileSummary.Text = T("Could not load the Execution Profile from Core: ", "Impossibile caricare da Core il profilo di esecuzione: ") + ex.Message;
            }
        }

        private async Task ApplyExecutionQualityAsync()
        {
            string quality = SelectedExecutionQuality();
            try
            {
                ExecutionProfileStatus status = await ProductV2Client.Current
                    .SetExecutionQualityAsync(quality, _selectedModelPath, CancellationToken.None);
                SelectExecutionQuality(status.ActiveQualityProfile);
                await RefreshExecutionProfileAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _executionProfileSummary.Text = T("Could not apply the Execution Profile: ", "Impossibile applicare il profilo di esecuzione: ") + ex.Message;
            }
        }

        private string SelectedExecutionQuality() =>
            _executionQuality.SelectedItem is ComboBoxItem item && item.Tag is string value
                ? value
                : ExecutionQualityProfile.Balanced;

        private void SelectExecutionQuality(string quality)
        {
            for (int i = 0; i < _executionQuality.Items.Count; i++)
            {
                if (_executionQuality.Items[i] is ComboBoxItem item
                    && item.Tag is string value
                    && string.Equals(value, quality, StringComparison.Ordinal))
                {
                    _executionQuality.SelectedIndex = i;
                    return;
                }
            }
        }

        private static string ExecutionQualityLabel(string quality) => quality switch
        {
            ExecutionQualityProfile.Fast => "Fast",
            ExecutionQualityProfile.MaximumQuality => "Maximum Quality",
            ExecutionQualityProfile.Overnight => "Overnight",
            _ => "Balanced"
        };

        private async Task ChoosePluginBundleAsync()
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add(".parux-plugin");
            picker.FileTypeFilter.Add(".zip");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null) return;
            _pluginBundlePath.Text = file.Path;
            _lastPluginInspection = null;
            _pluginInspectionSummary.Text = T("Bundle selected. Inspect it before installation.", "Bundle selezionato. Ispezionalo prima dell'installazione.");
        }

        private async Task InspectPluginBundleAsync()
        {
            string path = _pluginBundlePath.Text?.Trim() ?? string.Empty;
            if (path.Length == 0)
            {
                _pluginInspectionSummary.Text = T("Choose a plugin bundle first.", "Scegli prima un bundle di plugin.");
                return;
            }
            try
            {
                PluginBundleInspection inspection = await ProductV2Client.Current
                    .InspectPluginAsync(path, CancellationToken.None);
                _lastPluginInspection = inspection;
                _pluginInspectionSummary.Text = RenderPluginInspection(inspection);
            }
            catch (Exception ex)
            {
                _lastPluginInspection = null;
                _pluginInspectionSummary.Text = T("Plugin inspection failed: ", "Ispezione del plugin non riuscita: ") + ex.Message;
            }
        }

        private async Task InstallPluginBundleAsync()
        {
            string path = _pluginBundlePath.Text?.Trim() ?? string.Empty;
            PluginBundleInspection? inspection = _lastPluginInspection;
            if (inspection == null || !inspection.Valid || path.Length == 0)
            {
                _pluginInspectionSummary.Text = T("Inspect a valid bundle before installing it.", "Ispeziona un bundle valido prima di installarlo.");
                return;
            }
            if (!string.Equals(path, inspection.Manifest == null ? string.Empty : path, StringComparison.Ordinal))
            {
                // Defensive reset point: inspection is intentionally tied to the current path.
                _lastPluginInspection = null;
                _pluginInspectionSummary.Text = T("The selected bundle changed. Inspect it again before installation.", "Il bundle selezionato è cambiato. Ispezionalo di nuovo prima dell'installazione.");
                return;
            }
            if (!inspection.SignaturePresent && _pluginAllowUnsigned.IsChecked != true)
            {
                _pluginInspectionSummary.Text = T("This bundle is unsigned. Enable the explicit unsigned exception or use a signed bundle.", "Questo bundle non è firmato. Abilita l'eccezione esplicita per i non firmati oppure usa un bundle firmato.");
                return;
            }
            if (inspection.SignaturePresent && !inspection.SignatureValid)
            {
                _pluginInspectionSummary.Text = T("The bundle signature is invalid. PaRuX will not install it.", "La firma del bundle non è valida. PaRuX non lo installerà.");
                return;
            }

            string trust = inspection.SignaturePresent
                ? inspection.PublisherTrusted ? "Publisher is already trusted." : "Publisher is not yet trusted."
                : "UNSIGNED bundle: publisher identity is not cryptographically verified.";
            var dialog = new ContentDialog
            {
                Title = T("Install inspected plugin bundle?", "Installare il bundle di plugin ispezionato?"),
                Content = inspection.Manifest.DisplayName + " " + inspection.Manifest.Version + "\n"
                    + trust + "\n"
                    + BuildPermissionSummary(inspection.EffectivePermissions) + "\n\n"
                    + T("Skills/automations will be installed disabled and MCP definitions staged inert. Review them before enabling anything.", "Skill e automazioni verranno installate disattivate e le definizioni MCP preparate come inerti. Controllale prima di abilitare qualsiasi cosa."),
                PrimaryButtonText = T("Install", "Installa"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            try
            {
                PluginInstallResult result = await ProductV2Client.Current.InstallPluginAsync(new PluginInstallRequest
                {
                    BundlePath = path,
                    AllowUnsigned = _pluginAllowUnsigned.IsChecked == true,
                    TrustPublisher = _pluginTrustPublisher.IsChecked == true
                }, CancellationToken.None);
                _pluginInspectionSummary.Text = result.Message
                    + T("\nInstalled skills: ", "\nSkill installate: ") + result.SkillIds.Count
                    + T(" · automations: ", " · automazioni: ") + result.AutomationIds.Count
                    + T(" · staged MCP definitions: ", " · definizioni MCP preparate: ") + result.StagedMcpDefinitions;
                await RefreshTrustedPublishersAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _pluginInspectionSummary.Text = T("Plugin installation failed: ", "Installazione del plugin non riuscita: ") + ex.Message;
            }
        }

        private async Task RefreshTrustedPublishersAsync(CancellationToken ct)
        {
            try
            {
                List<PluginPublisherTrustDto> publishers = await ProductV2Client.Current
                    .ListTrustedPublishersAsync(ct);
                _trustedPublishers.Children.Clear();
                if (publishers.Count == 0)
                {
                    _trustedPublishers.Children.Add(new TextBlock
                    {
                        Text = T("No publisher fingerprints are trusted yet.", "Nessuna impronta di editore è ancora attendibile."),
                        Opacity = 0.68
                    });
                    return;
                }

                foreach (PluginPublisherTrustDto publisher in publishers.OrderBy(x => x.Publisher))
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(new TextBlock
                    {
                        Text = publisher.Publisher + " · " + ShortFingerprint(publisher.Fingerprint),
                        VerticalAlignment = VerticalAlignment.Center,
                        MinWidth = 360
                    });
                    var revoke = new Button { Content = T("Revoke trust", "Revoca l'attendibilità"), Tag = publisher.Fingerprint };
                    revoke.Click += async (sender, _) =>
                    {
                        if (sender is not Button button || button.Tag is not string fingerprint) return;
                        try
                        {
                            await ProductV2Client.Current.RevokeTrustedPublisherAsync(fingerprint, CancellationToken.None);
                            await RefreshTrustedPublishersAsync(CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _pluginInspectionSummary.Text = T("Could not revoke publisher trust: ", "Impossibile revocare l'attendibilità dell'editore: ") + ex.Message;
                        }
                    };
                    row.Children.Add(revoke);
                    _trustedPublishers.Children.Add(row);
                }
            }
            catch (Exception ex)
            {
                _trustedPublishers.Children.Clear();
                _trustedPublishers.Children.Add(new TextBlock
                {
                    Text = T("Could not load trusted publishers: ", "Impossibile caricare gli editori attendibili: ") + ex.Message,
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        private static string RenderPluginInspection(PluginBundleInspection inspection)
        {
            string signature = !inspection.SignaturePresent
                ? "unsigned"
                : inspection.SignatureValid
                    ? inspection.PublisherTrusted ? "signature valid · publisher trusted" : "signature valid · publisher untrusted"
                    : "INVALID SIGNATURE";
            string warnings = inspection.Warnings.Count == 0
                ? string.Empty
                : "\nWarnings: " + string.Join(" | ", inspection.Warnings);
            string diff = inspection.PermissionDiff.Count == 0
                ? string.Empty
                : "\nPermission changes: " + string.Join(", ", inspection.PermissionDiff);
            return inspection.Manifest.DisplayName + " " + inspection.Manifest.Version
                + " · publisher " + inspection.Manifest.Publisher
                + " · " + signature
                + "\nSHA-256: " + inspection.BundleSha256
                + "\nSkills " + inspection.SkillCount + " · automations " + inspection.AutomationCount + " · MCP " + inspection.McpCount
                + "\n" + BuildPermissionSummary(inspection.EffectivePermissions)
                + diff + warnings;
        }

        private static string BuildPermissionSummary(PluginPermissionDeclaration permissions)
        {
            var granted = new List<string>();
            if (permissions.ReadFiles) granted.Add("read files");
            if (permissions.WriteFiles) granted.Add("write files");
            if (permissions.DeleteFiles) granted.Add("delete files");
            if (permissions.Shell) granted.Add("shell");
            if (permissions.Network) granted.Add("network");
            if (permissions.Browser) granted.Add("browser");
            if (permissions.ComputerUse) granted.Add("computer use");
            if (permissions.ExternalProcesses) granted.Add("external processes");
            if (permissions.Automations) granted.Add("automations");
            if (permissions.RemoteMcp) granted.Add("remote MCP");
            return "Declared permissions: " + (granted.Count == 0 ? "none" : string.Join(", ", granted));
        }

        private static string ShortFingerprint(string value)
        {
            string fingerprint = (value ?? string.Empty).Trim();
            return fingerprint.Length <= 18 ? fingerprint : fingerprint[..18] + "…";
        }

        private ModelContextOption? SelectedContextOption() =>
            _contextLength.SelectedItem is ComboBoxItem item ? item.Tag as ModelContextOption : null;

        private void ShowSelectedCost()
        {
            ModelContextOption? option = SelectedContextOption();
            _contextCost.Text = option == null ? string.Empty : T("Physical-window cost: ", "Costo della finestra fisica: ") + option.Cost;
        }

        private async Task ApplyContextLengthAsync()
        {
            ModelContextOption? option = SelectedContextOption();
            if (option == null || string.IsNullOrWhiteSpace(_selectedModelPath))
            {
                _contextSummary.Text = T("Pick a model and a physical context length first.", "Scegli prima un modello e una lunghezza di contesto fisico.");
                return;
            }

            try
            {
                ModelContextPreferenceResult result = await ModelRolesClient.Current.SetContextPreferenceAsync(
                    new ModelContextPreferenceRequest
                    {
                        ModelPath = _selectedModelPath!,
                        ContextLength = option.ContextLength,
                        CacheType = option.CacheType,
                        UsesSystemRam = option.UsesSystemRam,
                        AllowBeyondTrainedContext = _allowBeyondTrained.IsChecked == true
                    },
                    CancellationToken.None);

                _contextSummary.Text = result.Applied
                    ? result.Message + (result.RequiresReload
                        ? T(" This physical-window change takes effect next time the model is loaded; Virtual Context does not require a reload.", " Questa modifica della finestra fisica ha effetto al prossimo caricamento del modello; il contesto virtuale non richiede un ricaricamento.")
                        : T(" Virtual Context remains automatic above this window.", " Il contesto virtuale resta automatico oltre questa finestra."))
                    : result.Message;
            }
            catch (Exception ex)
            {
                _contextSummary.Text = T("Could not apply that physical context length: ", "Impossibile applicare quella lunghezza di contesto fisico: ") + ex.Message;
            }
        }

        private async Task ClearContextLengthAsync()
        {
            if (string.IsNullOrWhiteSpace(_selectedModelPath)) return;
            try
            {
                ModelContextPreferenceResult result = await ModelRolesClient.Current
                    .ClearContextPreferenceAsync(_selectedModelPath!, CancellationToken.None);
                _contextSummary.Text = result.Message + T(" Virtual Context remains automatic.", " Il contesto virtuale resta automatico.");
                await RefreshContextOptionsAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _contextSummary.Text = T("Could not reset this model's physical context: ", "Impossibile reimpostare il contesto fisico di questo modello: ") + ex.Message;
            }
        }

        private static string DescribeLength(long tokens) =>
            tokens <= 0 ? "unknown"
            : tokens % 1024 == 0 ? (tokens / 1024).ToString() + "k tokens"
            : tokens.ToString("N0") + " tokens";

        private static string DescribeGigabytes(long bytes) =>
            bytes <= 0 ? "unknown" : (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0") + " GB";
    }
}
