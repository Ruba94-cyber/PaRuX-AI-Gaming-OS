using System;
using System.IO;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingRunPermissionModePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-coding-mode-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Root_mode_survives_task_store_reload_and_child_inherits_it()
    {
        var store = new TaskStore(_directory);
        AgentTaskDto root = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Implement a coding task",
            Role = "coding",
            CodingRunPermissionMode = CodingRunPermissionModes.Ask
        });

        Assert.Equal(CodingRunPermissionModes.Ask, root.CodingRunPermissionMode);
        Assert.Equal(CodingRunPermissionModes.Ask, new TaskStore(_directory).Get(root.Id)!.CodingRunPermissionMode);

        AgentTaskDto child = store.Create(new CreateAgentTaskRequest
        {
            ParentId = root.Id,
            Goal = "Implement a bounded delegated change"
        });
        Assert.Equal(CodingRunPermissionModes.Ask, child.CodingRunPermissionMode);

        Assert.Throws<InvalidDataException>(() => store.Create(new CreateAgentTaskRequest
        {
            ParentId = root.Id,
            Goal = "A child cannot weaken Ask mode",
            CodingRunPermissionMode = CodingRunPermissionModes.Bypass
        }));
    }

    [Fact]
    public void New_task_without_a_mode_defaults_to_ask_and_unknown_mode_fails_closed_to_ask()
    {
        var store = new TaskStore(_directory);
        AgentTaskDto fresh = store.Create(new CreateAgentTaskRequest { Goal = "Fresh task" });
        AgentTaskDto unknown = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Unknown mode task",
            CodingRunPermissionMode = "unexpected"
        });

        Assert.Equal(CodingRunPermissionModes.Ask, fresh.CodingRunPermissionMode);
        Assert.Equal(CodingRunPermissionModes.Ask, new TaskStore(_directory).Get(fresh.Id)!.CodingRunPermissionMode);
        Assert.Equal(CodingRunPermissionModes.Ask, unknown.CodingRunPermissionMode);
    }

    [Fact]
    public void Explicit_bypass_on_a_new_task_is_honoured_and_persisted()
    {
        var store = new TaskStore(_directory);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Explicit bypass",
            CodingRunPermissionMode = CodingRunPermissionModes.Bypass
        });

        Assert.Equal(CodingRunPermissionModes.Bypass, task.CodingRunPermissionMode);
        Assert.Equal(CodingRunPermissionModes.Bypass, new TaskStore(_directory).Get(task.Id)!.CodingRunPermissionMode);
    }

    [Fact]
    public void Legacy_row_without_a_stored_mode_still_resolves_to_bypass()
    {
        var store = new TaskStore(_directory);
        AgentTaskDto anchor = store.Create(new CreateAgentTaskRequest { Goal = "Creates the schema" });
        Assert.Equal(CodingRunPermissionModes.Ask, anchor.CodingRunPermissionMode);

        // A row written before the column existed: the INSERT does not name the column, so the
        // backfill DEFAULT 'bypass' applies exactly as it did for rows migrated by ALTER TABLE.
        string legacyId = Guid.NewGuid().ToString("N");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=" + Path.Combine(_directory, "orchestrator.db") + ";Mode=ReadWrite;Pooling=False"))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO agent_tasks (id, goal, status, created_utc, updated_utc) VALUES ($id, 'Legacy', 'Queued', $now, $now);";
            insert.Parameters.AddWithValue("$id", legacyId);
            insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        Assert.Equal(CodingRunPermissionModes.Bypass, new TaskStore(_directory).Get(legacyId)!.CodingRunPermissionMode);
        // The new task keeps its own value; the legacy backfill does not leak into new rows.
        Assert.Equal(CodingRunPermissionModes.Ask, new TaskStore(_directory).Get(anchor.Id)!.CodingRunPermissionMode);
    }

    [Fact]
    public void Conversation_defaults_to_ask_when_new_and_bypass_when_a_stored_document_lacks_the_field()
    {
        Assert.Equal(CodingRunPermissionModes.Ask, new PaRuX.Models.Conversation().CodingRunPermissionMode);

        var legacy = System.Text.Json.JsonSerializer.Deserialize<PaRuX.Models.Conversation>(
            "{\"Id\":\"legacy\",\"Title\":\"Old chat\"}")!;
        Assert.Equal(CodingRunPermissionModes.Bypass, legacy.CodingRunPermissionMode);
        Assert.Equal("legacy", legacy.Id);

        var explicitBypass = System.Text.Json.JsonSerializer.Deserialize<PaRuX.Models.Conversation>(
            "{\"CodingRunPermissionMode\":\"bypass\"}")!;
        var explicitAsk = System.Text.Json.JsonSerializer.Deserialize<PaRuX.Models.Conversation>(
            "{\"CodingRunPermissionMode\":\"ask\"}")!;
        Assert.Equal(CodingRunPermissionModes.Bypass, explicitBypass.CodingRunPermissionMode);
        Assert.Equal(CodingRunPermissionModes.Ask, explicitAsk.CodingRunPermissionMode);

        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<PaRuX.Models.Conversation>(
            System.Text.Json.JsonSerializer.Serialize(new PaRuX.Models.Conversation()))!;
        Assert.Equal(CodingRunPermissionModes.Ask, roundTrip.CodingRunPermissionMode);
    }

    [Fact]
    public void Contract_helpers_separate_default_for_new_from_stored_missing()
    {
        Assert.Equal(CodingRunPermissionModes.Ask, CodingRunPermissionModes.Default);
        Assert.Equal(CodingRunPermissionModes.Ask, CodingRunPermissionModes.NormalizeRequested(null));
        Assert.Equal(CodingRunPermissionModes.Ask, new CreateDesktopCodingTaskRequest().CodingRunPermissionMode);
        Assert.Equal(CodingRunPermissionModes.Bypass, CodingRunPermissionModes.NormalizeRequested(" Bypass "));
        Assert.Equal(CodingRunPermissionModes.Bypass, CodingRunPermissionModes.NormalizeStored(null));
        Assert.Equal(CodingRunPermissionModes.Bypass, CodingRunPermissionModes.NormalizeStored(""));
        Assert.Equal(CodingRunPermissionModes.Ask, CodingRunPermissionModes.NormalizeStored("unexpected"));
        Assert.Equal(CodingRunPermissionModes.Plan, CodingRunPermissionModes.NormalizeStored("PLAN"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { }
    }
}
