using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CodexWatchdog;

public sealed class HumanActionRequiredException(string message) : Exception(message);

public sealed class RpcConnection : IAsyncDisposable
{
    private Process? proxy;
    private ClientWebSocket? socket;
    private Task? stderrDrain;
    private int sequence;
    private readonly TimeSpan timeout;
    private bool broken;
    public RpcConnection(TimeSpan timeout) => this.timeout = timeout;

    public async Task ConnectAsync(Config config, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        if (config.Endpoint is { } endpoint)
        {
            var uri = new Uri(endpoint);
            if (uri.Scheme != "ws" || !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("v0.1 supports only local ws://127.0.0.1:PORT endpoints.");
            socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, limit.Token);
        }
        else
        {
            proxy = ProcessDetector.Start(config.CodexPath, ["app-server", "proxy"], redirectInput: true);
            stderrDrain = DrainErrorAsync(proxy.StandardError);
        }
        await CallAsync("initialize", new
        {
            clientInfo = new { name = "codex_watchdog", title = "Codex Watchdog", version = "0.1.0" },
            capabilities = new { experimentalApi = false }
        }, limit.Token);
        await SendAsync(JsonSerializer.Serialize(new { method = "initialized" }), limit.Token);
    }

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        if (broken) throw new IOException("RPC connection unusable after transport failure; restart watchdog.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var id = ++sequence;
        try
        {
            await SendAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }), limit.Token);
            while (true)
            {
                using var message = JsonDocument.Parse(await ReceiveAsync(limit.Token));
                var root = message.RootElement;
                if (root.TryGetProperty("method", out var incoming))
                {
                    if (root.TryGetProperty("id", out _))
                        throw new HumanActionRequiredException($"Server requires user action: {incoming.GetString()}");
                    continue;
                }
                if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var number) || number != id)
                    throw new JsonException("Unexpected RPC response id");
                if (root.TryGetProperty("error", out var error)) throw new IOException($"{method}: {error}");
                return root.GetProperty("result").Clone();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            broken = true;
            throw new TimeoutException($"RPC timeout: {method}; request outcome may be unknown");
        }
        catch
        {
            broken = true;
            throw;
        }
    }

    private async Task SendAsync(string message, CancellationToken token)
    {
        if (socket is not null)
            await socket.SendAsync(Encoding.UTF8.GetBytes(message).AsMemory(), WebSocketMessageType.Text, true, token);
        else if (proxy is not null)
        {
            await proxy.StandardInput.WriteLineAsync(message.AsMemory(), token);
            await proxy.StandardInput.FlushAsync(token);
        }
        else throw new InvalidOperationException("Not connected");
    }

    private async Task<string> ReceiveAsync(CancellationToken token)
    {
        const int maxBytes = 16 * 1024 * 1024;
        if (socket is null)
        {
            var line = await (proxy ?? throw new InvalidOperationException("Not connected")).StandardOutput.ReadLineAsync(token);
            if (line is null) throw new IOException("Codex proxy disconnected. Requires compatible running app-server daemon.");
            if (line.Length > maxBytes) throw new IOException("RPC message too large");
            return line;
        }
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(chunk.AsMemory(), token);
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("WebSocket closed or non-text message");
            buffer.Write(chunk, 0, result.Count);
            if (buffer.Length > maxBytes) throw new IOException("RPC message too large");
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static async Task DrainErrorAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer) != 0) { }
    }

    public async ValueTask DisposeAsync()
    {
        socket?.Abort();
        socket?.Dispose();
        if (proxy is null) return;
        if (!proxy.HasExited) proxy.Kill(); // Only our byte proxy, never the existing Codex process or daemon.
        await proxy.WaitForExitAsync();
        if (stderrDrain is not null) await stderrDrain;
        proxy.Dispose();
    }
}
