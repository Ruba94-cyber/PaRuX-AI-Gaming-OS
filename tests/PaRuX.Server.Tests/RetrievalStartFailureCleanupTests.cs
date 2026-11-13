using System.Diagnostics;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>M4: a failure between StartOwned and the first health check must not leak a running llama-server.</summary>
public sealed class RetrievalStartFailureCleanupTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-retr-leak-" + Guid.NewGuid().ToString("N"));

    public RetrievalStartFailureCleanupTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    [Fact]
    public async Task An_exception_after_the_child_started_stops_the_child()
    {
        Assert.True(OperatingSystem.IsWindows(), "The sentinel child uses Windows PowerShell.");
        string model = Path.Combine(_work, "m.gguf");
        File.WriteAllBytes(model, new byte[] { 1 });
        string pidFile = Path.Combine(_work, "pid.txt");
        var registry = new GamingHardOffRegistry();
        var server = new LlamaRetrievalServer(model, LlamaRetrievalMode.Embedding, () => false, registry, _ => Task.FromResult(false))
        {
            // Output is deliberately not redirected: reading process.StandardOutput right after StartOwned throws.
            LaunchOverrideForTests = _ =>
            {
                var info = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "WindowsPowerShell", "v1.0", "powershell.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-Command");
                info.ArgumentList.Add("Set-Content -LiteralPath '" + pidFile + "' -Value $PID; Start-Sleep -Seconds 120");
                return info;
            }
        };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => server.EnsureReadyAsync(CancellationToken.None));

            int pid = 0;
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (pid == 0 && DateTime.UtcNow < deadline)
            {
                try { int.TryParse(File.ReadAllText(pidFile).Trim(), out pid); } catch (IOException) { }
                if (pid == 0) await Task.Delay(100);
            }
            // The child may have been killed before it wrote its pid; then nothing is left running, which is the point.
            if (pid == 0) return;
            bool alive = true;
            for (int i = 0; i < 50 && alive; i++)
            {
                try { using Process p = Process.GetProcessById(pid); alive = !p.HasExited; }
                catch (ArgumentException) { alive = false; }
                if (alive) await Task.Delay(100);
            }
            Assert.False(alive, "The llama-server child outlived the failed start.");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
