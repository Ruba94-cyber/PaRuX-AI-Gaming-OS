using System;
using System.IO;
using System.Text.Json;
using PaRuX.Services;
using Xunit;

public sealed class CodingModelCallTraceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-trace-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Disabled_by_default_and_for_blank_directory()
    {
        string? previous = Environment.GetEnvironmentVariable(CodingModelCallTrace.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(CodingModelCallTrace.EnvironmentVariable, null);
            Assert.Null(CodingModelCallTrace.TryCreate("step-1"));
            Environment.SetEnvironmentVariable(CodingModelCallTrace.EnvironmentVariable, "   ");
            Assert.Null(CodingModelCallTrace.TryCreate("step-1"));
        }
        finally { Environment.SetEnvironmentVariable(CodingModelCallTrace.EnvironmentVariable, previous); }
    }

    [Fact]
    public void Writes_exact_request_and_raw_response_with_content_reasoning_finish_and_usage()
    {
        var trace = CodingModelCallTrace.TryCreate("step-3", new[] { "TOPSECRETTOKEN1234" }, _dir)!;
        string body = "{\"model\":\"qwen\",\"messages\":[{\"role\":\"user\",\"content\":\"hi TOPSECRETTOKEN1234 Authorization: Bearer abcdefghijklmnop\"}],\"response_format\":{\"type\":\"json_schema\"}}";
        trace.WriteRequest("/v1/chat/completions", body);
        trace.SetStatus(200);
        trace.ObserveSseData("{\"choices\":[{\"delta\":{\"reasoning_content\":\"think\"}}]}");
        trace.ObserveSseData("{\"choices\":[{\"delta\":{\"content\":\"{\\\"action\\\":\"}}]}");
        trace.ObserveSseData("{\"choices\":[{\"delta\":{\"content\":\"\\\"read_file\\\"}\"},\"finish_reason\":\"stop\"}],\"usage\":{\"completion_tokens\":7}}");
        trace.Complete();
        trace.Complete();

        using JsonDocument req = JsonDocument.Parse(File.ReadAllText(trace.RequestPath));
        string sent = req.RootElement.GetProperty("bodyText").GetString()!;
        Assert.Contains("response_format", sent);
        Assert.DoesNotContain("TOPSECRETTOKEN1234", sent);
        Assert.DoesNotContain("abcdefghijklmnop", sent);

        using JsonDocument res = JsonDocument.Parse(File.ReadAllText(trace.ResponsePath));
        Assert.Equal(200, res.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("{\"action\":\"read_file\"}", res.RootElement.GetProperty("content").GetString());
        Assert.Equal("think", res.RootElement.GetProperty("reasoning_content").GetString());
        Assert.Equal("stop", res.RootElement.GetProperty("finish_reason").GetString());
        Assert.Equal(7, res.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        Assert.Equal(3, res.RootElement.GetProperty("rawLines").GetArrayLength());
    }

    [Fact]
    public void Output_is_size_bounded_and_failed_calls_still_leave_a_response_file()
    {
        var trace = CodingModelCallTrace.TryCreate("step-9", null, _dir)!;
        trace.WriteRequest("/x", new string('a', CodingModelCallTrace.MaxFileCharacters + 5000));
        using (JsonDocument req = JsonDocument.Parse(File.ReadAllText(trace.RequestPath)))
        {
            Assert.True(req.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Equal(CodingModelCallTrace.MaxFileCharacters, req.RootElement.GetProperty("bodyText").GetString()!.Length);
        }
        for (int i = 0; i < CodingModelCallTrace.MaxRawLines + 10; i++)
            trace.ObserveSseData("{\"choices\":[{\"delta\":{\"content\":\"x\"}}]}");
        trace.SetError("HTTP 502");
        trace.Complete();
        using JsonDocument res = JsonDocument.Parse(File.ReadAllText(trace.ResponsePath));
        Assert.True(res.RootElement.GetProperty("rawLinesTruncated").GetBoolean());
        Assert.Equal("HTTP 502", res.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Unwritable_directory_never_throws()
    {
        string file = Path.Combine(Path.GetTempPath(), "parux-trace-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "x");
        try { Assert.Null(CodingModelCallTrace.TryCreate("step-1", null, file)); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Client_wires_the_trace_and_the_service_labels_each_step()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "src", "PaRuX.App", "Services", "StructuredChatClient.cs")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        string client = File.ReadAllText(Path.Combine(root!, "src", "PaRuX.App", "Services", "StructuredChatClient.cs"));
        Assert.Contains("trace?.WriteRequest(endpointPath, requestBody)", client);
        Assert.Contains("trace?.ObserveSseData(data)", client);
        Assert.Contains("trace?.Complete()", client);
        string service = File.ReadAllText(Path.Combine(root!, "src", "PaRuX.App", "Services", "CodingAgentService.cs"));
        Assert.Contains("traceLabel: \"step-\" + step", service);
    }
}
