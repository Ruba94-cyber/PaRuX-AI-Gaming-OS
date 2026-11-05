using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        /// <summary>
        /// Narrow, user-owned per-hunk review for ONE already tracked and modified
        /// text file. Nothing is applied until the second explicit confirmation.
        /// The full task must already be independently verified by Core, and the
        /// verified worktree receipt is rechecked immediately before application.
        /// A selected subset has not itself passed the full task's test suite.
        /// </summary>
        private async Task ReviewSingleFileHunksAsync(ChatMessage message, Button trigger)
        {
            if (string.IsNullOrWhiteSpace(message.CodeTaskId)) return;
            string taskId = message.CodeTaskId;
            trigger.IsEnabled = false;
            bool succeeded = false;
            try
            {
                (bool admitted, string? verified) =
                    await GetFreshCodingTaskApplyApprovalAsync(taskId);
                if (!admitted || string.IsNullOrWhiteSpace(verified))
                {
                    ShowTransientStatus(GameUi.T(
                        "Selective review requires a fresh verified Core task receipt.",
                        "La revisione selettiva richiede una verifica aggiornata del task da Core."));
                    return;
                }

                string? worktree = await ResolveTaskWorktreeAsync(taskId);
                if (string.IsNullOrWhiteSpace(worktree) || !Directory.Exists(worktree))
                {
                    ShowTransientStatus(GameUi.T("Task worktree is unavailable.",
                        "La cartella di lavoro del task non è disponibile."));
                    return;
                }
                AgentRepositorySetDto authority = await AgentOrchestratorClient.Current
                    .GetRepositorySetAsync(taskId, CancellationToken.None);
                if (authority.Repositories is null || authority.Repositories.Count != 1)
                {
                    ShowTransientStatus(GameUi.T(
                        "Selective hunks require exactly one Core-owned repository.",
                        "La revisione per blocchi richiede un solo repository autorizzato da Core."));
                    return;
                }
                string repositoryRoot = AgentRepositoryApplyAuthority.RequireSinglePrimaryRoot(authority, taskId);
                if (!Directory.Exists(repositoryRoot))
                {
                    ShowTransientStatus(GameUi.T("Original repository is unavailable.",
                        "Il repository originale non è disponibile."));
                    return;
                }
                bool shadow = ShadowWorkspaceService.IsShadowRepository(repositoryRoot);
                string? targetRoot = shadow
                    ? ShadowWorkspaceService.TryGetOriginalPath(repositoryRoot)
                    : repositoryRoot;
                if (string.IsNullOrWhiteSpace(targetRoot) || !Directory.Exists(targetRoot))
                {
                    ShowTransientStatus(GameUi.T("Original project folder is unavailable.",
                        "La cartella del progetto originale non è disponibile."));
                    return;
                }

                string? initialFingerprint = await FingerprintReviewedTaskDiffAsync(
                    authority, worktree, false, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(initialFingerprint) ||
                    !string.Equals(initialFingerprint, verified, StringComparison.Ordinal))
                {
                    ShowTransientStatus(GameUi.T("Task verification is stale. Re-run verification.",
                        "La verifica del task non è più valida. Ripeti i test."));
                    return;
                }
                IReadOnlyList<string>? changed = await CodingHunkReviewDiffReader
                    .ChangedPathsAsync(worktree, CancellationToken.None);
                if (changed is null || changed.Count == 0)
                {
                    ShowTransientStatus(GameUi.T(
                        "No bounded tracked changes are available for selective review.",
                        "Nessuna modifica tracciata disponibile per la revisione selettiva."));
                    return;
                }

                var filePicker = new ComboBox
                {
                    Header = GameUi.T("Modified tracked file", "File modificato e tracciato"),
                    MinWidth = 300, MaxWidth = 530
                };
                AutomationProperties.SetAutomationId(filePicker, "Chat_CodingTask_HunkFile");
                foreach (string file in changed)
                    filePicker.Items.Add(new ComboBoxItem { Content = file, Tag = file });
                filePicker.SelectedIndex = 0;
                var chooseFile = new ContentDialog
                {
                    XamlRoot = trigger.XamlRoot,
                    Title = GameUi.T("Review individual changes", "Rivedi singole modifiche"),
                    Content = new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = GameUi.T(
                                    "Only existing tracked UTF-8 files with ordinary Git text hunks are supported. New, deleted, binary or renamed files need whole-file review.",
                                    "Sono supportati solo file UTF-8 esistenti e tracciati con normali blocchi Git. File nuovi, eliminati, binari o rinominati richiedono la revisione completa."),
                                TextWrapping = TextWrapping.Wrap
                            },
                            filePicker
                        }
                    },
                    PrimaryButtonText = GameUi.T("Review hunks", "Rivedi i blocchi"),
                    CloseButtonText = GameUi.T("Cancel", "Annulla"),
                    DefaultButton = ContentDialogButton.Close
                };
                AutomationProperties.SetAutomationId(chooseFile, "Chat_CodingTask_HunkChooseDialog");
                if (await chooseFile.ShowAsync() != ContentDialogResult.Primary) return;
                if (filePicker.SelectedItem is not ComboBoxItem { Tag: string relativePath })
                    return;

                CodingHunkDiffRead fetched = await CodingHunkReviewDiffReader.ReadAsync(
                    worktree, relativePath, CancellationToken.None);
                if (!fetched.Ready || fetched.Diff is null
                    || fetched.Hunks.Count is < 1 or > 40)
                {
                    ShowTransientStatus(fetched.Ready
                        ? GameUi.T("Too many hunks for this dialog; use full-file review.",
                            "Troppi blocchi per questa finestra; usa la revisione completa.")
                        : fetched.Explanation);
                    return;
                }

                var hunkBoxes = new List<CheckBox>();
                var hunkStack = new StackPanel { Spacing = 14, MaxWidth = 740 };
                hunkStack.Children.Add(new TextBlock
                {
                    Text = GameUi.T(
                        "Choose the blocks to apply. Deselect rejected blocks. This is a partial result and must be tested separately after applying.",
                        "Seleziona i blocchi da applicare. Deseleziona quelli rifiutati. Il risultato parziale deve essere testato separatamente."),
                    TextWrapping = TextWrapping.Wrap
                });
                for (int index = 0; index < fetched.Hunks.Count; index++)
                {
                    CodingReviewHunk hunk = fetched.Hunks[index];
                    var checkbox = new CheckBox
                    {
                        IsChecked = true,
                        Content = GameUi.T("Apply block ", "Applica blocco ") + (index + 1)
                            + " · " + hunk.Header
                    };
                    AutomationProperties.SetAutomationId(checkbox, "Chat_CodingTask_Hunk_" + index);
                    hunkBoxes.Add(checkbox);
                    hunkStack.Children.Add(checkbox);
                    var body = new TextBlock
                    {
                        Text = hunk.Text,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        IsTextSelectionEnabled = true,
                        TextWrapping = TextWrapping.NoWrap
                    };
                    hunkStack.Children.Add(new ScrollViewer
                    {
                        Content = body, MaxHeight = 190,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    });
                }
                var reviewDialog = new ContentDialog
                {
                    XamlRoot = trigger.XamlRoot,
                    Title = GameUi.T("Choose exact code blocks", "Scegli i blocchi di codice"),
                    Content = new ScrollViewer
                    {
                        Content = hunkStack, MaxHeight = 460,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    },
                    PrimaryButtonText = GameUi.T("Preview selected result", "Anteprima risultato"),
                    CloseButtonText = GameUi.T("Cancel", "Annulla"),
                    DefaultButton = ContentDialogButton.Close
                };
                AutomationProperties.SetAutomationId(reviewDialog, "Chat_CodingTask_HunkReviewDialog");
                if (await reviewDialog.ShowAsync() != ContentDialogResult.Primary) return;

                CodingReviewChoice[] decisions = fetched.Hunks.Select((h, i) =>
                    new CodingReviewChoice(h.Id,
                        hunkBoxes[i].IsChecked == true
                            ? CodingHunkDecision.Accept : CodingHunkDecision.Reject)).ToArray();
                if (decisions.All(c => c.Decision != CodingHunkDecision.Accept))
                {
                    ShowTransientStatus(GameUi.T("No code blocks were accepted.",
                        "Nessun blocco di codice è stato accettato."));
                    return;
                }
                CodingHunkPreviewResult candidate = await Task.Run(() =>
                    CodingSelectiveHunkApplyService.PreparePreview(
                        targetRoot, relativePath, fetched.Diff, decisions));
                if (!candidate.Ready || candidate.CandidateSha256 is null)
                {
                    ShowTransientStatus(GameUi.T("Selective preview was refused: ",
                        "Anteprima selettiva rifiutata: ") + candidate.Explanation);
                    return;
                }

                // The approval is for exact bytes, not merely a digest.
                // Bound the display to avoid rendering megabytes of source text.
                string candidateText = candidate.CandidateText ?? string.Empty;
                string displayedCandidate = candidateText.Length > 4000
                    ? candidateText[..4000] + GameUi.T(
                        "\n... preview truncated; open the full file in the worktree for longer changes.",
                        "\n... anteprima abbreviata; apri il file completo per modifiche più lunghe.")
                    : candidateText;
                var confirmation = new ContentDialog
                {
                    XamlRoot = trigger.XamlRoot,
                    Title = GameUi.T("Apply only these approved blocks?",
                        "Applicare solo i blocchi approvati?"),
                    Content = new ScrollViewer
                    {
                        MaxHeight = 420,
                        Content = new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                            Text = GameUi.T(
                                "One file: ", "Un solo file: ") + relativePath
                                + "\n" + GameUi.T("Accepted blocks: ", "Blocchi accettati: ")
                                + decisions.Count(c => c.Decision == CodingHunkDecision.Accept)
                                + "/" + decisions.Length
                                + "\nSHA-256 before: " + candidate.PreimageSha256
                                + "\nSHA-256 after:  " + candidate.CandidateSha256
                                + "\n\n" + GameUi.T("Candidate text preview:", "Anteprima del testo risultante:")
                                + "\n" + displayedCandidate
                                + "\n\n" + GameUi.T(
                                    "This subset is different from the fully tested task. PaRuX will apply it as uncommitted local changes using a recovery journal. Re-run your tests afterward. No commit or push will happen.",
                                    "Questa selezione differisce dal task verificato nel suo insieme. PaRuX applicherà le modifiche locali senza commit, usando un journal di recupero. Riesegui i test dopo l'applicazione. Non verranno eseguiti commit o push.")
                        }
                    },
                    PrimaryButtonText = GameUi.T("Approve and apply", "Approva e applica"),
                    CloseButtonText = GameUi.T("Cancel", "Annulla"),
                    DefaultButton = ContentDialogButton.Close
                };
                AutomationProperties.SetAutomationId(confirmation, "Chat_CodingTask_HunkConfirmDialog");
                if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

                // No GUI-stored receipt or stale card may authorize a write.
                (bool nowApproved, string? nowVerified) =
                    await GetFreshCodingTaskApplyApprovalAsync(taskId);
                AgentRepositorySetDto currentAuthority = await AgentOrchestratorClient.Current
                    .GetRepositorySetAsync(taskId, CancellationToken.None);
                string currentRoot = AgentRepositoryApplyAuthority.RequireSinglePrimaryRoot(
                    currentAuthority, taskId);
                if (!nowApproved || !string.Equals(nowVerified, verified, StringComparison.Ordinal)
                    || !string.Equals(Path.GetFullPath(currentRoot),
                        Path.GetFullPath(repositoryRoot), StringComparison.OrdinalIgnoreCase))
                {
                    ShowTransientStatus(GameUi.T("Task approval or repository authority changed.",
                        "L'approvazione o l'autorizzazione del repository sono cambiate."));
                    return;
                }
                string? freshOriginal = ShadowWorkspaceService.IsShadowRepository(currentRoot)
                    ? ShadowWorkspaceService.TryGetOriginalPath(currentRoot)
                    : currentRoot;
                if (string.IsNullOrWhiteSpace(freshOriginal)
                    || !string.Equals(Path.GetFullPath(freshOriginal),
                        Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase))
                {
                    ShowTransientStatus(GameUi.T("Original workspace authority changed.",
                        "L'autorizzazione della cartella originale è cambiata."));
                    return;
                }
                string? newFingerprint = await FingerprintReviewedTaskDiffAsync(
                    currentAuthority, worktree, false, CancellationToken.None);
                CodingHunkDiffRead freshDiff = await CodingHunkReviewDiffReader.ReadAsync(
                    worktree, relativePath, CancellationToken.None);
                if (!string.Equals(newFingerprint, verified, StringComparison.Ordinal)
                    || !freshDiff.Ready || !string.Equals(freshDiff.Diff, fetched.Diff, StringComparison.Ordinal))
                {
                    ShowTransientStatus(GameUi.T(
                        "The task diff changed after review. Nothing was applied.",
                        "Il diff del task è cambiato dopo la revisione. Nessuna modifica applicata."));
                    return;
                }
                CodingHunkPreviewResult freshCandidate = await Task.Run(() =>
                    CodingSelectiveHunkApplyService.PreparePreview(
                        targetRoot, relativePath, fetched.Diff, decisions));
                if (!freshCandidate.Ready
                    || !string.Equals(freshCandidate.PreimageSha256,
                        candidate.PreimageSha256, StringComparison.Ordinal)
                    || !string.Equals(freshCandidate.CandidateSha256,
                        candidate.CandidateSha256, StringComparison.Ordinal))
                {
                    ShowTransientStatus(GameUi.T(
                        "The target file changed after confirmation. Nothing was applied.",
                        "Il file originale è cambiato dopo la conferma. Nessuna modifica applicata."));
                    return;
                }

                CodingSelectiveHunkApplyResult result = await Task.Run(() =>
                    CodingSelectiveHunkApplyService.Apply(
                        targetRoot, relativePath, fetched.Diff, decisions,
                        candidate.PreimageSha256, candidate.CandidateSha256,
                        explicitOneShotApproval: true));
                succeeded = result.Applied;
                ChatSession.Current.AppendAssistantNotice(
                    "**PaRuX selective code review** — " + result.Explanation
                    + "\n\n" + relativePath
                    + "\n\n" + GameUi.T("Selected subset must be tested after apply.",
                        "La selezione parziale deve essere testata dopo l'applicazione."));
                ShowTransientStatus(result.Applied
                    ? GameUi.T("Selected blocks applied. Re-run tests before continuing.",
                        "Blocchi selezionati applicati. Riesegui i test prima di continuare.")
                    : result.Explanation);
                if (!result.Applied && !string.IsNullOrWhiteSpace(result.UnresolvedRecoveryPath))
                    await ResolveUnresolvedShadowRecoveryAsync(
                        trigger.XamlRoot, targetRoot, result.UnresolvedRecoveryPath);
            }
            catch (Exception ex)
            {
                Log.Write("Selective hunk review refused: " + ex);
                ShowTransientStatus(GameUi.T("Selective review was not completed. No success is claimed.",
                    "Revisione selettiva non completata. Nessuna riuscita confermata."));
            }
            finally
            {
                trigger.IsEnabled = !succeeded;
                if (succeeded)
                    trigger.Content = GameUi.T("Selected hunks applied — run tests",
                        "Blocchi applicati — esegui i test");
            }
        }
    }
}
