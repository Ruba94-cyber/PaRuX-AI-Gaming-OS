using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamePackageStageTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(),
        "parux-stage-" + Guid.NewGuid().ToString("N"));

    // The persistent .parux-staging.lock is intentional infrastructure, not a package or temp file.
    private static string[] NonLockFiles(string dir) => Directory.EnumerateFiles(dir)
        .Where(f => !string.Equals(Path.GetFileName(f), ".parux-staging.lock", StringComparison.Ordinal))
        .ToArray();

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
            Assert.Single(NonLockFiles(Path.GetDirectoryName(result.StagedPath)!));
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
            Assert.Empty(NonLockFiles(staged));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Old_exact_partial_is_reclaimed_under_lease_but_foreign_file_survives()
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, "fixture.zip");
            byte[] content = "verified bytes"u8.ToArray();
            await File.WriteAllBytesAsync(input, content);
            string staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);
            string partial = Path.Combine(staging, ".parux-game-" + Guid.NewGuid().ToString("N") + ".partial");
            await File.WriteAllBytesAsync(partial, new byte[4096]);
            File.SetLastWriteTimeUtc(partial, DateTime.UtcNow.AddDays(-2));

            string foreign = Path.Combine(staging, "foreign.partial");
            await File.WriteAllTextAsync(foreign, "keep");
            File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-2));

            // Without reclamation, the orphan exceeds this staging quota.
            var service = new GamePackageStageService(() => false,
                () => CancellationToken.None, staging, maxTotalStagedBytes: 128);
            var result = await service.StageAsync(new GamePackageStageRequest(
                input, Convert.ToHexString(SHA256.HashData(content))), CancellationToken.None);

            Assert.False(File.Exists(partial));
            Assert.Equal("keep", await File.ReadAllTextAsync(foreign));
            Assert.Equal(content, await File.ReadAllBytesAsync(result.StagedPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Recent_partial_is_retained_and_still_reserves_quota()
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, "fixture.zip");
            byte[] content = "source"u8.ToArray();
            await File.WriteAllBytesAsync(input, content);
            string staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);
            string partial = Path.Combine(staging, ".parux-game-" + Guid.NewGuid().ToString("N") + ".partial");
            await File.WriteAllBytesAsync(partial, new byte[256]);
            var service = new GamePackageStageService(() => false,
                () => CancellationToken.None, staging, maxTotalStagedBytes: 128);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.StageAsync(new GamePackageStageRequest(
                    input, Convert.ToHexString(SHA256.HashData(content))), CancellationToken.None));
            Assert.Equal(256L, new FileInfo(partial).Length);
            Assert.Empty(Directory.GetFiles(staging, "game-*.zip"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Gaming_transition_cancels_stage_while_another_process_holds_the_lease()
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "fixture.zip");
            byte[] content = "source"u8.ToArray();
            await File.WriteAllBytesAsync(source, content);
            string staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);
            using var held = new FileStream(Path.Combine(staging, GamePackageStageService.LockFileName),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var gaming = new CancellationTokenSource();
            var stage = new GamePackageStageService(
                () => gaming.IsCancellationRequested, () => gaming.Token, staging);
            Task<GamePackageStageResult> pending = stage.StageAsync(
                new GamePackageStageRequest(source, Convert.ToHexString(SHA256.HashData(content))),
                CancellationToken.None);
            gaming.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Empty(Directory.GetFiles(staging, "game-*.zip"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Gaming_transition_cancels_delete_during_lease_wait_without_removing_package()
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "fixture.zip");
            byte[] content = "source"u8.ToArray();
            await File.WriteAllBytesAsync(source, content);
            string staging = Path.Combine(root, "staging");
            var setup = new GamePackageStageService(() => false,
                () => CancellationToken.None, staging);
            var saved = await setup.StageAsync(new GamePackageStageRequest(source,
                Convert.ToHexString(SHA256.HashData(content))), CancellationToken.None);
            string id = Path.GetFileNameWithoutExtension(saved.StagedPath)[5..];

            using var held = new FileStream(Path.Combine(staging, GamePackageStageService.LockFileName),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var gaming = new CancellationTokenSource();
            var stage = new GamePackageStageService(
                () => gaming.IsCancellationRequested, () => gaming.Token, staging);
            Task<bool> pending = stage.DeleteStagedAsync(id, CancellationToken.None);
            gaming.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(content, await File.ReadAllBytesAsync(saved.StagedPath));
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
            Assert.Empty(NonLockFiles(Path.Combine(root, "staging")));
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


    [Fact]
    public async Task Managed_catalog_removes_only_the_exact_staged_copy_with_explicit_id()
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "original.zip");
            byte[] bytes = "owned fixture"u8.ToArray();
            await File.WriteAllBytesAsync(original, bytes);
            string digest = Convert.ToHexString(SHA256.HashData(bytes));
            var service = new GamePackageStageService(() => false,
                () => CancellationToken.None, Path.Combine(root, "private"));
            var copy = await service.StageAsync(new GamePackageStageRequest(original, digest),
                CancellationToken.None);
            var catalog = service.ListStaged();
            Assert.Single(catalog);
            Assert.Equal(copy.SizeBytes, catalog[0].SizeBytes);
            Assert.True(GamePackageStageService.ValidStagedId(catalog[0].Id));
            Assert.False(await service.DeleteStagedAsync("../../original", CancellationToken.None));
            Assert.True(await service.DeleteStagedAsync(catalog[0].Id, CancellationToken.None));
            Assert.Empty(service.ListStaged());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(original));
        }
        finally { Directory.Delete(root, true); }
    }

}

public sealed class GamePackageStageSecurityTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public GamePackageStageSecurityTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task All_staging_endpoints_reject_non_owner_callers_before_reading_or_writing_files()
    {
        using var client = _factory.CreateClient();
        using var list = await client.GetAsync("/api/v1/gaming/installers/staged");
        using var delete = await client.DeleteAsync(
            "/api/v1/gaming/installers/staged/00000000000000000000000000000000");
        using var stage = await client.PostAsJsonAsync(
            "/api/v1/gaming/installers/stage",
            new GamePackageStageRequest("missing.zip", new string('a', 64)));
        using var completedDownloadStage = await client.PostAsync(
            "/api/v1/gaming/downloads/00000000000000000000000000000000/stage", null);
        using var completedDownloadInspect = await client.PostAsync(
            "/api/v1/gaming/downloads/00000000000000000000000000000000/inspect", null);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, stage.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, completedDownloadStage.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, completedDownloadInspect.StatusCode);
    }
}
