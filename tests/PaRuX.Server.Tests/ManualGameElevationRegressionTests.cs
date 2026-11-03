using System;
using System.IO;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManualGameElevationRegressionTests
{
    private static string Source()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PaRuX.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "PaRuX.Server", "Orchestration", "Gaming", "ManualGameSessionService.cs"));
    }

    [Fact]
    public void An_executable_that_requires_administrator_rights_is_started_through_the_windows_uac_prompt()
    {
        string source = Source();
        Assert.Contains("ErrorElevationRequired = 740", source, StringComparison.Ordinal);
        Assert.Contains("Verb = \"runas\"", source, StringComparison.Ordinal);
        // The unanswered prompt is bounded and a failure rolls back to AI with the real reason.
        Assert.Contains("TimeSpan.FromSeconds(120)", source, StringComparison.Ordinal);
        Assert.Contains("The executable did not start (\" + ex.Message", source, StringComparison.Ordinal);
    }
}
