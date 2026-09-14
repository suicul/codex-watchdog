using System.Text;

namespace CodexWatchdog;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine(Help);
            return 0;
        }
        Logger? logger = null;
        try
        {
            var config = Config.Parse(args);
            if (config.MockScenario is not null) throw new ArgumentException("Use tests/CodexWatchdog.Mock for mock scenarios.");
            Environment.CurrentDirectory = config.Project;
            Console.OutputEncoding = Encoding.UTF8;
            logger = new Logger(config.LogPath, config.Verbose);
            logger.Write(WatchdogState.STOPPED, 0, "TOLIK CODEX WATCHDOG v0.1 | Agent: Codex | Ctrl+C or stop file to stop");
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, evt) => { evt.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                startup.CancelAfter(config.RpcTimeout);
                await ProcessDetector.ShowGitAsync(logger, startup.Token);
                if (File.Exists(config.StopFile))
                {
                    logger.Write(WatchdogState.STOPPED, 0, "Stop file already exists; remove it explicitly to restart");
                    return 0;
                }
                await using var connection = new RpcConnection(config.RpcTimeout);
                await connection.ConnectAsync(config, stop.Token);
                if (config.List)
                {
                    var result = await connection.CallAsync("thread/loaded/list", new { }, stop.Token);
                    logger.Write(WatchdogState.STOPPED, 0, "Loaded thread IDs: " + result);
                    return 0;
                }
                var threadId = await CodexSession.SelectAsync(connection, config.ThreadId, stop.Token);
                using var sessionLock = new SessionLock(threadId);
                logger.Write(WatchdogState.WAITING, 0, $"Codex detected: thread {threadId}; project {config.Project}");
                return await new Watchdog(config, logger).RunAsync(new CodexSession(connection, threadId), stop.Token);
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        catch (OperationCanceledException)
        {
            logger?.Write(WatchdogState.STOPPED, 0, "Shutdown or startup timeout; no further requests");
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or TimeoutException
            or System.Text.Json.JsonException or System.ComponentModel.Win32Exception
            or System.Net.WebSockets.WebSocketException or HumanActionRequiredException)
        {
            if (logger is not null) logger.Write(WatchdogState.ERROR, 0, "Startup/connection failure", error: error.Message);
            else Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally { logger?.Dispose(); }
    }

    public const string Help = """
        TOLIK CODEX WATCHDOG 0.1 (.NET 10)
        codex-watchdog [options]
          --runtime 8h             Maximum watchdog runtime
          --max-iterations 30      Maximum continuation attempts (not polls)
          --max-errors 3           Maximum consecutive observation errors
          --interval 5s            Poll interval
          --wait-timeout 30m       Stop if observation has not changed
          --rpc-timeout 20s        Deadline for each server request
          --max-uncertain 3        Limit continuations without explicit CONTINUE marker
          --thread UUID            Exact loaded session; otherwise require one in project
          --project PATH           Git project (default: current directory)
          --endpoint ws://127.0.0.1:4500  Existing local app-server (default: daemon proxy)
          --codex-path PATH        Native codex executable for daemon proxy
          --list                   List loaded thread IDs, do not continue
          --log PATH               Append JSONL log (default: codex-watchdog.jsonl)
          --stop-file PATH         Stop when file exists (default: codex-watchdog.stop)
          --verbose                Longer console events
          --dry-run                Observe only; stop after first proposed continuation
          --help                   Show help
        Stop disconnects watchdog; it does NOT interrupt or kill your running Codex task.
        Use a single writer: do not type into the same Codex session during watchdog use.
        """;
}
