using System.Text.RegularExpressions;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source contract: every process launch under src/PaRuX.Server must be listed, with a reviewed statement of how the
/// Gaming hard-off covers it. A new <c>new Process</c> / <c>Process.Start</c> that nobody reviewed fails this test, so the
/// class of "a child that keeps the CPU/GPU while a game runs" gap cannot silently come back.
/// </summary>
public sealed class GamingHardOffProcessLaunchInventoryTests
{
    private static readonly Regex LaunchSite = new(@"new\s+Process\s*[{(]|Process\.Start\s*\(", RegexOptions.Compiled);

    private static readonly string[] Classes = { "STOPPED-BY-HARDOFF", "CALLER-GATED", "NOT-AI", "GAMING-FEATURE" };

    // A file that claims it is gated must actually mention one of the Gaming mechanisms.
    private static readonly string[] GamingMarkers =
    {
        "GamingChildGate.StartOwned", "StartOwned(", "GamingHardOffWatch", "GamingCancellationToken", "GamingActive",
        "GamingMode", "IsGamingActive", "StopAll", "SpeechProcessAdmission", "StopForGaming"
    };

    private sealed record Entry(string Path, int Count, string Class, string Reason);

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "tests", "PaRuX.Server.Tests", "GamingHardOffProcessLaunchAllowList.txt"))
                && Directory.Exists(Path.Combine(dir, "src", "PaRuX.Server")))
                return dir;
        }
        throw new DirectoryNotFoundException("The repository root with the Gaming hard-off allow-list was not found.");
    }

    private static List<Entry> ReadAllowList(string root)
    {
        var entries = new List<Entry>();
        foreach (string raw in File.ReadAllLines(Path.Combine(root, "tests", "PaRuX.Server.Tests", "GamingHardOffProcessLaunchAllowList.txt")))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] parts = line.Split('|', 4);
            Assert.True(parts.Length == 4, "Allow-list line must be 'path|count|class|reason': " + line);
            Assert.True(int.TryParse(parts[1], out int count) && count > 0, "Bad launch-site count: " + line);
            entries.Add(new Entry(parts[0].Trim().Replace('\\', '/'), count, parts[2].Trim(), parts[3].Trim()));
        }
        return entries;
    }

    private static Dictionary<string, int> ScanSource(string root)
    {
        string server = Path.Combine(root, "src", "PaRuX.Server");
        var sites = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(server, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(server, file).Replace('\\', '/');
            if (relative.StartsWith("obj/") || relative.StartsWith("bin/")) continue;
            int count = File.ReadLines(file)
                .Count(line => !line.TrimStart().StartsWith("//") && LaunchSite.IsMatch(line));
            if (count > 0) sites[relative] = count;
        }
        return sites;
    }

    [Fact]
    public void EveryProcessLaunchSiteIsListedWithItsReviewedGamingCoverage()
    {
        string root = RepoRoot();
        List<Entry> allowList = ReadAllowList(root);
        Dictionary<string, int> actual = ScanSource(root);

        var problems = new List<string>();
        Assert.Equal(allowList.Count, allowList.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach ((string path, int count) in actual.OrderBy(pair => pair.Key))
        {
            Entry? entry = allowList.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                problems.Add($"NEW process launch not reviewed: {path} ({count} site(s)). Cover it with the Gaming hard-off " +
                    "(GamingChildGate/GamingChildScope) or add it to GamingHardOffProcessLaunchAllowList.txt with the reason.");
            else if (entry.Count != count)
                problems.Add($"Launch-site count changed in {path}: reviewed {entry.Count}, found {count}. Review the new site and update the list.");
        }
        foreach (Entry entry in allowList)
        {
            if (!actual.ContainsKey(entry.Path)) problems.Add("Stale allow-list entry (no launch site any more): " + entry.Path);
            if (!Classes.Contains(entry.Class)) problems.Add($"Unknown class '{entry.Class}' for {entry.Path}.");
            if (entry.Reason.Length < 20) problems.Add("Coverage statement too short for " + entry.Path);
            if (entry.Class is "STOPPED-BY-HARDOFF" or "CALLER-GATED" && actual.ContainsKey(entry.Path))
            {
                // The launching file, or a file the reason names with "(File.cs)" when the gate lives in the caller.
                var texts = new List<string> { File.ReadAllText(Path.Combine(root, "src", "PaRuX.Server", entry.Path.Replace('/', Path.DirectorySeparatorChar))) };
                foreach (Match named in Regex.Matches(entry.Reason, @"\(([A-Za-z0-9_]+\.cs)\b"))
                {
                    string? found = Directory.EnumerateFiles(Path.Combine(root, "src", "PaRuX.Server"), named.Groups[1].Value, SearchOption.AllDirectories).FirstOrDefault();
                    if (found == null) problems.Add($"{entry.Path} names a missing caller file {named.Groups[1].Value}.");
                    else texts.Add(File.ReadAllText(found));
                }
                if (!texts.Any(text => GamingMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase))))
                    problems.Add($"{entry.Path} claims {entry.Class} but neither it nor the file it names references a Gaming mechanism.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(actual.Values.Sum() >= 50, "The inventory unexpectedly shrank; the scanner may be broken.");
    }

    [Fact]
    public void BenchmarkAdaptersAreOnlyConstructedByTheGamingGatedEngineRace()
    {
        string root = RepoRoot();
        string server = Path.Combine(root, "src", "PaRuX.Server");
        var constructors = new[] { "new ExLlamaV3EngineBenchmarkAdapter(", "new WindowsMlGenAiEngineBenchmarkAdapter(" };
        foreach (string file in Directory.EnumerateFiles(server, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(server, file).Replace('\\', '/');
            if (relative.StartsWith("obj/") || relative.StartsWith("bin/")) continue;
            string text = File.ReadAllText(file);
            foreach (string constructor in constructors)
            {
                if (!text.Contains(constructor, StringComparison.Ordinal)) continue;
                Assert.Equal("ManagedRuntimeEngineRaceService.cs", relative);
                Assert.Contains("GamingHardOffWatch", text);
                Assert.Contains("_runtime.GamingActive", text);
            }
        }
    }
}
