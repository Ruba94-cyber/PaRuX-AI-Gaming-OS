using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AppExitSignalTests
{
    [Theory]
    [InlineData(new[] { "PaRuX.exe", "--exit" }, true)]
    [InlineData(new[] { "PaRuX.exe", "--EXIT" }, true)]
    [InlineData(new[] { "PaRuX.exe", "--other", "--exit" }, true)]
    [InlineData(new[] { "PaRuX.exe" }, false)]
    [InlineData(new[] { "--exit" }, false)]            // only the executable path: no argument
    [InlineData(new[] { "PaRuX.exe", "--exit-now" }, false)]
    public void Exit_is_requested_only_by_the_exact_argument(string[] commandLine, bool expected) =>
        Assert.Equal(expected, AppExitSignal.IsExitRequest(commandLine));

    [Fact]
    public void Name_is_stable_for_the_same_folder_and_differs_between_installs()
    {
        string a = AppExitSignal.NameFor(@"C:\Tools\PaRuX\PaRuXData");
        Assert.Equal(a, AppExitSignal.NameFor(@"c:\tools\parux\paruxdata\"));
        Assert.NotEqual(a, AppExitSignal.NameFor(@"E:\PaRuX\PaRuXData"));
        Assert.StartsWith(@"Local\PaRuX.App.Exit.", a, StringComparison.Ordinal);
        Assert.DoesNotContain("Tools", a, StringComparison.OrdinalIgnoreCase);   // no path leaks into the object name
    }

    [Fact]
    public void A_listening_instance_receives_the_signal_once_and_nobody_else_does()
    {
        if (!OperatingSystem.IsWindows()) return;
        string name = AppExitSignal.NameFor(Path.Combine(Path.GetTempPath(), "parux-exit-" + Guid.NewGuid().ToString("N")));
        Assert.False(AppExitSignal.TrySignal(name));                             // nothing running: honest "not delivered"

        using var received = new ManualResetEventSlim();
        int calls = 0;
        using (AppExitSignal.Listen(name, () => { Interlocked.Increment(ref calls); received.Set(); }))
        {
            Assert.True(AppExitSignal.TrySignal(name));
            Assert.True(received.Wait(TimeSpan.FromSeconds(10)));
            AppExitSignal.TrySignal(name);
            Thread.Sleep(200);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        Assert.False(AppExitSignal.TrySignal(name));                             // listener gone
    }

    [Fact]
    public void Desktop_handles_the_exit_request_before_starting_anything()
    {
        string app = Read("src", "PaRuX.App", "App.xaml.cs");
        int exit = app.IndexOf("AppExitSignal.IsExitRequest(Environment.GetCommandLineArgs())", StringComparison.Ordinal);
        int mutex = app.IndexOf("new Mutex(true, mutexName", StringComparison.Ordinal);
        Assert.True(exit > 0 && mutex > exit, "The exit request must be handled before the single-instance check.");
        Assert.Contains("uiQueue.TryEnqueue(ExitApplication)", app, StringComparison.Ordinal);
    }

    private static string Read(params string[] path)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(path).ToArray()));
    }
}
