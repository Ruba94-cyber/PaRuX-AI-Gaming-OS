using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamePackageStageTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(),
        "parux-stage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Valid_external_digest_stages_one_byte_exact_copy_without_executing()
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, "fixture.zip");
            byte[] data = "download fixture only; never execute"u8.ToArray();
            await File.WriteAllBytesAsync(input, data);
            string digest = Convert.ToHexString(SHA256.HashData(data));
            var stage = new GamePackageStageService(() => false,
                () => CancellationToken.None, Path.Combine(root, "private-staging"));
            var result = await stage.StageAsync(new GamePackageStageRequest(input, digest), CancellationToken.None);
            Assert.Equal(data, await File.ReadAllBytesAsync(result.StagedPath));
            Assert.Equal("fixture.zip", result.FileName);
            Assert.Equal(data.Length, result.SizeBytes);
            Assert.Equal(digest.ToLowerInvariant(), result.Sha256);
            Assert.Equal(data, await File.ReadAllBytesAsync(input));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(result.StagedPath)!));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Wrong_digest_leaves_no_published_file_or_partial()
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, "fixture.msi");
            await File.WriteAllTextAsync(input, "fixture");
            string staged = Path.Combine(root, "staging");
            var service = new GamePackageStageService(() => false, () => CancellationToken.None, staged);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.StageAsync(new GamePackageStageRequest(input, new string('0', 64)), CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(staged));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("deadbeef")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Invalid_publisher_hash_is_never_accepted(string hash) =>
        Assert.False(GamePackageStageService.IsPublisherHash(hash));

    [Fact]
    public async Task Gaming_refuses_staging_before_creating_a_directory()
    {
        string root = NewRoot();
        var service = new GamePackageStageService(() => true,
            () => CancellationToken.None, root);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StageAsync(new GamePackageStageRequest("not-a-file.zip", new string('a', 64)),
                CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Cancelled_staging_does_not_leave_final_artifacts()
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "fixture.7z");
            await File.WriteAllBytesAsync(source, new byte[65536]);
            using var gaming = new CancellationTokenSource();
            gaming.Cancel();
            var service = new GamePackageStageService(() => false,
                () => gaming.Token, Path.Combine(root, "staging"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.StageAsync(new GamePackageStageRequest(source,
                    Convert.ToHexString(SHA256.HashData(new byte[65536]))), CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "staging")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Staging_quota_rejects_another_package_without_deleting_existing_one()
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "fixture.zip");
            byte[] bytes = "a small but complete fixture"u8.ToArray();
            await File.WriteAllBytesAsync(source, bytes);
            string digest = Convert.ToHexString(SHA256.HashData(bytes));
            var stage = new GamePackageStageService(
                () => false, () => CancellationToken.None,
                Path.Combine(root, "staging"), bytes.Length + 5);
            var first = await stage.StageAsync(
                new GamePackageStageRequest(source, digest), CancellationToken.None);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                stage.StageAsync(new GamePackageStageRequest(source, digest), CancellationToken.None));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(first.StagedPath));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(first.StagedPath)!, "game-*.zip"));
        }
        finally { Directory.Delete(root, true); }
    }


}
