using System.Net;
using System.Security.Cryptography;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Game downloads end to end on the real app and the real Core. Simulated: the file server (a loopback HTTP listener that serves a
/// deterministic 64 MB file slowly and honours Range, so pause and resume have something to act on). Real: the Downloads page, the
/// Core's download manager, the file written to disk and its SHA-256 check.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class RealDownloadsUiTests
{
    private const int Size = 64 * 1024 * 1024;

    private static string Text(AutomationElement e) { try { return e.Name ?? string.Empty; } catch { return string.Empty; } }

    private static byte[] Content()
    {
        var bytes = new byte[Size];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 31 + 7) & 0xFF);
        return bytes;
    }

    private sealed class SlowFileServer : IDisposable
    {
        private readonly HttpListener _http = new();
        private readonly byte[] _content;
        public int Port { get; }
        public int RangeRequests;
        public int Requests;

        public SlowFileServer(byte[] content)
        {
            _content = content;
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _http.Start();
            _ = Task.Run(async () =>
            {
                while (_http.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _http.GetContextAsync(); } catch (Exception) { break; }
                    _ = Task.Run(() => Serve(c));
                }
            });
        }

        private void Serve(HttpListenerContext c)
        {
            try
            {
                Interlocked.Increment(ref Requests);
                long start = 0;
                string? range = c.Request.Headers["Range"];
                if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref RangeRequests);
                    long.TryParse(range["bytes=".Length..].Split('-')[0], out start);
                    c.Response.StatusCode = 206;
                    c.Response.AddHeader("Content-Range", $"bytes {start}-{_content.Length - 1}/{_content.Length}");
                }
                c.Response.AddHeader("Accept-Ranges", "bytes");
                c.Response.ContentType = "application/octet-stream";
                c.Response.ContentLength64 = _content.Length - start;
                if (c.Request.HttpMethod == "HEAD") { c.Response.Close(); return; }
                const int chunk = 64 * 1024;
                for (long offset = start; offset < _content.Length; offset += chunk)
                {
                    int n = (int)Math.Min(chunk, _content.Length - offset);
                    c.Response.OutputStream.Write(_content, (int)offset, n);
                    c.Response.OutputStream.Flush();
                    Thread.Sleep(30);   // about 2 MB/s
                }
                c.Response.Close();
            }
            catch (Exception) { try { c.Response.Abort(); } catch { } }
        }

        public void Dispose() { try { _http.Stop(); _http.Close(); } catch { } }
    }

    private static long FolderBytes(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }) : 0;

    private static void OpenDownloads(UiTestApplication app)
    {
        new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "MainWindow_ModeTab_Gaming"));
        GamingSectionsPage.SelectSection(app.MainWindow, "Downloads");
    }

    private static void StartJob(UiTestApplication app, int port, string folder, string? sha)
    {
        UiWait.ById(app.MainWindow, "GameDownloads_Url", ControlType.Edit).AsTextBox().Text = $"http://127.0.0.1:{port}/game.bin";
        UiWait.ById(app.MainWindow, "GameDownloads_Destination", ControlType.Edit).AsTextBox().Text = folder;
        UiWait.ById(app.MainWindow, "GameDownloads_FileName", ControlType.Edit).AsTextBox().Text = "game.bin";
        UiWait.ById(app.MainWindow, "GameDownloads_SHA256", ControlType.Edit).AsTextBox().Text = sha ?? string.Empty;
        new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "GameDownloads_Start", ControlType.Button));
        // No download starts without the owner's confirmation: say yes in the consent dialog.
        var confirm = UiWait.ById(app.MainWindow, "PrimaryButton", ControlType.Button);
        confirm.Patterns.Invoke.Pattern.Invoke();
    }

    private static AutomationElement? ByIdPrefix(UiTestApplication app, string prefix) =>
        app.MainWindow.FindAllDescendants().FirstOrDefault(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith(prefix, StringComparison.Ordinal));

    private static void Refresh(UiTestApplication app)
    {
        try { UiWait.ById(app.MainWindow, "GameDownloads_Refresh", ControlType.Button).AsButton().Invoke(); } catch (Exception) { }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void A_download_can_be_paused_and_resumed_and_ends_with_the_exact_file_and_a_verified_checksum()
    {
        const string name = nameof(A_download_can_be_paused_and_resumed_and_ends_with_the_exact_file_and_a_verified_checksum);
        UiTestApplication.WaitForCorePortReleased();
        byte[] content = Content();
        string sha = Convert.ToHexString(SHA256.HashData(content));
        using var server = new SlowFileServer(content);
        using var app = new UiTestApplication(name);
        try
        {
            string folder = Path.Combine(app.DataRoot, "downloads-out");
            Directory.CreateDirectory(folder);
            OpenDownloads(app);
            StartJob(app, server.Port, folder, sha);

            // It starts and bytes arrive.
            UiWait.For(() => { Refresh(app); return FolderBytes(folder) > 512 * 1024; }, "bytes are arriving", TimeSpan.FromSeconds(60));
            UiWait.For(() => { Refresh(app); return ByIdPrefix(app, "GameDownloads_Pause_") is not null; }, "a Pause button is offered", TimeSpan.FromSeconds(30));
            // One click on Pause is enough, even if the list happens to be refreshing at that moment.
            ByIdPrefix(app, "GameDownloads_Pause_")!.AsButton().Invoke();
            UiWait.For(() => { Refresh(app); return ByIdPrefix(app, "GameDownloads_Resume_") is not null; }, "the job shows as paused", TimeSpan.FromSeconds(60));
            long frozen = FolderBytes(folder);
            Thread.Sleep(2500);
            long later = FolderBytes(folder);
            Assert.True(later - frozen < 2 * 1024 * 1024, "growth while paused: " + (later - frozen) + " bytes");
            Assert.True(frozen < Size, "the file is still partial when paused");

            ByIdPrefix(app, "GameDownloads_Resume_")!.AsButton().Invoke();
            UiWait.For(() =>
            {
                Refresh(app);
                return app.MainWindow.FindAllDescendants().Any(e => Text(e).Contains("SHA-256 verified", StringComparison.Ordinal));
            }, "the download completes with a verified checksum", TimeSpan.FromSeconds(120));

            string finalFile = Path.Combine(folder, "game.bin");
            Assert.True(File.Exists(finalFile), "the finished file exists");
            Assert.Equal(Size, new FileInfo(finalFile).Length);
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(finalFile))));
            Assert.Equal(new[] { "game.bin" }, Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray());
            Assert.True(server.RangeRequests >= 1, "resume used a Range request, not a restart from zero");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void A_download_whose_checksum_does_not_match_never_becomes_the_finished_file()
    {
        const string name = nameof(A_download_whose_checksum_does_not_match_never_becomes_the_finished_file);
        UiTestApplication.WaitForCorePortReleased();
        using var server = new SlowFileServer(Content());
        using var app = new UiTestApplication(name);
        try
        {
            string folder = Path.Combine(app.DataRoot, "downloads-out");
            Directory.CreateDirectory(folder);
            OpenDownloads(app);
            StartJob(app, server.Port, folder, new string('0', 64));

            // It runs to the end of the transfer, then refuses the file: no "verified" and no finished game.bin.
            UiWait.For(() =>
            {
                Refresh(app);
                return ByIdPrefix(app, "GameDownloads_Pause_") is null && ByIdPrefix(app, "GameDownloads_Resume_") is null
                    && ByIdPrefix(app, "GameDownloads_Remove_") is not null;
            }, "the job reaches an end state", TimeSpan.FromSeconds(120));
            Assert.False(app.MainWindow.FindAllDescendants().Any(e => Text(e).Contains("SHA-256 verified", StringComparison.Ordinal)));
            Assert.False(File.Exists(Path.Combine(folder, "game.bin")), "a file that failed its checksum is not left as the finished download");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void A_download_interrupted_by_closing_PaRuX_continues_from_the_kept_part_when_added_again()
    {
        const string name = nameof(A_download_interrupted_by_closing_PaRuX_continues_from_the_kept_part_when_added_again);
        UiTestApplication.WaitForCorePortReleased();
        byte[] content = Content();
        string sha = Convert.ToHexString(SHA256.HashData(content));
        using var server = new SlowFileServer(content);
        UiTestApplication app = new(name);
        try
        {
            string folder = Path.Combine(app.DataRoot, "downloads-out");
            Directory.CreateDirectory(folder);
            OpenDownloads(app);
            StartJob(app, server.Port, folder, sha);
            UiWait.For(() => { Refresh(app); return FolderBytes(folder) > 6 * 1024 * 1024; }, "several MiB arrived", TimeSpan.FromSeconds(90));
            long beforeRestart = FolderBytes(folder);

            // Close PaRuX (and its Core) in the middle of the transfer, then open it again on the same data.
            app = app.Restart(name + "-restart");
            OpenDownloads(app);

            // By design the history never stores a download's address or folder (a link can carry a secret), so the job is not listed
            // again; what survives is the part already on disk. Adding the same download again continues from it.
            Assert.True(FolderBytes(folder) >= beforeRestart - 1024 * 1024, "the partial file was kept: " + FolderBytes(folder) + " bytes vs " + beforeRestart);
            StartJob(app, server.Port, folder, sha);
            UiWait.For(() =>
            {
                Refresh(app);
                return app.MainWindow.FindAllDescendants().Any(e => Text(e).Contains("SHA-256 verified", StringComparison.Ordinal));
            }, "the same download completes after the restart", TimeSpan.FromSeconds(180));

            string finalFile = Path.Combine(folder, "game.bin");
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(finalFile))));
            Assert.True(server.RangeRequests >= 1, "the restart resumed with a Range request");
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
        finally { app.Dispose(); }
    }
}
