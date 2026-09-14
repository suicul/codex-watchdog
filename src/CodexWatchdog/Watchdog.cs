using System.Diagnostics;

namespace CodexWatchdog;

public sealed class Watchdog(Config config, Logger logger)
{
    public WatchdogState State { get; private set; } = WatchdogState.STOPPED;
    public int Errors { get; private set; }
    public ContinuationController Continuations { get; } = new();

    public async Task<int> RunAsync(ICodexSession session, CancellationToken token)
    {
        var runtime = Stopwatch.StartNew();
        var wait = Stopwatch.StartNew();
        string? previous = null;
        string? failedTurn = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(config.Runtime);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (File.Exists(config.StopFile)) return Stop("Manual stop file detected");
                var reason = SafetyController.StopReason(config, runtime.Elapsed, wait.Elapsed,
                    Continuations.Iterations, Errors, Continuations.UncertainIterations);
                if (reason is not null) return Stop(reason, Errors >= config.MaxErrors ? 1 : 0);
                Observation observation;
                try
                {
                    observation = await session.ObserveAsync(deadline.Token);
                }
                catch (HumanActionRequiredException error)
                {
                    return Stop(error.Message, 2);
                }
                catch (Exception error) when (error is IOException or TimeoutException or System.Text.Json.JsonException)
                {
                    Errors++;
                    Continuations.Ready(new(WatchdogState.ERROR, "", "Read failed"));
                    Transition(WatchdogState.ERROR, "Observation failed", error: error.Message);
                    await PauseAsync(deadline.Token);
                    continue;
                }
                var fingerprint = $"{observation.State}:{observation.TurnId}:{observation.Output}";
                if (fingerprint != previous)
                {
                    wait.Restart();
                    previous = fingerprint;
                }
                Transition(observation.State,
                    $"Runtime={runtime.Elapsed:hh\\:mm\\:ss}; Errors={Errors}/{config.MaxErrors}; Build={observation.Build}; Tests={observation.Tests}; {observation.Output}");
                var terminal = SafetyController.Terminal(observation);
                if (terminal is not null)
                {
                    Transition(terminal.Value, observation.ClaimsComplete
                        ? "Completion claimed; no further prompts. Build/test evidence: " + observation.Build + "/" + observation.Tests
                        : "Human action or unsupported state; no further prompts");
                    return terminal == WatchdogState.COMPLETED ? 0 : 2;
                }
                if (observation.State == WatchdogState.ERROR)
                {
                    // A failed turn is not three different errors just because it was polled three times.
                    if (!string.IsNullOrEmpty(observation.TurnId))
                    {
                        failedTurn = observation.TurnId;
                        Errors++;
                        return Stop($"Failed turn {failedTurn}; not retrying task automatically", 1);
                    }
                    Errors++;
                }
                else Errors = 0;
                if (Continuations.Ready(observation))
                {
                    if (config.DryRun)
                    {
                        Transition(WatchdogState.WAITING, "Dry-run decision", "Would send continuation; no mutation sent");
                        return Stop("Dry-run finished after first continuation decision");
                    }
                    if (File.Exists(config.StopFile)) return Stop("Manual stop before send");
                    deadline.Token.ThrowIfCancellationRequested();
                    Continuations.RecordAttempt(observation);
                    Transition(WatchdogState.CONTINUING, observation.TurnId, "Sending continuation");
                    try { await session.ContinueAsync(observation, deadline.Token); }
                    catch (HumanActionRequiredException error) { return Stop(error.Message, 2); }
                    catch (Exception error) when (error is IOException or TimeoutException or System.Text.Json.JsonException)
                    {
                        Errors++;
                        Transition(WatchdogState.ERROR, "Send outcome unknown; never retry automatically", error: error.Message);
                        return Stop("Restart only after inspecting Codex", 1);
                    }
                    wait.Restart();
                }
                await PauseAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return Stop(token.IsCancellationRequested ? "Graceful shutdown requested" : "Maximum runtime reached");
        }
    }

    private async Task PauseAsync(CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < config.Interval)
        {
            if (File.Exists(config.StopFile)) return;
            var remaining = config.Interval - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200), token);
        }
    }

    private int Stop(string reason, int code = 0)
    {
        Transition(WatchdogState.STOPPED, reason);
        return code;
    }

    private void Transition(WatchdogState state, string evt, string action = "", string error = "")
    {
        State = state;
        logger.Write(state, Continuations.Iterations, evt, action, error);
    }
}
