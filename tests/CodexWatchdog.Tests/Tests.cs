using System.Text.Json;
using CodexWatchdog;
using Xunit;

public class Tests
{
    private static Observation Waiting(string id = "t1", bool explicitContinue = true) =>
        new(WatchdogState.WAITING, id, "Work remains", ExplicitContinue: explicitContinue);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static Observation Detect(string status, string items = "[]", string turnStatus = "completed") =>
        StateDetector.Detect(Json($$"""{"status":{{status}}}"""),
            Json($$"""{"id":"t1","status":"{{turnStatus}}","itemsView":"full","items":{{items}}}"""));

    [Fact] public void Defaults() { var c = Config.Parse([]); Assert.Equal(30, c.MaxIterations); Assert.Equal(3, c.MaxErrors); Assert.Equal(TimeSpan.FromHours(8), c.Runtime); }
    [Fact] public void Parses() { var c = Config.Parse(["--runtime", "2h", "--max-iterations", "4", "--dry-run", "--interval", "100ms"]); Assert.Equal(TimeSpan.FromHours(2), c.Runtime); Assert.Equal(4, c.MaxIterations); Assert.True(c.DryRun); Assert.Equal(TimeSpan.FromMilliseconds(100), c.Interval); }
    [Theory]
    [InlineData("--runtime", "0h")][InlineData("--runtime", "NaNh")][InlineData("--runtime", "-1h")]
    [InlineData("--interval", "abc")][InlineData("--max-errors", "0")][InlineData("--max-iterations", "-2")]
    [InlineData("--unknown", "1")]
    public void RejectsBadConfig(string flag, string value) => Assert.Throws<ArgumentException>(() => Config.Parse([flag, value]));
    [Fact] public void RejectsMissingOrDuplicate() { Assert.Throws<ArgumentException>(() => Config.Parse(["--runtime"])); Assert.Throws<ArgumentException>(() => Config.Parse(["--dry-run", "--dry-run"])); }
    [Fact] public void NoPromptHeuristics() => Assert.Equal(WatchdogState.RUNNING, Detect("{\"type\":\"active\",\"activeFlags\":[]}", "[{\"type\":\"agentMessage\",\"text\":\"codex>\"}]").State);
    [Theory][InlineData("waitingOnApproval")][InlineData("waitingOnUserInput")]
    public void StopsAtUserAction(string flag) => Assert.True(Detect($$"""{"type":"active","activeFlags":["{{flag}}"]}""").NeedsHuman);
    [Fact] public void IdleCompletedTurnIsWaiting() => Assert.Equal(WatchdogState.WAITING, Detect("{\"type\":\"idle\"}").State);
    [Fact] public void FailedTurnIsError() => Assert.Equal(WatchdogState.ERROR, Detect("{\"type\":\"idle\"}", turnStatus: "failed").State);
    [Fact] public void InterruptedStops() => Assert.Equal(WatchdogState.STOPPED, Detect("{\"type\":\"idle\"}", turnStatus: "interrupted").State);
    [Fact] public void NotLoadedStops() => Assert.Equal(WatchdogState.STOPPED, Detect("{\"type\":\"notLoaded\"}").State);
    [Fact] public void BuildTextIsNotCompletion()
    {
        var o = Detect("{\"type\":\"idle\"}", "[{\"type\":\"agentMessage\",\"text\":\"Build succeeded; all tests passed\"}]");
        Assert.False(o.ClaimsComplete); Assert.Equal(Evidence.UNKNOWN, o.Build); Assert.Null(SafetyController.Terminal(o));
    }
    [Fact] public void VerifiedCompletion()
    {
        var o = Detect("{\"type\":\"idle\"}", """
            [{"type":"commandExecution","command":"dotnet build","status":"completed","exitCode":0},
             {"type":"commandExecution","command":"dotnet test","status":"completed","exitCode":0},
             {"type":"agentMessage","text":"Verified.\nWATCHDOG: COMPLETED"}]
            """);
        Assert.Equal(WatchdogState.COMPLETED, SafetyController.Terminal(o));
    }
    [Fact] public void UnverifiedClaimStillStops()
    {
        var o = Detect("{\"type\":\"idle\"}", "[{\"type\":\"agentMessage\",\"text\":\"Задача завершена.\"}]");
        Assert.True(o.ClaimsComplete); Assert.Equal(WatchdogState.STOPPED, SafetyController.Terminal(o));
    }
    [Fact] public void CompoundCommandIsNotProof()
    {
        var o = Detect("{\"type\":\"idle\"}", "[{\"type\":\"commandExecution\",\"command\":\"dotnet build || true\",\"status\":\"completed\",\"exitCode\":0}]");
        Assert.Equal(Evidence.UNKNOWN, o.Build);
    }
    [Fact] public void HumanQuestionOverridesDone() => Assert.Equal(WatchdogState.STOPPED,
        SafetyController.Terminal(Waiting() with { NeedsHuman = true, ClaimsComplete = true, Build = Evidence.PASS, Tests = Evidence.PASS }));
    [Fact] public void TwoPollsAndDedup()
    {
        var c = new ContinuationController(); var o = Waiting();
        Assert.False(c.Ready(o)); Assert.True(c.Ready(o)); c.RecordAttempt(o);
        Assert.False(c.Ready(o)); Assert.False(c.Ready(o)); Assert.Equal(1, c.Iterations);
        Assert.Throws<InvalidOperationException>(() => c.RecordAttempt(o));
    }
    [Fact] public void RunningResetsStableCandidate()
    {
        var c = new ContinuationController(); c.Ready(Waiting());
        c.Ready(new(WatchdogState.RUNNING, "", "long build")); Assert.False(c.Ready(Waiting()));
    }
    [Fact] public void Limits()
    {
        var c = new Config();
        Assert.Contains("runtime", SafetyController.StopReason(c, c.Runtime, TimeSpan.Zero, 0, 0, 0));
        Assert.Contains("iterations", SafetyController.StopReason(c, TimeSpan.Zero, TimeSpan.Zero, 30, 0, 0));
        Assert.Contains("errors", SafetyController.StopReason(c, TimeSpan.Zero, TimeSpan.Zero, 0, 3, 0));
        Assert.Contains("timeout", SafetyController.StopReason(c, TimeSpan.Zero, c.WaitTimeout, 0, 0, 0));
        Assert.Contains("uncertain", SafetyController.StopReason(c, TimeSpan.Zero, TimeSpan.Zero, 0, 0, 3));
    }

