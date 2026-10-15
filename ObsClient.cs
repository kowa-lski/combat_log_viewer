using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CombatLogViewer;

/// <summary>
/// A minimal client for OBS's built-in WebSocket server (obs-websocket 5, OBS 28+): connects and
/// authenticates, sends requests, and raises ReplayBufferSaved when OBS writes a replay.
/// </summary>
public sealed class ObsClient : IAsyncDisposable
{
    ClientWebSocket? socket;
    CancellationTokenSource? cts;
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> pending = new();

    /// <summary>OBS wrote a replay to this path (from the save hotkey or a SaveReplayBuffer request).</summary>
    public event Action<string>? ReplayBufferSaved;

    /// <summary>The connection closed after it had been open.</summary>
    public event Action<string>? Disconnected;

    public bool IsConnected => socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(string host, int port, string password, CancellationToken cancel)
    {
        socket = new ClientWebSocket();
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri($"ws://{host}:{port}"), timeout.Token);

        // Hello (op 0) → Identify (op 1) → Identified (op 2)
        var hello = await ReceiveAsync(timeout.Token) ?? throw new IOException("OBS closed the connection.");
        var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 1 << 6 /* Outputs */ };
        if (hello["d"]?["authentication"] is JsonObject auth)
        {
            if (password.Length == 0) throw new UnauthorizedAccessException("OBS needs the WebSocket password.");
            identify["authentication"] = AuthString(password, (string)auth["salt"]!, (string)auth["challenge"]!);
        }
        await SendAsync(new JsonObject { ["op"] = 1, ["d"] = identify }, timeout.Token);
        var identified = await ReceiveAsync(timeout.Token);
        if (identified?["op"]?.GetValue<int>() != 2)
            throw new UnauthorizedAccessException(socket.CloseStatus == (WebSocketCloseStatus)4009
                ? "OBS rejected the WebSocket password." : $"OBS refused the connection ({socket.CloseStatusDescription}).");

        _ = Task.Run(() => ReceiveLoop(cts.Token));
    }

    /// <summary>Sends a request and returns its responseData; throws if OBS reports a failure.</summary>
    public async Task<JsonNode?> RequestAsync(string type, JsonObject? data = null)
    {
        string id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        var d = new JsonObject { ["requestType"] = type, ["requestId"] = id };
        if (data != null) d["requestData"] = data;
        await SendAsync(new JsonObject { ["op"] = 6, ["d"] = d }, cts?.Token ?? default);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        pending.TryRemove(id, out _);
        if (done != tcs.Task) throw new TimeoutException($"OBS did not answer {type}.");
        return await tcs.Task;
    }

    async Task ReceiveLoop(CancellationToken cancel)
    {
        string reason = "connection closed";
        try
        {
            while (!cancel.IsCancellationRequested && await ReceiveAsync(cancel) is { } msg)
            {
                var d = msg["d"];
                switch (msg["op"]?.GetValue<int>())
                {
                    case 7: // RequestResponse
                        if (pending.TryGetValue((string?)d?["requestId"] ?? "", out var tcs))
                        {
                            var status = d?["requestStatus"];
                            if (status?["result"]?.GetValue<bool>() == true) tcs.TrySetResult(d?["responseData"]);
                            else tcs.TrySetException(new InvalidOperationException(
                                $"OBS: {(string?)status?["comment"] ?? $"request failed (code {status?["code"]})"}"));
                        }
                        break;
                    case 5: // Event
                        if ((string?)d?["eventType"] == "ReplayBufferSaved" && (string?)d?["eventData"]?["savedReplayPath"] is { } path)
                            ReplayBufferSaved?.Invoke(path);
                        break;
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { reason = e.Message; }
        foreach (var tcs in pending.Values) tcs.TrySetException(new IOException("OBS disconnected."));
        if (!cancel.IsCancellationRequested) Disconnected?.Invoke(reason);
    }

    async Task<JsonNode?> ReceiveAsync(CancellationToken cancel)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket!.ReceiveAsync(buffer, cancel);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonNode.Parse(message.ToArray());
    }

    readonly SemaphoreSlim sendLock = new(1, 1);

    async Task SendAsync(JsonObject msg, CancellationToken cancel)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await sendLock.WaitAsync(cancel);
        try { await socket!.SendAsync(bytes, WebSocketMessageType.Text, true, cancel); }
        finally { sendLock.Release(); }
    }

    /// <summary>obs-websocket 5 auth: base64(sha256(base64(sha256(password + salt)) + challenge)).</summary>
    static string AuthString(string password, string salt, string challenge)
    {
        string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public async ValueTask DisposeAsync()
    {
        cts?.Cancel();
        if (socket is { State: WebSocketState.Open })
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        socket?.Dispose();
    }
}
