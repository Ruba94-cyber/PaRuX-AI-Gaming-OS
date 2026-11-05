using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RoleProfilePolicyTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static RoleModelCandidate M(string name, double gb, bool coding = false, bool reasoning = false, bool vision = false,
        bool projector = false, bool uncensored = false) =>
        new("C:\\models\\" + name + ".gguf", name, (long)(gb * Gb), coding, reasoning, vision, projector, uncensored);

    private static RuntimeManagerHardwareStatus Gpu(int vramGb) => new()
    {
        GpuDetected = true,
        VramTotalMib = vramGb * 1024,
        RamTotalBytes = 32 * Gb,
        GpuInventoryAvailable = true,
        GpuLargestDedicatedVideoMemoryBytes = vramGb * Gb,
        GpuDedicatedVideoMemoryTotalBytes = vramGb * Gb
    };

    private static RoleRecommendation Of(IReadOnlyList<RoleRecommendation> r, RoleProfile p) => r.Single(x => x.Profile == p);

    [Fact]
    public void Nothing_installed_means_no_model_everywhere_with_plain_words()
    {
        var r = RoleProfilePolicy.Recommend(new List<RoleModelCandidate>(), Gpu(16), false);
        Assert.Equal(5, r.Count);
        Assert.All(r, x => Assert.Equal(RoleRecommendationKind.NoModel, x.Kind));
        Assert.All(r, x => Assert.False(string.IsNullOrWhiteSpace(x.Explanation)));
    }

    [Fact]
    public void Chat_prefers_a_general_model_that_fits_over_a_bigger_one_that_does_not()
    {
        var models = new List<RoleModelCandidate> { M("small-general", 4), M("huge-general", 60), M("coder-7b-coder", 5, coding: true) };
        var r = RoleProfilePolicy.Recommend(models, Gpu(16), false);
        Assert.Equal("small-general", Of(r, RoleProfile.Chat).ModelName);
    }

    [Fact]
    public void Chat_never_defaults_to_an_uncensored_model()
    {
        var models = new List<RoleModelCandidate> { M("plain", 3), M("spicy-uncensored", 8, uncensored: true) };
        var r = RoleProfilePolicy.Recommend(models, Gpu(16), false);
        Assert.Equal("plain", Of(r, RoleProfile.Chat).ModelName);
    }

    [Fact]
    public void Coding_uses_the_specialist_when_installed_and_chat_otherwise()
    {
        var withCoder = RoleProfilePolicy.Recommend(new List<RoleModelCandidate> { M("gen", 8), M("x-coder", 6, coding: true) }, Gpu(16), false);
        var coding = Of(withCoder, RoleProfile.Coding);
        Assert.Equal(RoleRecommendationKind.Picked, coding.Kind);
        Assert.Equal("x-coder", coding.ModelName);
        Assert.Equal("coding", coding.RoleKey);

        var without = RoleProfilePolicy.Recommend(new List<RoleModelCandidate> { M("gen", 8) }, Gpu(16), false);
        Assert.Equal(RoleRecommendationKind.UsesChat, Of(without, RoleProfile.Coding).Kind);
        Assert.Equal("gen", Of(without, RoleProfile.Coding).ModelName);
    }

    [Fact]
    public void A_specialist_too_large_for_the_pc_falls_back_to_chat_and_says_so()
    {
        var r = RoleProfilePolicy.Recommend(new List<RoleModelCandidate> { M("gen", 4), M("big-r1", 80, reasoning: true) }, Gpu(8), false);
        var reasoning = Of(r, RoleProfile.Reasoning);
        Assert.Equal(RoleRecommendationKind.UsesChat, reasoning.Kind);
        Assert.Contains("too large", reasoning.Explanation);
    }

    [Fact]
    public void Vision_needs_a_model_with_its_projector_and_has_no_text_fallback()
    {
        var noProjector = RoleProfilePolicy.Recommend(new List<RoleModelCandidate> { M("gen", 4), M("x-vl-7b", 6, vision: true) }, Gpu(16), false);
        var v = Of(noProjector, RoleProfile.Vision);
        Assert.Equal(RoleRecommendationKind.NoModel, v.Kind);
        Assert.Contains("vision file", v.Explanation);

        var ok = RoleProfilePolicy.Recommend(new List<RoleModelCandidate> { M("gen", 4), M("x-vl-7b", 6, vision: true, projector: true) }, Gpu(16), false);
        Assert.Equal("x-vl-7b", Of(ok, RoleProfile.Vision).ModelName);
    }

    [Fact]
    public void Documents_follows_the_chat_model()
    {
        var r = RoleProfilePolicy.Recommend(new List<RoleModelCandidate> { M("gen", 4) }, Gpu(16), false);
        var docs = Of(r, RoleProfile.Documents);
        Assert.Equal(RoleRecommendationKind.UsesChat, docs.Kind);
        Assert.Equal(Of(r, RoleProfile.Chat).ModelPath, docs.ModelPath);
        Assert.Equal("standard", docs.RoleKey);
    }

    [Fact]
    public void Missing_files_are_never_recommended_and_unknown_hardware_still_picks()
    {
        var gone = M("ghost", 4) with { ExistsOnDisk = false };
        var r = RoleProfilePolicy.Recommend(new List<RoleModelCandidate> { gone, M("real", 2) }, null, false);
        Assert.Equal("real", Of(r, RoleProfile.Chat).ModelName);
    }

    [Fact]
    public void Italian_text_is_produced()
    {
        var r = RoleProfilePolicy.Recommend(new List<RoleModelCandidate>(), Gpu(8), true);
        Assert.Contains("modello", Of(r, RoleProfile.Chat).Explanation);
    }

    // ---- presenter ----

    private static RoleProfileBoard Board(List<RoleModelCandidate> models, List<RoleCurrentAssignment> current, bool advanced = false) =>
        RoleProfilePresenter.Build(RoleProfilePolicy.Recommend(models, Gpu(16), false), current, models, advanced, false);

    [Fact]
    public void Unset_roles_can_be_applied_and_apply_all_plans_only_those()
    {
        var models = new List<RoleModelCandidate> { M("gen", 8), M("x-coder", 6, coding: true) };
        var board = Board(models, new List<RoleCurrentAssignment>());
        Assert.True(board.CanApplyAll);
        var ops = RoleProfilePresenter.PlanApplyAll(board);
        Assert.Contains(ops, o => o.RoleKey == "standard" && o.ModelPath.EndsWith("gen.gguf"));
        Assert.Contains(ops, o => o.RoleKey == "coding" && o.ModelPath.EndsWith("x-coder.gguf"));
        Assert.DoesNotContain(ops, o => o.RoleKey == "vision");
    }

    [Fact]
    public void A_users_own_choice_is_never_replaced_by_apply_all_but_can_be_by_its_own_button()
    {
        var models = new List<RoleModelCandidate> { M("gen", 8), M("other", 3) };
        var current = new List<RoleCurrentAssignment> { new("standard", models[1].Path, IsUserChoice: true) };
        var board = Board(models, current);
        var chat = board.Rows.Single(r => r.Profile == RoleProfile.Chat);
        Assert.Equal(RoleRowState.YourChoice, chat.State);
        Assert.DoesNotContain(RoleProfilePresenter.PlanApplyAll(board), o => o.RoleKey == "standard");
        Assert.NotNull(RoleProfilePresenter.PlanApplyRow(chat));
    }

    [Fact]
    public void Matching_assignment_is_already_set_and_nothing_to_apply()
    {
        var models = new List<RoleModelCandidate> { M("gen", 8) };
        var current = new List<RoleCurrentAssignment> { new("standard", models[0].Path, false) };
        var board = Board(models, current);
        Assert.Equal(RoleRowState.Matches, board.Rows.Single(r => r.Profile == RoleProfile.Chat).State);
        Assert.False(board.CanApplyAll);
    }

    [Fact]
    public void Fallback_to_chat_does_not_freeze_a_role_override()
    {
        var board = Board(new List<RoleModelCandidate> { M("gen", 8) }, new List<RoleCurrentAssignment>());
        var coding = board.Rows.Single(r => r.Profile == RoleProfile.Coding);
        Assert.Equal(RoleRowState.Matches, coding.State);
        Assert.Null(RoleProfilePresenter.PlanApplyRow(coding));
    }

    [Fact]
    public void Advanced_override_lists_installed_models_and_validates_the_choice()
    {
        var models = new List<RoleModelCandidate> { M("gen", 8), M("x-vl", 6, vision: true, projector: true), M("txt", 2) };
        var simple = Board(models, new List<RoleCurrentAssignment>(), advanced: false);
        Assert.All(simple.Rows, r => Assert.Empty(r.AdvancedChoices));

        var adv = Board(models, new List<RoleCurrentAssignment>(), advanced: true);
        var chat = adv.Rows.Single(r => r.Profile == RoleProfile.Chat);
        Assert.Equal(3, chat.AdvancedChoices.Count);
        Assert.Single(adv.Rows.Single(r => r.Profile == RoleProfile.Vision).AdvancedChoices);   // only vision-capable
        Assert.NotNull(RoleProfilePresenter.PlanOverride(chat, models[2].Path));
        Assert.Null(RoleProfilePresenter.PlanOverride(chat, "C:\\not\\installed.gguf"));
        Assert.Null(RoleProfilePresenter.PlanOverride(adv.Rows.Single(r => r.Profile == RoleProfile.Documents), models[0].Path));
    }

    [Fact]
    public void Automation_ids_are_stable()
    {
        var board = Board(new List<RoleModelCandidate> { M("gen", 8) }, new List<RoleCurrentAssignment>());
        Assert.Equal("RoleProfile_Chat", board.Rows[0].AutomationPrefix);
        Assert.Equal("RoleProfile_Documents", board.Rows[4].AutomationPrefix);
        Assert.Equal("RoleProfile_Panel", RoleProfileBoard.PanelAutomationId);
        Assert.Equal("RoleProfile_ApplyRecommended", RoleProfileBoard.ApplyAllAutomationId);
    }

    [Fact]
    public void Empty_installation_summary_asks_to_install_a_model()
    {
        var board = Board(new List<RoleModelCandidate>(), new List<RoleCurrentAssignment>());
        Assert.Contains("Install a model", board.Summary);
        Assert.False(board.CanApplyAll);
    }
}
