using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        private int _workbookRefreshGeneration;
        private bool _workbookActionBusy;
        private WorkbookEditProposal? _workbookProposal;
        private string? _workbookConversation;

        private bool _workbookWasBusy;

        private void UpdateWorkbookTurnState(bool busy)
        {
            if (busy && !_workbookWasBusy)
            {
                ++_workbookRefreshGeneration;
                WorkbookEditSave.IsEnabled = false;
                WorkbookEditDiscard.IsEnabled = false;
                if (_workbookProposal != null)
                    WorkbookEditStatus.Text = T("Wait for the reply to finish before reviewing changes.", "Attendi la fine della risposta prima di controllare le modifiche.");
            }
            bool finished = _workbookWasBusy && !busy;
            _workbookWasBusy = busy;
            if (finished) _ = RefreshWorkbookEditAsync();
        }

        private async Task RefreshWorkbookEditAsync()
        {
            string? conversation = ChatSession.Current.Conversation?.Id;
            if (_workbookActionBusy && conversation == _workbookConversation) return;
            int generation = ++_workbookRefreshGeneration;
            bool knownHere = _workbookProposal != null && _workbookConversation == conversation;
            if (!knownHere)
            {
                _workbookProposal = null;
                _workbookConversation = null;
                WorkbookEditPanel.Visibility = Visibility.Collapsed;
            }
            if (string.IsNullOrWhiteSpace(conversation) || ChatSession.Current.IsBusy) return;
            try
            {
                var proposals = await DesktopIntegrationClient.Current.GetWorkbookEditsAsync(conversation, CancellationToken.None);
                if (generation != _workbookRefreshGeneration || conversation != ChatSession.Current.Conversation?.Id || ChatSession.Current.IsBusy) return;
                if (proposals.Count == 0)
                {
                    _workbookProposal = null;
                    _workbookConversation = null;
                    WorkbookEditPanel.Visibility = Visibility.Collapsed;
                    return;
                }
                _workbookProposal = proposals[0];
                _workbookConversation = conversation;
                RenderWorkbookEdit(_workbookProposal);
            }
            catch (Exception ex)
            {
                if (!knownHere || generation != _workbookRefreshGeneration || conversation != ChatSession.Current.Conversation?.Id) return;
                WorkbookEditStatus.Text = T("Could not refresh workbook preview: ", "Impossibile aggiornare l'anteprima del foglio: ") + ex.Message;
                WorkbookEditSave.IsEnabled = false;
                WorkbookEditDiscard.IsEnabled = false;
            }
        }

        private void RenderWorkbookEdit(WorkbookEditProposal proposal)
        {
            WorkbookEditFiles.Text = T("Source: ", "Origine: ") + proposal.SourceFileName + "\n"
                + T("Copy: ", "Copia: ") + proposal.OutputFileName;
            WorkbookEditCells.Children.Clear();
            foreach (WorkbookEditCellPreview cell in proposal.Cells)
            {
                WorkbookEditCells.Children.Add(new TextBlock
                {
                    Text = cell.Sheet + " · " + cell.Cell + "\n" + T("Before: ", "Prima: ") + cell.Before
                        + "\n" + T("After: ", "Dopo: ") + cell.After,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });
            }
            WorkbookEditValidation.Text = T("Formula calculation: ", "Calcolo formule: ") + DescribeWorkbookCalculation(proposal.CalculationStatus)
                + "\n" + (proposal.OpenXmlValidated ? T("Workbook structure validated", "Struttura del file verificata")
                    : T("Workbook structure not validated", "Struttura del file non verificata"))
                + (proposal.ValidationWarnings.Count > 0 ? "\n" + string.Join("\n", proposal.ValidationWarnings) : "");
            WorkbookEditStatus.Text = T("Review these changes before saving a copy.", "Controlla le modifiche prima di salvare una copia.");
            WorkbookEditSave.IsEnabled = !_workbookActionBusy && !ChatSession.Current.IsBusy;
            WorkbookEditDiscard.IsEnabled = !_workbookActionBusy && !ChatSession.Current.IsBusy;
            WorkbookEditPanel.Visibility = Visibility.Visible;
        }

        private static string DescribeWorkbookCalculation(string status) => status switch
        {
            "no-formulas" => T("No formulas", "Nessuna formula"),
            "cached-local-and-recalc-on-open" => T("Calculated locally. Recalculates on open", "Calcolate localmente. Ricalcolo all'apertura"),
            "contains-uncomputed-formulas-recalc-on-open" => T("Some formulas are not calculated. Recalculate on open", "Alcune formule non sono calcolate. Ricalcolo all'apertura"),
            "not-applicable" => T("Not applicable", "Non applicabile"),
            _ => status
        };

        private async void WorkbookEditSave_Click(object sender, RoutedEventArgs e) => await ApplyWorkbookEditAsync(true);
        private async void WorkbookEditDiscard_Click(object sender, RoutedEventArgs e) => await ApplyWorkbookEditAsync(false);

        private async Task ApplyWorkbookEditAsync(bool save)
        {
            WorkbookEditProposal? proposal = _workbookProposal;
            string? conversation = _workbookConversation;
            if (_workbookActionBusy || ChatSession.Current.IsBusy || proposal == null || conversation == null || conversation != ChatSession.Current.Conversation?.Id) return;
            _workbookActionBusy = true;
            int generation = ++_workbookRefreshGeneration;
            WorkbookEditSave.IsEnabled = false;
            WorkbookEditDiscard.IsEnabled = false;
            try
            {
                DocumentWriteResult? result = null;
                if (save) result = await DesktopIntegrationClient.Current.SaveWorkbookEditAsync(proposal.Id, conversation, CancellationToken.None);
                else await DesktopIntegrationClient.Current.DiscardWorkbookEditAsync(proposal.Id, conversation, CancellationToken.None);
                if (generation != _workbookRefreshGeneration || conversation != ChatSession.Current.Conversation?.Id) return;
                _workbookProposal = null;
                _workbookConversation = null;
                WorkbookEditCells.Children.Clear();
                WorkbookEditFiles.Text = "";
                WorkbookEditValidation.Text = "";
                WorkbookEditStatus.Text = save
                    ? T("Copy saved: ", "Copia salvata: ") + result!.Path
                    : T("Changes discarded.", "Modifiche scartate.");
            }
            catch (Exception ex)
            {
                if (generation == _workbookRefreshGeneration && conversation == ChatSession.Current.Conversation?.Id)
                    WorkbookEditStatus.Text = T("Workbook action failed: ", "Operazione sul foglio non riuscita: ") + ex.Message;
            }
            finally
            {
                _workbookActionBusy = false;
                if (generation != _workbookRefreshGeneration)
                    _ = RefreshWorkbookEditAsync();
                else if (_workbookProposal != null)
                {
                    WorkbookEditSave.IsEnabled = true;
                    WorkbookEditDiscard.IsEnabled = true;
                }
            }
        }
    }
}
