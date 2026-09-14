using System.Text.Json;

namespace CodexWatchdog;

public interface ICodexSession
{
    Task<Observation> ObserveAsync(CancellationToken token);
    Task ContinueAsync(Observation expected, CancellationToken token);
}

public sealed class CodexSession(RpcConnection connection, string threadId) : ICodexSession
{
    public static async Task<string> SelectAsync(RpcConnection connection, string? requested, CancellationToken token)
    {
        var candidates = new List<string>();
        string? cursor = null;
        do
        {
            var loaded = await connection.CallAsync("thread/loaded/list", new { cursor, limit = 100 }, token);
            foreach (var idElement in loaded.GetProperty("data").EnumerateArray())
            {
                var id = idElement.GetString() ?? throw new JsonException("Missing thread id");
                if (requested is not null && id != requested) continue;
                var result = await connection.CallAsync("thread/read", new { threadId = id, includeTurns = false }, token);
                var thread = result.GetProperty("thread");
                var cwd = thread.GetProperty("cwd").GetString() ?? "";
                if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)).Equals(
                    Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    candidates.Add(id);
            }
            cursor = loaded.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        } while (cursor is not null);
        return candidates.Count == 1 ? candidates[0] : throw new IOException(
            $"Expected one loaded session in current directory, found {candidates.Count}. Specify --thread UUID. Candidates: {string.Join(", ", candidates)}");
    }

    public async Task<Observation> ObserveAsync(CancellationToken token)
    {
        var result = await connection.CallAsync("thread/read", new { threadId, includeTurns = false }, token);
        var thread = result.GetProperty("thread");
        if (thread.GetProperty("status").GetProperty("type").GetString() != "idle")
            return StateDetector.Detect(thread, null);
        var page = await connection.CallAsync("thread/turns/list",
            new { threadId, limit = 1, sortDirection = "desc", itemsView = "full" }, token);
        var turns = page.GetProperty("data");
        // Metadata is checked again because another client may have started a turn during the history read.
        result = await connection.CallAsync("thread/read", new { threadId, includeTurns = false }, token);
        return StateDetector.Detect(result.GetProperty("thread"), turns.GetArrayLength() == 0 ? null : turns[0]);
    }

    public async Task ContinueAsync(Observation expected, CancellationToken token)
    {
        var current = await ObserveAsync(token);
        if (current.State != WatchdogState.WAITING || current.TurnId != expected.TurnId
            || current.NeedsHuman || current.ClaimsComplete || current.Output != expected.Output)
            throw new HumanActionRequiredException("Session changed before send; stopping without continuation");
        await connection.CallAsync("turn/start", new
        {
            threadId,
            input = new[] { new { type = "text", text = ContinuationController.Message } },
            approvalPolicy = "untrusted",
            approvalsReviewer = "user",
            sandboxPolicy = new { type = "workspaceWrite", networkAccess = false, writableRoots = Array.Empty<string>(), excludeTmpdirEnvVar = true, excludeSlashTmp = true }
        }, token);
    }
}