    private sealed class Fake(Func<int, Observation> observe, bool failSend = false) : ICodexSession
    {
        public int Sent { get; private set; }
        public Task<Observation> ObserveAsync(CancellationToken token) => Task.FromResult(observe(Sent));
        public Task ContinueAsync(Observation expected, CancellationToken token)
        {
            Sent++;
            if (failSend) throw new IOException("ambiguous transport failure");
            return Task.CompletedTask;
        }
    }
    private static async Task<(WatchdogState State, int Sends, int Code)> Run(Fake fake, Config? configuration = null, CancellationToken token = default)
    {
        var file = Path.GetTempFileName();
        try
        {
            using var log = new Logger(file, false);
            var config = (configuration ?? new Config()) with { Interval = TimeSpan.FromMilliseconds(1), StopFile = file + ".stop" };
            var watchdog = new Watchdog(config, log);
            var code = await watchdog.RunAsync(fake, token);
            return (watchdog.State, fake.Sent, code);
        }
        finally { File.Delete(file); }
    }
    [Fact] public async Task FullStateMachine()
    {
        var f = new Fake(n => n == 0 ? Waiting() : Waiting("t2") with { ClaimsComplete = true, Build = Evidence.PASS, Tests = Evidence.PASS });
        var r = await Run(f); Assert.Equal(WatchdogState.COMPLETED, r.State); Assert.Equal(1, r.Sends);
    }
    [Fact] public async Task MaxIterationsCountsSendsOnly()
    {
        var r = await Run(new Fake(n => Waiting("t" + n)), new Config { MaxIterations = 2 });
        Assert.Equal(2, r.Sends); Assert.Equal(WatchdogState.STOPPED, r.State);
    }
    [Fact] public async Task DryRunNeverSends() => Assert.Equal(0, (await Run(new Fake(_ => Waiting()), new Config { DryRun = true })).Sends);
    [Fact] public async Task RuntimeDoesNotSendToBusy() => Assert.Equal(0, (await Run(new Fake(_ => new(WatchdogState.RUNNING, "", "busy")), new Config { Runtime = TimeSpan.FromMilliseconds(20) })).Sends);
    [Fact] public async Task TimeoutDoesNotSend() => Assert.Equal(0, (await Run(new Fake(_ => new(WatchdogState.RUNNING, "", "busy")), new Config { WaitTimeout = TimeSpan.FromMilliseconds(10) })).Sends);
    [Fact] public async Task MaxErrorsStops()
    {
        var calls = 0;
        var r = await Run(new Fake(_ => { calls++; throw new IOException("offline"); }), new Config { MaxErrors = 3 });
        Assert.Equal(3, calls); Assert.Equal(1, r.Code); Assert.Equal(0, r.Sends);
    }
    [Fact] public async Task AmbiguousSendIsNeverRetried()
    {
        var r = await Run(new Fake(_ => Waiting(), true)); Assert.Equal(1, r.Sends); Assert.Equal(1, r.Code);
    }
    [Fact] public async Task CancellationStops()
    {
        using var c = new CancellationTokenSource(); c.Cancel();
        Assert.Equal(WatchdogState.STOPPED, (await Run(new Fake(_ => Waiting()), token: c.Token)).State);
    }
    [Fact] public async Task UncertaintyBudgetStops()
    {
        var r = await Run(new Fake(n => Waiting("t" + n, false)), new Config { MaxUncertain = 2 }); Assert.Equal(2, r.Sends);
    }
}
