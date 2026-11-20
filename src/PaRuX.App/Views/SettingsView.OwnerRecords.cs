using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Three owner-only surfaces over Core: installed plugins (with Remove), the activity log (with a tamper check) and outbound
    /// webhooks. Core stays authoritative; these panels only show its answers and send the owner's explicit choices.
    /// </summary>
    public sealed partial class SettingsView
    {
        private bool _ownerRecordsAdded;
        private StackPanel? _installedPluginsList;
        private TextBlock? _installedPluginsStatus;
        private StackPanel? _auditList;
        private TextBlock? _auditStatus;
        private StackPanel? _webhookList;
        private TextBlock? _webhookStatus;
        private TextBox? _webhookUrl;
        private TextBox? _webhookEvents;

        private void AddOwnerRecordsSections()
        {
            if (_ownerRecordsAdded || SettingsContentPanel == null) return;
            _ownerRecordsAdded = true;

            SettingsContentPanel.Children.Add(OwnerSection("PaRuX Platform", T("INSTALLED PLUGINS", "PLUGIN INSTALLATI"),
                T("Plugins you installed. Removing one deletes its skills and automations; it never touches anything else.",
                  "Plugin che hai installato. Rimuoverne uno elimina le sue competenze e automazioni, senza toccare altro."),
                out _installedPluginsList, out _installedPluginsStatus, "Settings_InstalledPlugins",
                (T("Refresh", "Aggiorna"), () => _ = RefreshInstalledPluginsAsync())));

            StackPanel audit = OwnerSection("Privacy & Network", T("ACTIVITY LOG", "REGISTRO ATTIVITÀ"),
                T("A private record on this PC of security-relevant actions (plugins, remote access, API keys, Internet switch). Nothing in it leaves the PC.",
                  "Un registro privato su questo PC delle azioni rilevanti per la sicurezza (plugin, accesso remoto, chiavi API, interruttore Internet). Nulla esce dal PC."),
                out _auditList, out _auditStatus, "Settings_AuditLog",
                (T("Refresh", "Aggiorna"), () => _ = RefreshAuditAsync()),
                (T("Check the log is untouched", "Verifica che il registro sia intatto"), () => _ = VerifyAuditAsync()));
            // Corporate hides the ordinary Privacy cards behind a compact local-only panel; the activity log stays visible there, because
            // an administrator needs it most.
            AutomationProperties.SetAutomationId(audit, "Settings_AuditLog_Section");
            SettingsContentPanel.Children.Add(audit);
            SettingsContentPanel.Children.Add(BuildRolesSection());

            // Corporate Lite is offline by construction: no webhooks and no Steam (the Core refuses them too).
            bool corporate = PaRuX.Contracts.CorporateMode.IsActive;
            StackPanel webhooks = OwnerSection("Privacy & Network", T("WEBHOOKS", "WEBHOOK"),
                T("Send a signed message to an address you choose when something in the activity log happens. Off until you add one. An address outside your network needs the Internet switch on.",
                  "Invia un messaggio firmato a un indirizzo che scegli quando succede qualcosa nel registro. Spenti finché non ne aggiungi uno. Un indirizzo fuori dalla tua rete richiede l'interruttore Internet acceso."),
                out _webhookList, out _webhookStatus, "Settings_Webhooks",
                (T("Refresh", "Aggiorna"), () => _ = RefreshWebhooksAsync()));
            var card = (StackPanel)((Border)webhooks.Children[1]).Child;
            _webhookUrl = new TextBox { Header = T("Address", "Indirizzo"), PlaceholderText = "http://192.168.1.20:8080/parux" };
            _webhookEvents = new TextBox { Header = T("Events (comma separated)", "Eventi (separati da virgola)"), PlaceholderText = "plugin., apikey." };
            AutomationProperties.SetAutomationId(_webhookUrl, "Settings_Webhooks_Url_TextBox");
            AutomationProperties.SetAutomationId(_webhookEvents, "Settings_Webhooks_Events_TextBox");
            var add = new Button { Content = T("Add webhook", "Aggiungi webhook") };
            AutomationProperties.SetAutomationId(add, "Settings_Webhooks_Add_Button");
            add.Click += async (_, _) => await AddWebhookAsync();
            card.Children.Add(_webhookUrl);
            card.Children.Add(_webhookEvents);
            card.Children.Add(add);
            if (!corporate) SettingsContentPanel.Children.Add(webhooks);

            if (!corporate) SettingsContentPanel.Children.Add(BuildSteamSection());

            _ = RefreshInstalledPluginsAsync();
            _ = RefreshAuditAsync();
            _ = RefreshWebhooksAsync();
        }

        private StackPanel? _rolesList;
        private TextBlock? _rolesStatus;

        /// <summary>Read-only access for a paired device: the owner picks, per device, between full access and looking only.</summary>
        private StackPanel BuildRolesSection()
        {
            StackPanel section = OwnerSection("Privacy & Network", T("DEVICE ACCESS", "ACCESSO DEI DISPOSITIVI"),
                T("Choose what each paired phone or tablet may do. Read only means it can look at chats and status but cannot send messages, start work or change anything. This PC always has full access.",
                  "Scegli cosa può fare ogni telefono o tablet abbinato. Sola lettura significa che può guardare chat e stato ma non inviare messaggi, avviare attività né cambiare nulla. Questo PC ha sempre accesso completo."),
                out _rolesList, out _rolesStatus, "Settings_Roles",
                (T("Refresh", "Aggiorna"), () => _ = RefreshRolesAsync()));
            AutomationProperties.SetAutomationId(section, "Settings_Roles_Section");
            _ = RefreshRolesAsync();
            return section;
        }

        private async Task RefreshRolesAsync()
        {
            if (_rolesList == null || _rolesStatus == null) return;
            try
            {
                RemoteAccessStatus remote = await OwnerRecordsClient.Current.RemoteStatusAsync(CancellationToken.None);
                List<PrincipalRoleEntry> roles = await OwnerRecordsClient.Current.ListRolesAsync(CancellationToken.None);
                _rolesList.Children.Clear();
                if (remote.Devices.Count == 0) _rolesList.Children.Add(Line(T("No paired devices.", "Nessun dispositivo abbinato."), 0.7));
                foreach (PairedDeviceInfo device in remote.Devices)
                {
                    string principal = "device:" + device.Id;
                    string current = roles.FirstOrDefault(r => r.PrincipalId == principal)?.Role ?? PrincipalRoles.Operator;
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(Line(device.Name));
                    var choice = new ComboBox { MinWidth = 180 };
                    AutomationProperties.SetName(choice, T("Access for ", "Accesso per ") + device.Name);
                    AutomationProperties.SetAutomationId(choice, "Settings_Roles_Device_" + device.Id);
                    choice.Items.Add(new ComboBoxItem { Content = T("Full access", "Accesso completo"), Tag = PrincipalRoles.Operator });
                    choice.Items.Add(new ComboBoxItem { Content = T("Read only", "Sola lettura"), Tag = PrincipalRoles.Viewer });
                    choice.SelectedIndex = current == PrincipalRoles.Viewer ? 1 : 0;
                    choice.SelectionChanged += async (_, _) =>
                    {
                        if (choice.SelectedItem is not ComboBoxItem { Tag: string role }) return;
                        try { await OwnerRecordsClient.Current.SetRoleAsync(principal, role, CancellationToken.None); _rolesStatus!.Text = T("Saved.", "Salvato."); }
                        catch (Exception ex) { _rolesStatus!.Text = ex.Message; }
                    };
                    Grid.SetColumn(choice, 1);
                    row.Children.Add(choice);
                    _rolesList.Children.Add(row);
                }
            }
            catch (Exception ex) { _rolesStatus.Text = T("Could not read device access: ", "Impossibile leggere l'accesso dei dispositivi: ") + ex.Message; }
        }

        private TextBlock? _steamStatus;

        /// <summary>Steam achievements need the owner's own Web API key and SteamID. Nothing is requested until both are saved and the Internet switch is on.</summary>
        private StackPanel BuildSteamSection()
        {
            StackPanel section = OwnerSection("Integrations", T("STEAM ACHIEVEMENTS", "RISULTATI STEAM"),
                T("Show your Steam achievements on a game's card. You use your own Steam Web API key (steamcommunity.com/dev/apikey) and your SteamID64, and your Steam profile's Game details must be Public. The key is stored encrypted and never shown again. Needs the Internet switch on.",
                  "Mostra i tuoi risultati Steam nella scheda di un gioco. Usi la tua chiave Steam Web API (steamcommunity.com/dev/apikey) e il tuo SteamID64, e i Dettagli di gioco del profilo Steam devono essere Pubblici. La chiave è salvata cifrata e non viene più mostrata. Richiede l'interruttore Internet acceso."),
                out _, out _steamStatus, "Settings_Steam");
            var card = (StackPanel)((Border)section.Children[1]).Child;
            var key = new PasswordBox { Header = T("Steam Web API key", "Chiave Steam Web API") };
            var id = new TextBox { Header = "SteamID64", PlaceholderText = "76561198000000000" };
            AutomationProperties.SetAutomationId(key, "Settings_Steam_Key_PasswordBox");
            AutomationProperties.SetAutomationId(id, "Settings_Steam_Id_TextBox");
            var save = new Button { Content = T("Save", "Salva") };
            var remove = new Button { Content = T("Remove", "Rimuovi") };
            AutomationProperties.SetAutomationId(save, "Settings_Steam_Save_Button");
            AutomationProperties.SetAutomationId(remove, "Settings_Steam_Remove_Button");
            save.Click += async (_, _) =>
            {
                try
                {
                    var status = await OwnerRecordsClient.Current.SaveSteamAsync(new SteamCredentialsRequest { ApiKey = key.Password, SteamId = id.Text }, CancellationToken.None);
                    key.Password = string.Empty;
                    _steamStatus!.Text = T("Saved for SteamID ", "Salvato per lo SteamID ") + status.SteamId + ".";
                }
                catch (Exception ex) { _steamStatus!.Text = ex.Message; }
            };
            remove.Click += async (_, _) =>
            {
                try { await OwnerRecordsClient.Current.DeleteSteamAsync(CancellationToken.None); _steamStatus!.Text = T("Removed.", "Rimossa."); }
                catch (Exception ex) { _steamStatus!.Text = ex.Message; }
            };
            card.Children.Add(key);
            card.Children.Add(id);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(save);
            row.Children.Add(remove);
            card.Children.Add(row);
            _ = Task.Run(async () =>
            {
                try
                {
                    var status = await OwnerRecordsClient.Current.SteamStatusAsync(CancellationToken.None);
                    _dispatcher.TryEnqueue(() => _steamStatus!.Text = status.Configured
                        ? T("Saved for SteamID ", "Salvato per lo SteamID ") + status.SteamId + "." : T("Not set up.", "Non configurato."));
                }
                catch (Exception) { }
            });
            return section;
        }

        private StackPanel OwnerSection(string category, string title, string description, out StackPanel list, out TextBlock status,
            string idPrefix, params (string Label, Action Run)[] buttons)
        {
            var section = new StackPanel
            {
                Tag = category, Spacing = 8,
                Visibility = string.Equals(_selectedSettingsCategory, category, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed,
            };
            section.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 0) });
            var card = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? stroke) && stroke is Microsoft.UI.Xaml.Media.Brush brush)
                card.BorderBrush = brush;
            var content = new StackPanel { Spacing = 9 };
            content.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
            list = new StackPanel { Spacing = 6 };
            AutomationProperties.SetAutomationId(list, idPrefix + "_List");
            content.Children.Add(list);
            status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
            AutomationProperties.SetAutomationId(status, idPrefix + "_Status_Text");
            content.Children.Add(status);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach ((string label, Action run) in buttons)
            {
                var b = new Button { Content = label };
                Action captured = run;
                b.Click += (_, _) => captured();
                row.Children.Add(b);
            }
            content.Children.Add(row);
            card.Child = content;
            section.Children.Add(card);
            return section;
        }

        private static TextBlock Line(string text, double opacity = 1) =>
            new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity };

        // ---- installed plugins ----

        private async Task RefreshInstalledPluginsAsync()
        {
            if (_installedPluginsList == null || _installedPluginsStatus == null) return;
            try
            {
                List<InstalledPluginDto> plugins = await OwnerRecordsClient.Current.ListPluginsAsync(CancellationToken.None);
                _installedPluginsList.Children.Clear();
                if (plugins.Count == 0) _installedPluginsList.Children.Add(Line(T("No plugins installed.", "Nessun plugin installato."), 0.7));
                foreach (InstalledPluginDto plugin in plugins)
                {
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    string name = string.IsNullOrWhiteSpace(plugin.Name) ? plugin.PluginId : plugin.Name;
                    row.Children.Add(Line(name + " " + plugin.Version + (string.IsNullOrWhiteSpace(plugin.Publisher) ? "" : " · " + plugin.Publisher)));
                    var remove = new Button { Content = T("Remove", "Rimuovi") };
                    AutomationProperties.SetAutomationId(remove, "Settings_InstalledPlugins_Remove_" + plugin.PluginId);
                    InstalledPluginDto captured = plugin;
                    remove.Click += async (_, _) => await RemovePluginAsync(captured);
                    Grid.SetColumn(remove, 1);
                    row.Children.Add(remove);
                    _installedPluginsList.Children.Add(row);
                }
                _installedPluginsStatus.Text = string.Empty;
            }
            catch (Exception ex) { _installedPluginsStatus.Text = T("Could not read the plugin list: ", "Impossibile leggere l'elenco dei plugin: ") + ex.Message; }
        }

        private async Task RemovePluginAsync(InstalledPluginDto plugin)
        {
            var dialog = new ContentDialog
            {
                Title = T("Remove this plugin?", "Rimuovere questo plugin?"),
                Content = T("Its skills and automations are deleted. Your own skills, chats and files are not touched.",
                            "Le sue competenze e automazioni vengono eliminate. Le tue competenze, chat e file non vengono toccati."),
                PrimaryButtonText = T("Remove", "Rimuovi"), CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                PluginUninstallResult result = await OwnerRecordsClient.Current.UninstallPluginAsync(plugin.PluginId, plugin.Version, CancellationToken.None);
                if (_installedPluginsStatus != null) _installedPluginsStatus.Text = result.Message;
            }
            catch (Exception ex) { if (_installedPluginsStatus != null) _installedPluginsStatus.Text = T("Could not remove: ", "Impossibile rimuovere: ") + ex.Message; }
            await RefreshInstalledPluginsAsync();
            await RefreshAuditAsync();
        }

        // ---- activity log ----

        private async Task RefreshAuditAsync()
        {
            if (_auditList == null || _auditStatus == null) return;
            try
            {
                List<AuditEntry> entries = await OwnerRecordsClient.Current.RecentAuditAsync(15, CancellationToken.None);
                _auditList.Children.Clear();
                if (entries.Count == 0) _auditList.Children.Add(Line(T("Nothing recorded yet.", "Ancora nulla registrato."), 0.7));
                foreach (AuditEntry e in entries)
                    _auditList.Children.Add(Line(e.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + " · " + e.Action
                        + (string.IsNullOrWhiteSpace(e.Subject) ? "" : " · " + e.Subject), 0.9));
            }
            catch (Exception ex) { _auditStatus.Text = T("Could not read the log: ", "Impossibile leggere il registro: ") + ex.Message; }
        }

        private async Task VerifyAuditAsync()
        {
            if (_auditStatus == null) return;
            try
            {
                AuditVerification v = await OwnerRecordsClient.Current.VerifyAuditAsync(CancellationToken.None);
                _auditStatus.Text = v.Intact
                    ? T("The log is intact (" + v.Entries + " entries).", "Il registro è intatto (" + v.Entries + " voci).")
                    : T("The log was changed: ", "Il registro è stato modificato: ") + v.Message;
            }
            catch (Exception ex) { _auditStatus.Text = T("Could not check the log: ", "Impossibile verificare il registro: ") + ex.Message; }
        }

        // ---- webhooks ----

        private async Task RefreshWebhooksAsync()
        {
            if (_webhookList == null || _webhookStatus == null) return;
            try
            {
                List<WebhookDto> hooks = await OwnerRecordsClient.Current.ListWebhooksAsync(CancellationToken.None);
                _webhookList.Children.Clear();
                if (hooks.Count == 0) _webhookList.Children.Add(Line(T("No webhooks.", "Nessun webhook."), 0.7));
                foreach (WebhookDto hook in hooks)
                {
                    var row = new StackPanel { Spacing = 4 };
                    string state = !hook.Enabled ? T("switched off after repeated failures", "spento dopo errori ripetuti")
                        : hook.LastAttemptUtc is null ? T("not used yet", "mai usato")
                        : T("last answer ", "ultima risposta ") + hook.LastStatus;
                    row.Children.Add(Line(hook.Url + " · " + string.Join(", ", hook.Events) + " · " + state));
                    var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    var test = new Button { Content = T("Send a test", "Invia un test") };
                    var delete = new Button { Content = T("Delete", "Elimina") };
                    AutomationProperties.SetAutomationId(test, "Settings_Webhooks_Test_" + hook.Id);
                    AutomationProperties.SetAutomationId(delete, "Settings_Webhooks_Delete_" + hook.Id);
                    string id = hook.Id;
                    test.Click += async (_, _) =>
                    {
                        try { WebhookTestResult r = await OwnerRecordsClient.Current.TestWebhookAsync(id, CancellationToken.None); _webhookStatus.Text = r.Message; }
                        catch (Exception ex) { _webhookStatus.Text = ex.Message; }
                        await RefreshWebhooksAsync();
                    };
                    delete.Click += async (_, _) =>
                    {
                        try { await OwnerRecordsClient.Current.DeleteWebhookAsync(id, CancellationToken.None); _webhookStatus.Text = T("Webhook deleted.", "Webhook eliminato."); }
                        catch (Exception ex) { _webhookStatus.Text = ex.Message; }
                        await RefreshWebhooksAsync();
                    };
                    buttons.Children.Add(test);
                    buttons.Children.Add(delete);
                    row.Children.Add(buttons);
                    _webhookList.Children.Add(row);
                }
            }
            catch (Exception ex) { _webhookStatus.Text = T("Could not read the webhooks: ", "Impossibile leggere i webhook: ") + ex.Message; }
        }

        private async Task AddWebhookAsync()
        {
            if (_webhookUrl == null || _webhookEvents == null || _webhookStatus == null) return;
            try
            {
                var request = new CreateWebhookRequest
                {
                    Url = _webhookUrl.Text,
                    Events = _webhookEvents.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                };
                WebhookCreated created = await OwnerRecordsClient.Current.CreateWebhookAsync(request, CancellationToken.None);
                var secretBox = new TextBox { Text = created.Secret, IsReadOnly = true, MinWidth = 360 };
                var dialog = new ContentDialog
                {
                    Title = T("Copy the signing secret now", "Copia ora il segreto di firma"),
                    Content = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            Line(T("PaRuX cannot show this secret again. Your receiver uses it to check the X-PaRuX-Signature header.",
                                   "PaRuX non può mostrare di nuovo questo segreto. Il tuo ricevitore lo usa per controllare l'intestazione X-PaRuX-Signature.")),
                            secretBox,
                        },
                    },
                    CloseButtonText = T("I copied it", "L'ho copiato"), XamlRoot = XamlRoot,
                };
                _webhookUrl.Text = string.Empty;
                _webhookEvents.Text = string.Empty;
                _webhookStatus.Text = T("Webhook added.", "Webhook aggiunto.");
                await RefreshWebhooksAsync();
                await dialog.ShowAsync();
            }
            catch (Exception ex) { _webhookStatus.Text = ex.Message; }
        }
    }
}
