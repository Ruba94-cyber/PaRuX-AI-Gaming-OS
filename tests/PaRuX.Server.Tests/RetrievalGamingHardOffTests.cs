using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Gaming hard-off for the resident llama.cpp retrieval (embedding / reranker) servers. The "llama-server" is a small
/// PowerShell HttpListener on the loopback port the server picked, so no model, GPU or user process is touched. The
/// Gaming signal is an injected flag that is driven exactly like the guard drives it (stop, then reopen).
/// </summary>
public sealed class RetrievalGamingHardOffTests : IDisposable
{
    private const string FakeServer = @"
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, __PORT__)
$l.Start()
while ($true) {
  $c = $l.AcceptTcpClient()
  $s = $c.GetStream()
  $buf = New-Object byte[] 65536
  $n = $s.Read($buf, 0, 65536)
  $req = [Text.Encoding]::ASCII.GetString($buf, 0, $n)
  if ($req -match 'Content-Length: (\d+)') {
    $need = [int]$Matches[1]
    $have = $n - ($req.IndexOf(""`r`n`r`n"") + 4)
    while ($have -lt $need) { $have += $s.Read($buf, 0, 65536) }
  }
  $p = '/'
  if ($req -match '^(GET|POST) (\S+)') { $p = $Matches[2] }
  if ($p -eq '/v1/embeddings' -and '__HANG__' -eq 'yes') { Start-Sleep -Seconds 120 }
  if ($p -eq '/health') { $b = '{""status"":""ok""}' }
  elseif ($p -eq '/v1/embeddings') { $b = '{""data"":[{""index"":0,""embedding"":[3.0,4.0]}]}' }
  else { $b = '{}' }
  $bytes = [Text.Encoding]::UTF8.GetBytes($b)
  $h = [Text.Encoding]::ASCII.GetBytes(""HTTP/1.1 200 OK`r`nContent-Type: application/json`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n"")
  $s.Write($h, 0, $h.Length)
  $s.Write($bytes, 0, $bytes.Length)
  $s.Flush()
  $c.Close()
}";

    private readonly string _modelPath = Path.Combine(Path.GetTempPath(), "parux-retrieval-test-" + Guid.NewGuid().ToString("N") + ".gguf");
    private readonly GamingHardOffRegistry _registry = new();
    private readonly List<LlamaRetrievalServer> _servers = new();
    private int _spawnCount;
    private bool _gaming;
    private bool _hang;

    public RetrievalGamingHardOffTests()
    {
        Assert.True(OperatingSystem.IsWindows(), "The fake retrieval server uses Windows PowerShell.");
        File.WriteAllBytes(_modelPath, new byte[] { 1 });
    }

    public void Dispose()
    {
        _gaming = true;
        _registry.StopAll();
        foreach (LlamaRetrievalServer server in _servers)
        {
            try { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
        try { File.Delete(_modelPath); } catch { }
    }

    private LlamaCppEmbeddingProvider NewProvider()
    {
        var server = new LlamaRetrievalServer(_modelPath, LlamaRetrievalMode.Embedding, () => _gaming, _registry,
            _ => Task.FromResult(false))
        {
            LaunchOverrideForTests = port =>
            {
                Interlocked.Increment(ref _spawnCount);
                var info = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "WindowsPowerShell", "v1.0", "powershell.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-Command");
                info.ArgumentList.Add(FakeServer.Replace("__PORT__", port.ToString()).Replace("__HANG__", _hang ? "yes" : "no"));
                return info;
            }
        };
        _servers.Add(server);
        return new LlamaCppEmbeddingProvider(server, "test-embed", "r1", dimensions: 2);
    }

    /// <summary>The Gaming start as the guard performs it: marker first, then every registered participant.</summary>
    private bool StartGaming()
    {
        _gaming = true;
        return _registry.StopAll();
    }

    private void StopGaming()
    {
        _gaming = false;
        _registry.ReopenAll();
    }

    [Fact]
    public async Task EmbedBatch_WorksAndNormalizesWhenGamingIsOff()
    {
        LlamaCppEmbeddingProvider provider = NewProvider();
        IReadOnlyList<float[]?> vectors = await provider.EmbedBatchAsync(new[] { "hello" }, CancellationToken.None);
        Assert.Equal(0.6f, vectors[0]![0], 3);
        Assert.Equal(1, _spawnCount);
    }

    [Fact]
    public async Task GamingStart_KillsTheResidentServer_AndRefusesWithoutRespawning()
    {
        LlamaCppEmbeddingProvider provider = NewProvider();
        await provider.EmbedBatchAsync(new[] { "hello" }, CancellationToken.None);
        Assert.Equal(1, _spawnCount);

        Assert.True(StartGaming(), "The resident server must confirm exit.");

        var typed = await Assert.ThrowsAsync<UnavailableDuringGamingException>(() =>
            provider.EmbedBatchAsync(new[] { "again" }, CancellationToken.None));
        Assert.Equal("gaming_active", UnavailableDuringGamingException.ErrorCode);
        Assert.NotNull(typed.Message);
        Assert.Equal(1, _spawnCount);
        // IsAvailable never starts anything either.
        Assert.False(await provider.IsAvailableAsync(CancellationToken.None));
        Assert.Equal(1, _spawnCount);
    }

    [Fact]
    public async Task NoSpawnWhileGamingIsActive_EvenBeforeTheGuardTicks()
    {
        LlamaCppEmbeddingProvider provider = NewProvider();
        _gaming = true; // marker present, registry not yet driven
        await Assert.ThrowsAsync<UnavailableDuringGamingException>(() =>
            provider.EmbedBatchAsync(new[] { "x" }, CancellationToken.None));
        Assert.Equal(0, _spawnCount);
    }

    [Fact]
    public async Task InFlightRequest_GetsTheTypedError_WhenGamingStarts()
    {
        _hang = true;
        LlamaCppEmbeddingProvider provider = NewProvider();
        // The first call is the readiness probe (/health answers); the embedding call itself hangs in the child.
        Task<IReadOnlyList<float[]?>> inFlight = provider.EmbedBatchAsync(new[] { "slow" }, CancellationToken.None);
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (_spawnCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);
        await Task.Delay(1500); // let it reach the hanging /v1/embeddings call

        Assert.True(StartGaming());

        await Assert.ThrowsAsync<UnavailableDuringGamingException>(() => inFlight.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task CallerCancel_IsNotRewrittenAsGaming()
    {
        _hang = true;
        LlamaCppEmbeddingProvider provider = NewProvider();
        using var cts = new CancellationTokenSource();
        Task<IReadOnlyList<float[]?>> inFlight = provider.EmbedBatchAsync(new[] { "slow" }, cts.Token);
        await Task.Delay(2500);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task AfterGaming_TheServerRecoversLazily()
    {
        LlamaCppEmbeddingProvider provider = NewProvider();
        await provider.EmbedBatchAsync(new[] { "one" }, CancellationToken.None);
        Assert.True(StartGaming());
        StopGaming();
        Assert.Equal(1, _spawnCount); // reopening never starts anything by itself

        IReadOnlyList<float[]?> vectors = await provider.EmbedBatchAsync(new[] { "two" }, CancellationToken.None);
        Assert.NotNull(vectors[0]);
        Assert.Equal(2, _spawnCount);
    }

    [Fact]
    public async Task DisposedProvider_LeavesTheRegistry()
    {
        LlamaCppEmbeddingProvider provider = NewProvider();
        Assert.Equal(1, _registry.Count);
        await provider.DisposeAsync();
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public async Task IndexingBatch_RethrowsTheGamingError_InsteadOfWritingNullVectors()
    {
        var identity = new RetrievalEmbeddingIdentity("p", "m", "r", 2, "retrieval");
        var gamingProvider = new ResolvedRetrievalEmbeddingProvider(identity, 0f, true, false,
            (_, _) => throw new UnavailableDuringGamingException("Gaming"));
        await Assert.ThrowsAsync<UnavailableDuringGamingException>(() =>
            RagStore.EmbedResolvedBatchAsync(gamingProvider, new[] { "chunk" }, CancellationToken.None));

        // Regression: an ordinary provider failure still degrades to "no vector" rather than failing the run.
        var failing = gamingProvider with { EmbedBatchAsync = (_, _) => throw new InvalidDataException("bad") };
        IReadOnlyList<float[]?> result = await RagStore.EmbedResolvedBatchAsync(failing, new[] { "chunk" }, CancellationToken.None);
        Assert.Single(result);
        Assert.Null(result[0]);
    }
}
