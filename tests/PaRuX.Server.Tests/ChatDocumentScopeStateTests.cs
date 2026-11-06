using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>No WinUI, model or process: exact per-turn source selection policy.</summary>
public sealed class ChatDocumentScopeStateTests
{
    [Fact]
    public void Guide_scope_replaces_only_explicit_sources_and_enables_documents()
    {
        try
        {
            ChatDocumentScopeState.SetEnabled(false);
            Assert.True(ChatDocumentScopeState.TryUseExplicitSources(
                new[] { "guide-one", "guide-two", "guide-one" }));
            var selected = ChatDocumentScopeState.ToRequest();
            Assert.NotNull(selected);
            Assert.True(selected!.Enabled);
            Assert.Equal(new[] { "guide-one", "guide-two" },
                selected.SourceIds.OrderBy(x => x, StringComparer.Ordinal));
        }
        finally { ChatDocumentScopeState.SetEnabled(false); }
    }

    [Fact]
    public void Refused_empty_or_invalid_guide_scope_cannot_broaden_to_all()
    {
        try
        {
            ChatDocumentScopeState.SetEnabled(false);
            Assert.True(ChatDocumentScopeState.TryUseExplicitSources(new[] { "approved" }));
            Assert.False(ChatDocumentScopeState.TryUseExplicitSources(Array.Empty<string>()));
            Assert.False(ChatDocumentScopeState.TryUseExplicitSources(new[] { "invalid\nsource" }));
            Assert.False(ChatDocumentScopeState.TryUseExplicitSources(
                Enumerable.Range(0, 65).Select(i => "guide-" + i).ToArray()));
            Assert.Equal(new[] { "approved" }, ChatDocumentScopeState.ToRequest()!.SourceIds);
        }
        finally { ChatDocumentScopeState.SetEnabled(false); }
    }
    [Fact]
    public void Unchecking_final_explicit_folder_never_silently_expands_to_all_sources()
    {
        try
        {
            ChatDocumentScopeState.SetEnabled(false);
            Assert.True(ChatDocumentScopeState.TryUseExplicitSources(new[] { "invoice-folder" }));
            Assert.True(ChatDocumentScopeState.Enabled);
            ChatDocumentScopeState.SetSource("invoice-folder", false);
            Assert.False(ChatDocumentScopeState.Enabled);
            Assert.Null(ChatDocumentScopeState.ToRequest());

            // The user can still explicitly turn document mode back on to use
            // all enabled sources, retaining the original intentional UX.
            ChatDocumentScopeState.SetEnabled(true);
            Assert.Empty(ChatDocumentScopeState.ToRequest()!.SourceIds);
        }
        finally { ChatDocumentScopeState.SetEnabled(false); }
    }

    [Fact]
    public void Removing_or_disabling_selected_folder_fails_closed_but_surviving_selection_stays_scoped()
    {
        try
        {
            ChatDocumentScopeState.SetEnabled(false);
            Assert.True(ChatDocumentScopeState.TryUseExplicitSources(new[] { "finance", "legal" }));
            Assert.False(ChatDocumentScopeState.Prune(new[] { "legal", "other" }));
            Assert.Equal(new[] { "legal" }, ChatDocumentScopeState.ToRequest()!.SourceIds);
            Assert.True(ChatDocumentScopeState.Prune(new[] { "other" }));
            Assert.False(ChatDocumentScopeState.Enabled);
            Assert.Null(ChatDocumentScopeState.ToRequest());
        }
        finally { ChatDocumentScopeState.SetEnabled(false); }
    }

}
