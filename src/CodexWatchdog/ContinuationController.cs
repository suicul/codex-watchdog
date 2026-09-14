namespace CodexWatchdog;

public sealed class ContinuationController
{
    public const string Message = "Продолжай выполнение текущей задачи. Не останавливайся без необходимости. Если обнаружил проблему — самостоятельно исследуй её, исправь и продолжи. Перед завершением обязательно проверь результат сборкой и тестами.\n"
        + "Если задача уже выполнена, не вноси дополнительных изменений. Не выполняй git push, force push, удаление репозитория, массовое удаление файлов, изменение системных настроек, shutdown/reboot. Не обходи подтверждения. Если нужно решение человека, остановись. Последней строкой ответа укажи ровно WATCHDOG: COMPLETED, WATCHDOG: CONTINUE или WATCHDOG: BLOCKED. COMPLETED допустим только после проверки результата. CONTINUE означает, что конкретная работа ещё осталась. BLOCKED означает, что требуется человек.";
    private readonly HashSet<string> sent = [];
    private string? candidate;
    public int Iterations { get; private set; }
    public int UncertainIterations { get; private set; }

    public bool Ready(Observation observation)
    {
        if (observation.State != WatchdogState.WAITING || string.IsNullOrEmpty(observation.TurnId))
        {
            candidate = null;
            return false;
        }
        var stable = candidate == observation.TurnId;
        candidate = observation.TurnId;
        return stable && !sent.Contains(observation.TurnId);
    }

    public void RecordAttempt(Observation observation)
    {
        if (!sent.Add(observation.TurnId)) throw new InvalidOperationException("Turn already continued");
        Iterations++;
        if (!observation.ExplicitContinue) UncertainIterations++;
        candidate = null;
    }
}

public static class SafetyController
{
    public static string? StopReason(Config config, TimeSpan elapsed, TimeSpan waiting,
        int iterations, int errors, int uncertain)
    {
        if (elapsed >= config.Runtime) return "Maximum runtime reached";
        if (iterations >= config.MaxIterations) return "Maximum continuation iterations reached";
        if (errors >= config.MaxErrors) return "Maximum consecutive errors reached";
        if (waiting >= config.WaitTimeout) return "State wait timeout; no continuation sent";
        if (uncertain >= config.MaxUncertain) return "Completion remains uncertain; manual review required";
        return null;
    }

    public static WatchdogState? Terminal(Observation observation)
    {
        if (observation.NeedsHuman) return WatchdogState.STOPPED;
        if (observation.State == WatchdogState.STOPPED) return WatchdogState.STOPPED;
        if (!observation.ClaimsComplete) return null;
        return observation.Build == Evidence.PASS && observation.Tests == Evidence.PASS
            ? WatchdogState.COMPLETED : WatchdogState.STOPPED;
    }
}
