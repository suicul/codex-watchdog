using System.Globalization;

namespace CodexWatchdog;

public sealed record Config
{
    public TimeSpan Runtime { get; init; } = TimeSpan.FromHours(8);
    public int MaxIterations { get; init; } = 30;
    public int MaxErrors { get; init; } = 3;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan RpcTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public int MaxUncertain { get; init; } = 3;
    public bool Verbose { get; init; }
    public bool DryRun { get; init; }
    public string? ThreadId { get; init; }
    public string? Endpoint { get; init; }
    public string CodexPath { get; init; } = "codex";
    public string LogPath { get; init; } = "codex-watchdog.jsonl";
    public string Project { get; init; } = Environment.CurrentDirectory;
    public string? MockScenario { get; init; }
    public bool List { get; init; }
    public string StopFile { get; init; } = "codex-watchdog.stop";

    public static Config Parse(string[] args)
    {
        var configuration = new Config();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option))
                throw new ArgumentException($"Option repeated: {option}");
            if (option == "--list")
            {
                configuration = configuration with { List = true };
                continue;
            }
            if (option == "--verbose")
            {
                configuration = configuration with { Verbose = true };
                continue;
            }
            if (option == "--dry-run")
            {
                configuration = configuration with { DryRun = true };
                continue;
            }
            if (option is not ("--runtime" or "--max-iterations" or "--max-errors"
                or "--interval" or "--wait-timeout" or "--request-timeout"
                or "--max-uncertain-iterations" or "--thread" or "--endpoint"
                or "--codex-path" or "--log" or "--stop-file" or "--project"
                or "--mock-scenario" or "--rpc-timeout" or "--max-uncertain"))
                throw new ArgumentException($"Unknown option: {option}");
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {option}");
            var value = args[index];
            configuration = option switch
            {
                "--project" => configuration with { Project = Path.GetFullPath(value) },
                "--mock-scenario" => configuration with { MockScenario = value },
                "--rpc-timeout" => configuration with { RpcTimeout = Duration(value, option) },
                "--max-uncertain" => configuration with { MaxUncertain = PositiveInteger(value, option) },
                "--runtime" => configuration with { Runtime = Duration(value, option) },
                "--max-iterations" => configuration with { MaxIterations = PositiveInteger(value, option) },
                "--max-errors" => configuration with { MaxErrors = PositiveInteger(value, option) },
                "--interval" => configuration with { Interval = Duration(value, option) },
                "--wait-timeout" => configuration with { WaitTimeout = Duration(value, option) },
                "--request-timeout" => configuration with { RpcTimeout = Duration(value, option) },
                "--max-uncertain-iterations" => configuration with { MaxUncertain = PositiveInteger(value, option) },
                "--thread" => configuration with { ThreadId = value },
                "--endpoint" => configuration with { Endpoint = value },
                "--codex-path" => configuration with { CodexPath = value },
                "--log" => configuration with { LogPath = value },
                "--stop-file" => configuration with { StopFile = value },
                _ => throw new ArgumentException($"Unknown option: {option}")
            };
        }
        return configuration;
    }

    private static int PositiveInteger(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new ArgumentException($"{option} requires a positive integer.");
        return number;
    }

    private static TimeSpan Duration(string value, string option)
    {
        var suffixLength = value.EndsWith("ms", StringComparison.Ordinal) ? 2 : 1;
        var unit = value.Length >= suffixLength ? value[^suffixLength..] : "";
        var multiplier = unit switch
        {
            "ms" => TimeSpan.TicksPerMillisecond,
            "s" => TimeSpan.TicksPerSecond,
            "m" => TimeSpan.TicksPerMinute,
            "h" => TimeSpan.TicksPerHour,
            _ => 0L
        };
        if (value.Length <= suffixLength || multiplier == 0
            || !decimal.TryParse(value[..^suffixLength], NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount)
            || amount <= 0 || amount > (decimal)long.MaxValue / multiplier)
            throw new ArgumentException($"{option} requires a positive duration such as 8h, 30m, 10s or 100ms.");
        var ticks = decimal.Truncate(amount * multiplier);
        if (ticks < 1)
            throw new ArgumentException($"{option} duration is too small.");
        return TimeSpan.FromTicks((long)ticks);
    }
}
