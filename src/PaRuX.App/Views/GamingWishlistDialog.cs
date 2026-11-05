using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// Compact, local-first Steam wishlist editor. This is not a monitoring
/// service: only the explicit one-shot quote button can fetch a price. It never
/// schedules polling or generates notifications.
/// Uses the desktop-owner control plane and respects the Gaming hard-off in Core.
/// </summary>
internal static class GamingWishlistDialog
{
    private static string T(string en, string it) => GameUi.T(en, it);
    private static readonly GamingLibraryClient Client = GamingLibraryClient.Current;

    internal static async Task ShowAsync(XamlRoot root)
    {
        string? feedback = null;
        // Each action closes the active dialog before opening another; WinUI does not
        // allow nested ContentDialogs on the same root.
        for (int actions = 0; actions < 40; actions++)
        {
            GamingCallResult<List<LocalWishlistItemDto>> fetched = await Client.GetWishlistAsync();
            if (!fetched.Succeeded || fetched.Value is null)
            {
                await MessageAsync(root, T("Wishlist unavailable", "Lista desideri non disponibile"),
                    fetched.Error ?? T("Could not read the local list.", "Impossibile leggere la lista locale."));
                return;
            }
            var items = fetched.Value.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            var selection = new ComboBox
            {
                Header = T("Saved games", "Giochi salvati"),
                PlaceholderText = T("Choose a game", "Seleziona un gioco"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(selection, "GameHome_Wishlist_Select");
            foreach (LocalWishlistItemDto item in items)
            {
                selection.Items.Add(new ComboBoxItem
                {
                    Content = item.Title + " · Steam " + item.ProductId
                        + " · " + (item.PriceWatchOptIn ? T("preference saved", "preferenza salvata") : T("preference off", "preferenza disattivata")),
                    Tag = item
                });
            }
            if (selection.Items.Count > 0) selection.SelectedIndex = 0;
            var status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = feedback ?? (items.Count == 0
                    ? T("No games saved yet.", "Non ci sono ancora giochi salvati.")
                    : T("These preferences are stored only on this PC.", "Queste preferenze rimangono solo su questo PC."))
            };
            AutomationProperties.SetAutomationId(status, "GameHome_Wishlist_Status");
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            var remove = new Button
            {
                Content = T("Remove selected game", "Rimuovi gioco selezionato"),
                IsEnabled = selection.Items.Count > 0
            };
            AutomationProperties.SetAutomationId(remove, "GameHome_Wishlist_Remove");
            remove.Click += async (_, _) =>
            {
                if (selection.SelectedItem is not ComboBoxItem { Tag: LocalWishlistItemDto selected }) return;
                remove.IsEnabled = false;
                try
                {
                    GamingCallResult<object> response = await Client.RemoveWishlistItemAsync(selected.ProductId);
                    status.Text = response.Succeeded
                        ? T("Game removed from the local wishlist.", "Gioco rimosso dalla lista locale.")
                        : response.Error ?? T("Could not remove the game.", "Impossibile rimuovere il gioco.");
                    if (response.Succeeded)
                    {
                        selection.Items.Remove(selection.SelectedItem);
                        if (selection.Items.Count > 0) selection.SelectedIndex = 0;
                    }
                }
                finally { remove.IsEnabled = selection.Items.Count > 0; }
            };

            var panel = new StackPanel { Spacing = 12, MaxWidth = 490 };
            panel.Children.Add(new TextBlock
            {
                Text = T("Your list stays on this PC. Only explicit one-shot price buttons contact Steam after Game Discover consent. Batch checks at most 8 watched games in sequence. No scheduled checks, alerts or purchases.",
                    "La lista resta su questo PC. Solo i pulsanti di verifica manuale contattano Steam dopo il consenso Game Discover. Il controllo multiplo verifica al massimo 8 giochi in sequenza. Nessun controllo programmato, avviso o acquisto."),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(selection);
            // A quote is requested only from this explicit click; it never starts
            // periodic checks and requires the separately enabled Game Discover
            // setting. The requested country may not match the Steam account's region.
            var quote = new Button
            {
                Content = T("Check Steam price once", "Controlla prezzo Steam una volta"),
                IsEnabled = selection.Items.Count > 0
            };
            AutomationProperties.SetAutomationId(quote, "GameHome_Wishlist_Quote");
            bool isBatchRunning = false;
            bool isSingleRunning = false;
            bool closing = false;
            var singleQuoteCts = new System.Threading.CancellationTokenSource();
            Task singleQuoteTask = Task.CompletedTask;
            selection.SelectionChanged += (_, _) =>
            {
                quote.IsEnabled = !isBatchRunning && !isSingleRunning && selection.SelectedItem is ComboBoxItem;
            };
            quote.Click += (_, _) => { singleQuoteTask = RunSingleQuoteAsync(); };
            async Task RunSingleQuoteAsync()
            {
                if (isSingleRunning || isBatchRunning || closing) return;
                if (selection.SelectedItem is not ComboBoxItem { Tag: LocalWishlistItemDto item }) return;
                isSingleRunning = true;
                quote.IsEnabled = false;
                remove.IsEnabled = false;
                status.Text = T("Requesting a one-time storefront price…", "Richiesta prezzo una tantum allo store…");
                try
                {
                    GamingCallResult<LocalWishlistQuoteDto> response =
                        await Client.QuoteWishlistPriceAsync(item.ProductId, consent: true, ct: singleQuoteCts.Token);
                    if (singleQuoteCts.IsCancellationRequested || closing) return;
                    if (!response.Succeeded || response.Value is null)
                    {
                        status.Text = response.Error ??
                            T("Steam did not provide a price. Enable Game Discover to allow one-time lookups.",
                              "Steam non ha fornito un prezzo. Abilita Game Discover per le ricerche una tantum.");
                        return;
                    }
                    LocalWishlistQuoteDto result = response.Value;
                    string money = (result.PriceMinor / 100m).ToString("0.00", CultureInfo.CurrentCulture)
                        + " " + result.Currency;
                    string compared = result.AtOrBelowTarget switch
                    {
                        true => T("at or below target", "entro il prezzo obiettivo"),
                        false => T("above target", "sopra il prezzo obiettivo"),
                        _ => T("not comparable to the saved target", "non confrontabile con la soglia salvata")
                    };
                    status.Text = money + " · " + result.RequestedCountry + " · " + compared
                        + Environment.NewLine + T(
                            "Indicative storefront price only: Steam account region not verified. No alerts or purchases.",
                            "Prezzo indicativo dello store: paese dell'account Steam non verificato. Nessun avviso o acquisto.");
                }
                catch (OperationCanceledException) { /* Dialog closed: HTTP request cancelled. */ }
                catch (Exception)
                {
                    if (!closing) status.Text = T("One-time Steam lookup failed.", "Richiesta prezzo Steam non riuscita.");
                }
                finally
                {
                    isSingleRunning = false;
                    if (!closing)
                    {
                        quote.IsEnabled = !isBatchRunning && selection.SelectedItem is ComboBoxItem;
                        remove.IsEnabled = selection.Items.Count > 0;
                    }
                }
            }

            // A separate, explicit button checks only already opted-in items.
            // This action is never scheduled and never runs from opening the dialog.
            LocalWishlistManualBatchPlan batchPlan = LocalWishlistManualBatchPolicy.Build(items);
            var batch = new Button
            {
                Content = T("Check watched prices once (max 8)", "Controlla prezzi selezionati una volta (max 8)"),
                IsEnabled = batchPlan.Items.Count > 0
            };
            AutomationProperties.SetAutomationId(batch, "GameHome_Wishlist_BatchQuote");
            var batchCts = new System.Threading.CancellationTokenSource();
            Task batchTask = Task.CompletedTask;
            batch.Click += (_, _) =>
            {
                batchTask = RunBatchAsync();
            };

            async Task RunBatchAsync()
            {
                if (isBatchRunning || isSingleRunning || closing) return;
                isBatchRunning = true;
                batch.IsEnabled = false;
                quote.IsEnabled = false;
                remove.IsEnabled = false;
                var results = new List<string>();
                int passed = 0;
                try
                {
                    foreach (LocalWishlistItemDto item in batchPlan.Items)
                    {
                        if (batchCts.IsCancellationRequested ||
                            GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive())
                        {
                            results.Add(T("Stopped: Gaming started or the dialog closed.",
                                "Interrotto: avviato Gaming o finestra chiusa."));
                            break;
                        }

                        status.Text = T("Checking " + (passed + 1) + " of " + batchPlan.Items.Count
                            + " watched games…", "Controllo " + (passed + 1) + " di " +
                            batchPlan.Items.Count + " giochi selezionati…");
                        // Each call reuses Core's owner consent, Discover allow-list,
                        // shared rate limiter and Gaming hard-off.
                        GamingCallResult<LocalWishlistQuoteDto> response =
                            await Client.QuoteWishlistPriceAsync(
                                item.ProductId, consent: true, ct: batchCts.Token);
                        if (batchCts.IsCancellationRequested || closing)
                            break;
                        passed++;
                        if (response.Succeeded && response.Value is { } price)
                        {
                            string money = (price.PriceMinor / 100m).ToString("0.00",
                                CultureInfo.CurrentCulture) + " " + price.Currency;
                            string comparison = price.AtOrBelowTarget switch
                            {
                                true => T("target met", "obiettivo raggiunto"),
                                false => T("above target", "sopra obiettivo"),
                                _ => T("not comparable", "non confrontabile")
                            };
                            results.Add(item.Title + ": " + money + " (" + price.RequestedCountry
                                + ", " + comparison + ")");
                        }
                        else
                        {
                            results.Add(item.Title + ": " +
                                (response.Error ?? T("price unavailable", "prezzo non disponibile")));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Closing the dialog or entering Gaming cancels the current HTTP call.
                }
                catch (Exception)
                {
                    results.Add(T("The batch stopped unexpectedly; try again when the Core is ready.",
                        "Controllo interrotto; riprova quando Core è disponibile."));
                }
                finally
                {
                    if (!closing)
                    {
                        if (batchPlan.DeferredCount > 0)
                            results.Add(T(batchPlan.DeferredCount + " more watched games were not checked. "
                                + "Disable some watch preferences to select others.",
                                batchPlan.DeferredCount + " altri giochi non sono stati controllati. "
                                + "Disattiva alcune preferenze per sceglierne altri."));
                        results.Add(T("Prices are indicative for the selected storefront, not a verified "
                            + "Steam account region. No alerts or purchases.",
                            "Prezzi indicativi dello store selezionato, non del paese verificato "
                            + "dell'account Steam. Nessun avviso o acquisto."));
                        status.Text = string.Join(Environment.NewLine, results);
                        batch.IsEnabled = batchPlan.Items.Count > 0;
                        quote.IsEnabled = selection.SelectedItem is ComboBoxItem;
                        remove.IsEnabled = selection.Items.Count > 0;
                    }
                    isBatchRunning = false;
                }
            }

            panel.Children.Add(remove);
            panel.Children.Add(quote);
            panel.Children.Add(batch);
            panel.Children.Add(status);
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = T("Steam wishlist · local-first", "Lista desideri Steam · locale"),
                Content = panel,
                PrimaryButtonText = T("Add game", "Aggiungi gioco"),
                SecondaryButtonText = T("Change watch setting", "Cambia controllo prezzi"),
                CloseButtonText = T("Close", "Chiudi"),
                DefaultButton = ContentDialogButton.Close,
                IsSecondaryButtonEnabled = selection.Items.Count > 0
            };
            dialog.Closing += (_, _) =>
            {
                closing = true;
                batchCts.Cancel();
                singleQuoteCts.Cancel();
            };
            ContentDialogResult choice = await dialog.ShowAsync();
            // Do not leave an in-flight request alive after the dialog closes or
            // open another content dialog while its price run is still finishing.
            batchCts.Cancel();
            singleQuoteCts.Cancel();
            await Task.WhenAll(batchTask, singleQuoteTask);
            batchCts.Dispose();
            singleQuoteCts.Dispose();
            if (choice == ContentDialogResult.None) return;
            if (choice == ContentDialogResult.Primary)
                feedback = await AddAsync(root);
            else if (selection.SelectedItem is ComboBoxItem { Tag: LocalWishlistItemDto selected })
                feedback = await ToggleWatchAsync(root, selected);
            else
                feedback = T("Select a game first.", "Seleziona prima un gioco.");
        }
    }

    private static async Task<string> AddAsync(XamlRoot root)
    {
        var id = new TextBox { Header = T("Steam App ID (digits)", "ID app Steam (cifre)"), MaxLength = 12 };
        var title = new TextBox { Header = T("Game title", "Titolo del gioco"), MaxLength = 200 };
        var price = new NumberBox
        {
            Header = T("Target price (major currency units)", "Prezzo obiettivo (unità monetarie)"),
            Value = 0,
            Minimum = 0,
            Maximum = 10000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var currency = new TextBox { Header = T("Currency code", "Codice valuta"), MaxLength = 3, Text = "EUR" };
        var country = new TextBox
        {
            Header = T("Store country (two-letter code)", "Paese dello store (codice di due lettere)"),
            MaxLength = 2,
            PlaceholderText = "IT"
        };
        AutomationProperties.SetAutomationId(id, "GameHome_Wishlist_AppId");
        AutomationProperties.SetAutomationId(title, "GameHome_Wishlist_Title");
        AutomationProperties.SetAutomationId(price, "GameHome_Wishlist_Target");
        AutomationProperties.SetAutomationId(currency, "GameHome_Wishlist_Currency");
        AutomationProperties.SetAutomationId(country, "GameHome_Wishlist_Country");

        var panel = new StackPanel { Spacing = 8, MaxWidth = 490 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = T("Saving a game does not enable price watching. Choose the region used by your store account.",
                "Salvare un gioco non attiva il controllo prezzi. Indica il paese usato dal tuo account store.")
        });
        panel.Children.Add(id);
        panel.Children.Add(title);
        panel.Children.Add(price);
        panel.Children.Add(currency);
        panel.Children.Add(country);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Add Steam game", "Aggiungi gioco Steam"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 470 },
            PrimaryButtonText = T("Save locally", "Salva in locale"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return T("No changes made.", "Nessuna modifica.");

        string appId = id.Text.Trim();
        string name = title.Text.Trim();
        string isoCurrency = currency.Text.Trim().ToUpperInvariant();
        string isoCountry = country.Text.Trim().ToUpperInvariant();
        if (appId.Length == 0 || appId.Any(c => c < '0' || c > '9') || name.Length == 0
            || isoCurrency.Length != 3 || !isoCurrency.All(c => c is >= 'A' and <= 'Z')
            || isoCountry.Length != 2 || !isoCountry.All(c => c is >= 'A' and <= 'Z')
            || !double.IsFinite(price.Value) || price.Value < 0 || price.Value > 10000)
            return T("Enter a valid Steam ID, title, currency, store country and price.",
                "Inserisci ID Steam, titolo, valuta, paese e prezzo validi.");

        int targetMinor = checked((int)Math.Round(price.Value * 100, MidpointRounding.AwayFromZero));
        var request = new LocalWishlistItemDto("steam", appId, name, isoCurrency, targetMinor, false)
        {
            StoreCountryCode = isoCountry
        };
        GamingCallResult<LocalWishlistItemDto> saved = await Client.AddWishlistItemAsync(request);
        return saved.Succeeded
            ? T("Game saved locally with price watching disabled.", "Gioco salvato in locale, controllo prezzi disattivato.")
            : saved.Error ?? T("Could not save this game.", "Impossibile salvare questo gioco.");
    }

    private static async Task<string> ToggleWatchAsync(XamlRoot root, LocalWishlistItemDto selected)
    {
        bool enable = !selected.PriceWatchOptIn;
        var confirmation = new ContentDialog
        {
            XamlRoot = root,
            Title = enable ? T("Enable price-watch preference?", "Attivare la preferenza controllo prezzi?")
                : T("Disable price-watch preference?", "Disattivare la preferenza controllo prezzi?"),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = enable
                    ? T("Save explicit consent for this game's threshold in the selected store region. PaRuX will NOT fetch prices, run checks or notify you automatically yet.",
                        "Salva il consenso esplicito per la soglia di questo gioco nel paese dello store selezionato. PaRuX NON recupererà prezzi né avvierà controlli o notifiche automatiche.")
                    : T("Turn off this preference? The game stays saved.",
                        "Disattivare questa preferenza? Il gioco resterà salvato.")
            },
            PrimaryButtonText = enable ? T("Enable preference", "Attiva preferenza") : T("Disable", "Disattiva"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            return T("No changes made.", "Nessuna modifica.");

        GamingCallResult<LocalWishlistItemDto> saved =
            await Client.SetWishlistWatchAsync(selected.ProductId, enable, consent: enable);
        return saved.Succeeded
            ? (enable ? T("Preference saved. No automatic checks have started.", "Preferenza salvata. Nessun controllo automatico avviato.")
                      : T("Price-watch preference disabled.", "Preferenza controllo prezzi disattivata."))
            : saved.Error ?? T("Could not update the preference.", "Impossibile modificare la preferenza.");
    }

    private static async Task MessageAsync(XamlRoot root, string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = T("Close", "Chiudi")
        }.ShowAsync();
    }
}
