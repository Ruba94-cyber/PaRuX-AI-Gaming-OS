using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PaRuX.RuntimeStub;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        // Intentionally ignore llama.cpp command-line arguments for the chat-style launches. The overhead
        // benchmark owns the HTTP listener itself; this process exists only so PaRuX exercises the real
        // managed-runtime launch/lifecycle path instead of skipping it on Windows where a shell script
        // cannot stand in for llama-server.exe.
        //
        // Retrieval launches (--embedding / --reranking with --port N) additionally get a tiny loopback
        // /health responder so the dedicated retrieval-server lifecycle (identity capture, attested
        // connection, relaunch after a Core restart) can be driven against a real child process.
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        TcpListener? listener = null;
        int portIndex = Array.IndexOf(args, "--port");
        bool retrieval = args.Contains("--embedding") || args.Contains("--reranking");
        // SEC-F01: opt-in (PARUX_STUB_SERVE_HTTP=1) full chat-style responder, so the per-launch key can be proven against a
        // real child process. Off by default: other tests own the text port's listener themselves.
        bool serveHttp = retrieval || Environment.GetEnvironmentVariable("PARUX_STUB_SERVE_HTTP") == "1";
        if (serveHttp && portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out int port) && port > 0)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            // Like llama-server --api-key (LLAMA_API_KEY): every route except the public ones needs "Authorization: Bearer <key>".
            string? apiKey = Environment.GetEnvironmentVariable("LLAMA_API_KEY");
            if (Environment.GetEnvironmentVariable("PARUX_STUB_LEAK_KEY") == "1" && !string.IsNullOrEmpty(apiKey))
                Console.Error.WriteLine("stub received api key " + apiKey);
            _ = Task.Run(() => ServeAsync(listener, string.IsNullOrEmpty(apiKey) ? null : apiKey, shutdown.Token));
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static readonly string[] PublicPaths = { "/health", "/v1/health", "/models", "/v1/models" };

    private static async Task ServeAsync(TcpListener listener, string? apiKey, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                NetworkStream stream = client.GetStream();
                (string path, string? authorization) = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                bool isPublic = PublicPaths.Contains(path, StringComparer.Ordinal);
                bool authorized = apiKey is null || isPublic
                    || string.Equals(authorization, "Bearer " + apiKey, StringComparison.Ordinal);
                (int status, string body) = !authorized
                    ? (401, "{\"error\":{\"message\":\"Invalid API Key\",\"type\":\"authentication_error\",\"code\":401}}")
                    : path switch
                    {
                        "/slots" => (200, "[{\"id\":0,\"is_processing\":false}]"),
                        "/props" => (200, "{\"default_generation_settings\":{\"n_ctx\":4096}}"),
                        "/v1/chat/completions" => (200,
                            "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"4\"},\"finish_reason\":\"stop\"}]}"),
                        // Test-only: what this child really received. Authenticated like every other non-public route.
                        "/_stub/process" => (200, System.Text.Json.JsonSerializer.Serialize(new
                        {
                            commandLine = Environment.CommandLine,
                            arguments = Environment.GetCommandLineArgs(),
                            environmentHasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LLAMA_API_KEY"))
                        })),
                        _ => (200, "{\"status\":\"ok\"}")
                    };
                byte[] payload = Encoding.UTF8.GetBytes(body);
                byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + (status == 200 ? " OK" : " Unauthorized")
                    + "\r\nContent-Type: application/json\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    /// <summary>Reads one HTTP/1.1 request completely (headers and Content-Length body) and returns its path and Authorization header.</summary>
    private static async Task<(string Path, string? Authorization)> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var received = new List<byte>();
        var buffer = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0 && received.Count < 65536)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            received.AddRange(buffer.AsSpan(0, read).ToArray());
            headerEnd = IndexOfHeaderEnd(received);
        }
        string head = Encoding.ASCII.GetString(received.ToArray(), 0, headerEnd < 0 ? received.Count : headerEnd);
        string[] lines = head.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        string path = requestLine.Length > 1 ? requestLine[1] : "/";
        int query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        string? authorization = null;
        int contentLength = 0;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) authorization = value;
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) _ = int.TryParse(value, out contentLength);
        }
        int bodyHave = headerEnd < 0 ? 0 : received.Count - (headerEnd + 4);
        while (headerEnd >= 0 && bodyHave < contentLength)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            bodyHave += read;
        }
        return (path, authorization);
    }

    private static int IndexOfHeaderEnd(List<byte> bytes)
    {
        for (int i = 0; i + 3 < bytes.Count; i++)
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) return i;
        return -1;
    }
}
