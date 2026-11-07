using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatMediaJobReconcilerTests
{
    private static ChatMessage Running(string kind = "image", string? id = "job-1", bool chain = false)
    {
        var m = new ChatMessage { Role = "assistant" };
        ChatMediaJobReconciler.BeginStep(m, kind, chain);
        if (id != null) ChatMediaJobReconciler.SetJobId(m, id);
        return m;
    }

    private static MediaJobStatus Job(string status, string id = "job-1", string? error = null, MediaAsset? asset = null) =>
        new() { Id = id, Status = status, Stage = "Sampling", ProgressPercent = 42, Error = error, Asset = asset };

    [Fact]
    public void OldChatWithoutMediaFieldsIsNotReconciled()
    {
        var m = new ChatMessage { Role = "assistant", Content = "hello" };
        Assert.False(ChatMediaJobReconciler.NeedsReconcile(m));
        Assert.Equal(ChatMediaReconcileAction.None, ChatMediaJobReconciler.Reconcile(m, null, true));
        Assert.False(ChatMediaJobReconciler.Present(m).ShowRetry);
    }

    [Fact]
    public void RunningJobReattachesAndShowsProgress()
    {
        var m = Running();
        Assert.Equal(ChatMediaReconcileAction.Reattach, ChatMediaJobReconciler.Reconcile(m, Job("running"), true));
        ChatMediaPresentation p = ChatMediaJobReconciler.Present(m);
        Assert.True(p.ShowProgress);
        Assert.Contains("42%", p.ProgressText);
        Assert.False(p.ShowRetry);
    }

    [Fact]
    public void Cancelling_a_video_is_a_transient_state_not_an_unknown_failure()
    {
        var reply = Running(kind: "video");
        var job = Job("cancelling");
        job.Stage = "Stop pending confirmation";
        Assert.Equal(ChatMediaReconcileAction.Reattach,
            ChatMediaJobReconciler.Reconcile(reply, job, serverReachable: true));
        Assert.Equal("running", reply.MediaJobState);
        Assert.False(reply.IsError);
        Assert.Contains("Stop pending confirmation", ChatMediaJobReconciler.Present(reply).ProgressText);
        Assert.False(ChatMediaJobReconciler.Present(reply).ShowRetry);

        Assert.Equal(ChatMediaReconcileAction.Settled,
            ChatMediaJobReconciler.Reconcile(reply, Job("cancelled"), serverReachable: true));
        Assert.Equal("cancelled", reply.MediaJobState);
        Assert.True(ChatMediaJobReconciler.Present(reply).ShowRetry);
    }

    [Fact]
    public void Only_engine_reported_plausible_eta_is_displayed_and_resets_between_steps()
    {
        var reply = Running(kind: "image");
        var active = Job("running");
        active.EtaSeconds = 93;
        ChatMediaJobReconciler.ReportProgress(reply, active);
        Assert.Equal(93, reply.MediaEtaSeconds);
        Assert.Contains("approx. 1m 33s remaining", ChatMediaJobReconciler.Present(reply).ProgressText);

        active.EtaSeconds = -4;
        ChatMediaJobReconciler.ReportProgress(reply, active);
        Assert.Null(reply.MediaEtaSeconds);
        Assert.DoesNotContain("remaining", ChatMediaJobReconciler.Present(reply).ProgressText);

        active.EtaSeconds = 90001;
        ChatMediaJobReconciler.ReportProgress(reply, active);
        Assert.Null(reply.MediaEtaSeconds);

        active.EtaSeconds = 21;
        active.Status = "queued";
        ChatMediaJobReconciler.ReportProgress(reply, active);
        Assert.Null(reply.MediaEtaSeconds);

        active.Status = "running";
        ChatMediaJobReconciler.ReportProgress(reply, active);
        Assert.Equal(21, reply.MediaEtaSeconds);
        ChatMediaJobReconciler.BeginStep(reply, "video", true);
        Assert.Null(reply.MediaEtaSeconds);
        Assert.Equal("running", reply.MediaJobState);
    }

    [Fact]
    public void UnreachableServerDefersWithoutLosingTheJob()
    {
        var m = Running();
        Assert.Equal(ChatMediaReconcileAction.Defer, ChatMediaJobReconciler.Reconcile(m, null, false));
        Assert.Equal("running", m.MediaJobState);
    }

    [Fact]
    public void CompletedJobAttachesAssetExactlyOnce()
    {
        var m = Running();
        var asset = new MediaAsset { FileName = "a.png", MediaType = "image/png" };
        Assert.Equal(ChatMediaReconcileAction.AttachAsset, ChatMediaJobReconciler.Reconcile(m, Job("completed", asset: asset), true));
        var saved = new ChatAttachment { FileName = "a.png", MediaType = "image/png", LocalPath = "x.png" };
        ChatMediaJobReconciler.ApplyCompleted(m, saved, "job-1", "Generated locally");
        ChatMediaJobReconciler.ApplyCompleted(m, new ChatAttachment { MediaType = "image/png" }, "job-1", "Generated locally");
        Assert.Single(m.Attachments);
        Assert.Equal("completed", m.MediaJobState);
        Assert.Equal(ChatMediaReconcileAction.None, ChatMediaJobReconciler.Reconcile(m, Job("completed", asset: asset), true));
    }

    [Fact]
    public void CompletedJobAlreadyAttachedSettlesWithoutDuplicate()
    {
        var m = Running();
        m.Attachments.Add(new ChatAttachment { MediaType = "image/png", MediaJobId = "job-1" });
        Assert.Equal(ChatMediaReconcileAction.Settled,
            ChatMediaJobReconciler.Reconcile(m, Job("completed", asset: new MediaAsset()), true));
        Assert.Single(m.Attachments);
        Assert.Equal("completed", m.MediaJobState);
    }

    [Fact]
    public void FailedJobOffersRetryAndIsHonest()
    {
        var m = Running();
        Assert.Equal(ChatMediaReconcileAction.Settled, ChatMediaJobReconciler.Reconcile(m, Job("failed", error: "out of memory"), true));
        Assert.True(m.IsError);
        Assert.Contains("out of memory", m.Content);
        ChatMediaPresentation p = ChatMediaJobReconciler.Present(m);
        Assert.True(p.ShowRetry);
        Assert.Equal("Retry", p.RetryLabel);
    }

    [Fact]
    public void CancelledJobIsLabelledNotBlank()
    {
        var m = Running();
        Assert.Equal(ChatMediaReconcileAction.Settled, ChatMediaJobReconciler.Reconcile(m, Job("cancelled"), true));
        Assert.Equal("cancelled", m.MediaJobState);
        Assert.False(string.IsNullOrWhiteSpace(m.Content));
        Assert.Contains("cancelled", m.Content);
        ChatMediaPresentation p = ChatMediaJobReconciler.Present(m);
        Assert.Equal(ChatMediaJobReconciler.CancelledText, p.CancelledText);
        Assert.True(p.ShowRetry);
    }

    [Fact]
    public void JobUnknownToServerOrNeverCreatedSettlesAsFailedWithRetry()
    {
        var unknown = Running();
        Assert.Equal(ChatMediaReconcileAction.Settled, ChatMediaJobReconciler.Reconcile(unknown, null, true));
        Assert.Equal("failed", unknown.MediaJobState);
        Assert.True(ChatMediaJobReconciler.Present(unknown).ShowRetry);

        var noId = Running(id: null);
        Assert.Equal(ChatMediaReconcileAction.Settled, ChatMediaJobReconciler.Reconcile(noId, Job("running"), true));
        Assert.Equal("failed", noId.MediaJobState);
    }

    [Fact]
    public void ChainedVideoFailureKeepsImageAndOffersRetryVideo()
    {
        var m = Running(chain: true);
        ChatMediaJobReconciler.ApplyCompleted(m, new ChatAttachment { MediaType = "image/png", LocalPath = "i.png" }, "job-1", "Generated locally");
        ChatMediaJobReconciler.BeginStep(m, "video", true);
        ChatMediaJobReconciler.SetJobId(m, "job-2");
        ChatMediaJobReconciler.ApplyFailure(m, "video model missing");

        Assert.Single(m.Attachments);
        Assert.False(m.IsError);
        Assert.Contains("Generated locally", m.Content);
        Assert.Contains("video model missing", m.Content);
        ChatMediaPresentation p = ChatMediaJobReconciler.Present(m);
        Assert.True(p.ShowRetry);
        Assert.Equal("Retry video", p.RetryLabel);
    }

    [Fact]
    public void RetryVideoPlansOnlyVideoFromExistingImageAndDoesNotDuplicate()
    {
        var user = new ChatMessage { Role = "user", Content = "genera un'immagine di un gatto e poi animala" };
        var reply = Running(chain: true);
        ChatMediaJobReconciler.ApplyCompleted(reply, new ChatAttachment { MediaType = "image/png", LocalPath = "i.png" }, "job-1", "Generated locally");
        ChatMediaJobReconciler.BeginStep(reply, "video", true);
        ChatMediaJobReconciler.ApplyFailure(reply, "boom");
        var messages = new List<ChatMessage> { user, reply };

        ChatMediaRetryPlan? plan = ChatMediaRetryPlanner.Plan(messages, reply);
        Assert.NotNull(plan);
        Assert.True(plan!.RunVideo);
        Assert.False(plan.RunImage);
        Assert.Equal("i.png", plan.SourceImagePath);
        Assert.Equal(user.Content, plan.Prompt);
        Assert.Equal(-1, plan.Seed);

        ChatMediaRetryPlanner.PrepareRetry(reply, "video", true);
        ChatMediaJobReconciler.SetJobId(reply, "job-3");
        Assert.Equal("running", reply.MediaJobState);
        Assert.False(reply.IsError);
        ChatMediaJobReconciler.ApplyCompleted(reply, new ChatAttachment { MediaType = "video/mp4", LocalPath = "v.mp4" }, "job-3", "Generated locally video");
        Assert.Equal(2, reply.Attachments.Count);
        Assert.Single(reply.Attachments, a => a.MediaType == "image/png");
        Assert.False(reply.MediaChainVideoPending);
        Assert.False(ChatMediaJobReconciler.Present(reply).ShowRetry);
    }

    [Fact]
    public void RetryOfFailedImageReusesPromptAndChainsVideoWhenRequested()
    {
        var user = new ChatMessage { Role = "user", Content = "generate an image of a fox and then animate it" };
        var reply = Running(chain: true);
        ChatMediaJobReconciler.ApplyFailure(reply, "no GPU");
        ChatMediaRetryPlan? plan = ChatMediaRetryPlanner.Plan(new[] { user, reply }, reply);
        Assert.NotNull(plan);
        Assert.True(plan!.RunImage);
        Assert.True(plan.RunVideo);
        Assert.Null(plan.SourceImagePath);
    }

    [Fact]
    public void RunningOrNonMediaRepliesAreNotRetryable()
    {
        var user = new ChatMessage { Role = "user", Content = "x" };
        var running = Running();
        Assert.Null(ChatMediaRetryPlanner.Plan(new[] { user, running }, running));
        var text = new ChatMessage { Role = "assistant", Content = "hi" };
        Assert.Null(ChatMediaRetryPlanner.Plan(new[] { user, text }, text));
    }

    [Fact]
    public void CancelledChainAfterImageKeepsImage()
    {
        var m = Running(chain: true);
        ChatMediaJobReconciler.ApplyCompleted(m, new ChatAttachment { MediaType = "image/png", LocalPath = "i.png" }, "job-1", "Generated locally");
        ChatMediaJobReconciler.BeginStep(m, "video", true);
        ChatMediaJobReconciler.ApplyCancelled(m);
        Assert.Single(m.Attachments);
        Assert.Contains("cancelled", m.Content);
        Assert.Equal("Retry video", ChatMediaJobReconciler.Present(m).RetryLabel);
    }

    [Fact]
    public void ChatMessageRoundTripsAndOldJsonLoads()
    {
        var m = Running();
        ChatMediaJobReconciler.ReportProgress(m, new MediaJobStatus
        {
            Id = "job-1", Status = "running", ProgressPercent = 17,
            Stage = "Sampling", EtaSeconds = 82
        });
        string json = System.Text.Json.JsonSerializer.Serialize(m);
        ChatMessage? back = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(json);
        Assert.Equal("job-1", back!.MediaJobId);
        Assert.Equal(82, back.MediaEtaSeconds);
        Assert.Contains("approx. 1m 22s remaining", ChatMediaJobReconciler.Present(back).ProgressText);

        ChatMediaJobReconciler.ApplyCompleted(m, new ChatAttachment { MediaType = "image/png" },
            "job-1", "Image saved");
        Assert.Null(m.MediaEtaSeconds);
        ChatMessage? old = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>("{\"Role\":\"assistant\",\"Content\":\"x\"}");
        Assert.Null(old!.MediaJobState);
        Assert.Null(old.MediaEtaSeconds);
    }
}
