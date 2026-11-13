using System.Net;
using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>SEC-F02/F12/F13: managed downloads fail closed without a digest, on a wrong digest and for pickle files.</summary>
public sealed class VerifiedDownloadSecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-verified-" + Guid.NewGuid().ToString("N"));

    public VerifiedDownloadSecurityTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeHandler(byte[] payload, Action? onRequest = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static readonly byte[] Payload = System.Text.Encoding.UTF8.GetBytes("synthetic checkpoint bytes");
    private const string Url = "https://huggingface.co/owner/repo/resolve/0123456789abcdef0123456789abcdef01234567/file.bin";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task A_missing_or_malformed_digest_refuses_before_any_request_is_sent(string? digest)
    {
        int requests = 0;
        using var http = new HttpClient(new FakeHandler(Payload, () => requests++));
        string target = Path.Combine(_dir, "model.bin");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            VerifiedDownload.DownloadAsync(http, Url, target, digest, Payload.Length, CancellationToken.None));
        Assert.Equal(0, requests);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task A_wrong_digest_discards_the_bytes_and_publishes_nothing()
    {
        using var http = new HttpClient(new FakeHandler(Payload));
        string target = Path.Combine(_dir, "model.bin");

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            VerifiedDownload.DownloadAsync(http, Url, target, new string('0', 64), Payload.Length, CancellationToken.None));
        Assert.Contains("SHA-256", error.Message);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task A_size_that_differs_from_the_pin_is_refused(int delta)
    {
        using var http = new HttpClient(new FakeHandler(Payload));
        string target = Path.Combine(_dir, "model.bin");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            VerifiedDownload.DownloadAsync(http, Url, target, Sha(Payload), Payload.Length + delta, CancellationToken.None));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public async Task A_non_https_source_is_refused()
    {
        using var http = new HttpClient(new FakeHandler(Payload));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            VerifiedDownload.DownloadAsync(http, Url.Replace("https:", "http:"), Path.Combine(_dir, "m.bin"),
                Sha(Payload), Payload.Length, CancellationToken.None));
    }

    [Fact]
    public async Task The_matching_digest_and_size_publish_the_file()
    {
        using var http = new HttpClient(new FakeHandler(Payload));
        string target = Path.Combine(_dir, "sub", "model.bin");

        await VerifiedDownload.DownloadAsync(http, Url, target, Sha(Payload).ToUpperInvariant(), Payload.Length, CancellationToken.None);
        Assert.Equal(Payload, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Theory]
    [InlineData("tiny.pt", true)]
    [InlineData("model.PTH", true)]
    [InlineData("weights.ckpt", true)]
    [InlineData("pytorch_model.bin", true)]
    [InlineData("data.pkl", true)]
    [InlineData("model.safetensors", false)]
    [InlineData("model.gguf", false)]
    public void Pickle_containers_are_recognised_by_extension(string name, bool pickle) =>
        Assert.Equal(pickle, VerifiedDownload.IsPickleFormat(name));

    [Fact]
    public void Avatar_checkpoints_are_pinned_to_an_immutable_commit_with_a_digest_and_size()
    {
        string unet = AvatarEngineService.CheckpointUrl("latentsync_unet.pt");
        Assert.DoesNotContain("/resolve/main/", unet);
        Assert.Contains("/resolve/" + AvatarEngineService.CheckpointRepositoryCommit + "/", unet);
        Assert.Equal(40, AvatarEngineService.CheckpointRepositoryCommit.Length);
        Assert.True(VerifiedDownload.IsValidSha256(AvatarEngineService.UnetSha256));
        Assert.True(VerifiedDownload.IsValidSha256(AvatarEngineService.WhisperTinySha256));
        Assert.True(AvatarEngineService.UnetBytes > 0 && AvatarEngineService.WhisperTinyBytes > 0);
    }

    private static MediaCompanionItem Item(string? sha256) => new()
    {
        Folder = "text_encoders", FileName = "te.safetensors", Repository = "owner/repo",
        Revision = new string('a', 40), Sha256 = sha256, PinnedSizeBytes = 4, Fetchable = true
    };

    [Fact]
    public void A_downloaded_companion_without_a_pinned_digest_is_discarded_not_installed()
    {
        string downloaded = Path.Combine(_dir, "te.safetensors");
        string target = Path.Combine(_dir, "comfy", "te.safetensors");
        File.WriteAllBytes(downloaded, new byte[4]);

        string? failure = MediaCompanionFetcher.MoveIntoPlace(Item(null), downloaded, target);

        Assert.Contains("no pinned SHA-256", failure);
        Assert.False(File.Exists(downloaded));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void A_downloaded_companion_with_a_wrong_digest_is_discarded_not_installed()
    {
        string downloaded = Path.Combine(_dir, "te.safetensors");
        string target = Path.Combine(_dir, "comfy", "te.safetensors");
        File.WriteAllBytes(downloaded, new byte[4]);

        string? failure = MediaCompanionFetcher.MoveIntoPlace(Item(new string('1', 64)), downloaded, target);

        Assert.Contains("does not match its pinned SHA-256", failure);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void A_downloaded_companion_with_the_right_digest_is_installed()
    {
        string downloaded = Path.Combine(_dir, "te.safetensors");
        string target = Path.Combine(_dir, "comfy", "te.safetensors");
        File.WriteAllBytes(downloaded, new byte[4]);

        Assert.Null(MediaCompanionFetcher.MoveIntoPlace(Item(Sha(new byte[4])), downloaded, target));
        Assert.True(File.Exists(target));
    }

    private static string RegistryWith(string fileName, string source) => $$"""
        {
          "schemaVersion": 1,
          "embeddedStems": { "textEncoder": ["text_encoders."], "vae": ["vae."] },
          "families": [{
            "id": "demo", "displayName": "Demo", "support": "supported", "pipelines": ["image"],
            "templateFile": "z-image-smoke-api.json",
            "mainLoader": { "node": "1", "classType": "UNETLoader", "input": "unet_name", "folder": "diffusion_models" },
            "requiredNodes": ["UNETLoader"], "signature": { "requiredStems": ["a."] },
            "companions": [{ "role": "text_encoder", "folder": "text_encoders", "fileName": "{{fileName}}", "clipType": "demo", "source": {{source}} }]
          }]
        }
        """;

    private static readonly string PinnedSource =
        "{ \"repository\": \"owner/repo\", \"path\": \"te.bin\", \"revision\": \"" + new string('a', 40)
        + "\", \"sizeBytes\": 10, \"sha256\": \"" + new string('b', 64) + "\" }";

    [Theory]
    [InlineData("te.pt")]
    [InlineData("te.pth")]
    public void A_pickle_companion_with_a_downloadable_source_is_refused_by_the_registry(string fileName)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => MediaCompanionRegistry.Parse(RegistryWith(fileName, PinnedSource)));
        Assert.Contains("pickle", error.Message);
    }

    [Fact]
    public void A_pickle_companion_without_a_source_stays_a_manual_install()
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Parse(RegistryWith("te.pt", "{}"));
        Assert.Equal("te.pt", Assert.Single(Assert.Single(registry.Families).Companions).FileName);
    }

    [Fact]
    public void A_safetensors_companion_with_a_pinned_source_is_still_accepted()
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Parse(RegistryWith("te.safetensors", PinnedSource));
        Assert.True(Assert.Single(Assert.Single(registry.Families).Companions).Source.IsPinned);
    }

    [Fact]
    public void The_shipped_registry_offers_no_downloadable_pickle_file()
    {
        Assert.DoesNotContain(MediaCompanionRegistry.Shipped.Families.SelectMany(f => f.Companions),
            c => c.Source.IsPinned && VerifiedDownload.IsPickleFormat(c.FileName));
    }
}
