using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexWatchdog;

public sealed class Logger : IDisposable
{
    private readonly StreamWriter writer;
    private readonly bool verbose;
    private readonly object sync = new();

    public Logger(string path, bool verbose)
    {
        this.verbose = verbose;
        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Write(WatchdogState state, int iteration, string evt, string action = "", string error = "")
    {
        var timestamp = DateTimeOffset.Now;
        var entry = JsonSerializer.Serialize(new
        {
            timestamp,
            state = state.ToString(),
            iteration,
            @event = evt,
            action,
            error
        });
        lock (sync)
        {
            writer.WriteLine(entry);
            var limit = verbose ? 2000 : 400;
            var message = $"[{timestamp:HH:mm:ss}] State: {state} | Iteration: {iteration} | {SafeText(evt, limit)}";
            if (action.Length > 0) message += $" | Action: {SafeText(action, limit)}";
            if (error.Length > 0) message += $" | Error: {SafeText(error, limit)}";
            Console.WriteLine(message);
        }
    }

    private static string SafeText(string value, int limit)
    {
        var text = new StringBuilder();
        foreach (var character in value)
        {
            if (text.Length >= limit)
            {
                text.Append("...");
                break;
            }
            if (char.IsControl(character) || char.GetUnicodeCategory(character) is
                UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else
                text.Append(character);
        }
        return text.ToString();
    }

    public void Dispose()
    {
        lock (sync) writer.Dispose();
    }
}
