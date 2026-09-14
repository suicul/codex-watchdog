using System.Security.Cryptography;
using System.Text;

namespace CodexWatchdog;

public sealed class SessionLock : IDisposable
{
    private readonly FileStream stream;
    public SessionLock(string threadId)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexWatchdog", "locks");
        Directory.CreateDirectory(directory);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(threadId)));
        try { stream = new FileStream(Path.Combine(directory, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("Another watchdog owns this thread. Stop it first.", error); }
    }
    public void Dispose() => stream.Dispose();
}
