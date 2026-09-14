using System.Diagnostics;

namespace CodexWatchdog;

public static class ProcessDetector
{
    public static int[] FindCodex()
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName("codex"))
        {
            using (process) ids.Add(process.Id);
        }
        return [.. ids];
    }

    public static Process Start(string executable, IEnumerable<string> arguments, bool redirectInput = false)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows() && Path.GetExtension(executable) is ".cmd" or ".bat")
            throw new ArgumentException("Use native codex.exe with --codex-path; .cmd/.bat shell wrappers are unsupported.");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException($"Cannot start {executable}");
    }

    public static async Task<(int ExitCode, string Output)> CaptureAsync(string executable,
        string[] arguments, CancellationToken token)
    {
        using var process = Start(executable, arguments);
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            return (process.ExitCode, (await stdout).Trim() + "\n" + (await stderr).Trim());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public static async Task ShowGitAsync(Logger logger, CancellationToken token)
    {
        logger.Write(WatchdogState.STOPPED, 0, $"Directory: {Environment.CurrentDirectory}; Codex PIDs (informational): {string.Join(',', FindCodex())}");
        foreach (var arguments in new[]
        {
            new[] { "rev-parse", "--show-toplevel" },
            new[] { "branch", "--show-current" },
            new[] { "status", "--short", "--branch" }
        })
        {
            var result = await CaptureAsync("git", ["--no-optional-locks", .. arguments], token);
            logger.Write(WatchdogState.STOPPED, 0, $"git {string.Join(' ', arguments)}: {result.Output}");
            if (result.ExitCode != 0) throw new IOException("Run watchdog inside a Git repository with git available. No checkpoint created.");
        }
    }
}
