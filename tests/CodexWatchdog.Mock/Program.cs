using System.Text.Json;

var scenario = Environment.GetEnvironmentVariable("WATCHDOG_MOCK_SCENARIO") ?? "complete";
var trace = Environment.GetEnvironmentVariable("WATCHDOG_MOCK_TRACE");
var sent = 0;
var reads = 0;
while (await Console.In.ReadLineAsync() is { } line)
{
    using var document = JsonDocument.Parse(line);
    var request = document.RootElement;
    var method = request.GetProperty("method").GetString();
    if (trace is not null) await File.AppendAllTextAsync(trace, line + Environment.NewLine);
    if (!request.TryGetProperty("id", out var id)) continue;
    if (scenario == "disconnect") return;
    if (scenario == "timeout" && method == "thread/read") { await Task.Delay(TimeSpan.FromMinutes(5)); continue; }
    if (scenario == "error" && method == "thread/read")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { id, error = new { code = -32000, message = "mock read failure" } }));
        continue;
    }
    if (scenario == "request" && method == "thread/read")
    {
        Console.WriteLine("{\"id\":\"approval-1\",\"method\":\"item/commandExecution/requestApproval\",\"params\":{}}");
        continue;
    }
    object result;
    switch (method)
    {
        case "initialize": result = new { userAgent = "mock/0.1" }; break;
        case "thread/loaded/list":
            result = new { data = scenario == "ambiguous-session" ? new[] { "mock-thread", "mock-thread-2" } : new[] { "mock-thread" }, nextCursor = (string?)null };
            break;
        case "thread/read":
            reads++;
            var running = scenario == "running" || (sent > 0 && reads < 10);
            var status = scenario == "blocked" ? new { type = "active", activeFlags = new[] { "waitingOnApproval" } }
                : new { type = running ? "active" : "idle", activeFlags = Array.Empty<string>() };
            result = new { thread = new { id = request.GetProperty("params").GetProperty("threadId").GetString(), cwd = Environment.CurrentDirectory, status } };
            break;
        case "thread/turns/list":
            var complete = scenario == "already-complete" || scenario == "complete" && sent > 0;
            var text = complete ? "Done.\nWATCHDOG: COMPLETED" : scenario == "uncertain" ? "Some progress made." : "Work remains.\nWATCHDOG: CONTINUE";
            object[] items = complete ?
                [new { type = "commandExecution", command = "dotnet build", status = "completed", exitCode = 0 },
                 new { type = "commandExecution", command = "dotnet test", status = "completed", exitCode = 0 },
                 new { type = "agentMessage", phase = "final_answer", text }]
                : [new { type = "agentMessage", phase = "final_answer", text }];
            result = new { data = new[] { new { id = "turn-" + sent, status = "completed", itemsView = "full", items } }, nextCursor = (string?)null };
            break;
        case "turn/start":
            sent++;
            if (scenario == "lost-send") return;
            result = new { turn = new { id = "turn-" + sent, status = "inProgress", items = Array.Empty<object>() } };
            break;
        default:
            Console.WriteLine(JsonSerializer.Serialize(new { id, error = new { code = -32601, message = "Unsupported method" } }));
            continue;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
}
