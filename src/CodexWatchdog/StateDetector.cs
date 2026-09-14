using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexWatchdog;

public enum WatchdogState { RUNNING, WAITING, CONTINUING, ERROR, COMPLETED, STOPPED }
public enum Evidence { UNKNOWN, PASS, FAIL }
public sealed record Observation(WatchdogState State, string TurnId, string Output,
    Evidence Build = Evidence.UNKNOWN, Evidence Tests = Evidence.UNKNOWN,
    bool ClaimsComplete = false, bool NeedsHuman = false, bool ExplicitContinue = false);

public static class StateDetector
{
    public static Observation Detect(JsonElement thread, JsonElement? turn)
    {
        var status = thread.GetProperty("status");
        var kind = status.GetProperty("type").GetString();
        if (kind == "active")
        {
            var blocked = status.GetProperty("activeFlags").GetArrayLength() > 0;
            return new(blocked ? WatchdogState.STOPPED : WatchdogState.RUNNING, "",
                blocked ? "Codex requests approval or user input" : "Codex is active", NeedsHuman: blocked);
        }
        if (kind == "systemError") return new(WatchdogState.ERROR, "", "Codex system error");
        if (kind != "idle") return new(WatchdogState.STOPPED, "", $"Unsupported/inactive thread status: {kind}");
        if (turn is not { } last) return new(WatchdogState.STOPPED, "", "No previous turn; no task to continue");
        var id = last.GetProperty("id").GetString() ?? throw new JsonException("Missing turn id");
        var turnStatus = last.GetProperty("status").GetString();
        if (turnStatus == "inProgress") return new(WatchdogState.RUNNING, id, "Turn still in progress");
        if (turnStatus == "failed") return new(WatchdogState.ERROR, id, "Codex turn failed; manual investigation required");
        if (turnStatus != "completed") return new(WatchdogState.STOPPED, id, $"Turn {turnStatus}; do not override interruption");
        if (last.TryGetProperty("itemsView", out var view) && view.GetString() != "full")
            return new(WatchdogState.STOPPED, id, "Incomplete turn history");
        var output = "";
        var build = Evidence.UNKNOWN;
        var tests = Evidence.UNKNOWN;
        var human = false;
        foreach (var item in last.GetProperty("items").EnumerateArray())
        {
            switch (item.GetProperty("type").GetString())
            {
                case "agentMessage":
                    if (item.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() > 0)
                        human = true;
                    if (!item.TryGetProperty("phase", out var phase) || phase.GetString() != "commentary")
                        output = item.GetProperty("text").GetString() ?? "";
                    break;
                case "commandExecution":
                    if (item.GetProperty("status").GetString() == "inProgress")
                        return new(WatchdogState.RUNNING, id, "Command still running");
                    var command = item.GetProperty("command").GetString() ?? "";
                    var result = item.TryGetProperty("exitCode", out var exit) && exit.TryGetInt32(out var code)
                        ? code == 0 ? Evidence.PASS : Evidence.FAIL : Evidence.UNKNOWN;
                    // Only recognize a single simple command, never output strings or compound shells.
                    if (Regex.IsMatch(command, @"^[\w./:\\ -]+$", RegexOptions.CultureInvariant))
                    {
                        if (Regex.IsMatch(command, @"^(dotnet|cargo|npm|pnpm|yarn) (run )?build( |$)")) build = result;
                        if (Regex.IsMatch(command, @"^((dotnet|cargo|npm|pnpm|yarn) (run )?test|pytest)( |$)")) tests = result;
                    }
                    break;
            }
        }
        var line = output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
        var complete = line == "WATCHDOG: COMPLETED" || Regex.IsMatch(output,
            @"(?im)^\s*(task (is )?completed|all requested work (is )?completed|задача (полностью )?(завершена|выполнена))[.!\s]*$");
        human |= line == "WATCHDOG: BLOCKED" || Regex.IsMatch(output,
            @"(?i)(\?|подтверд|разрешени|нужен ваш|нужно ваше|git\s+push|force.push|rm\s+-rf|shutdown|reboot|approve|permission|confirm)");
        return new(WatchdogState.WAITING, id, output, build, tests, complete, human,
            line == "WATCHDOG: CONTINUE");
    }
}
