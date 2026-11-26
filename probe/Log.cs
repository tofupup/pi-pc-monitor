using System.Text;

namespace PcmonProbe;

/// <summary>Appends timestamped lines to C:\ProgramData\pcmon-probe\probe.log, flushed on every line.</summary>
static class Log
{
    static readonly object Gate = new();
    static readonly Dictionary<string, DateTime> LastThrottled = new();
    static FileStream? _file;
    static StreamWriter? _writer;
    static bool _echo;

    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pcmon-probe");

    public static void Open(bool echo)
    {
        _echo = echo;
        Directory.CreateDirectory(Dir);
        _file = new FileStream(Path.Combine(Dir, "probe.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(_file, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public static void Line(string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff} {text}";
        lock (Gate)
        {
            try { _writer?.WriteLine(line); } catch { /* never let logging kill the probe */ }
            if (_echo) Console.WriteLine(line);
        }
    }

    /// <summary>Logs at most once a minute per key, so a persistent failure doesn't flood the log.</summary>
    public static void Throttled(string key, string text)
    {
        lock (Gate)
        {
            if (LastThrottled.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
            LastThrottled[key] = DateTime.UtcNow;
        }
        Line(text);
    }

    /// <summary>Forces the log to disk, for lines written just before shutdown or sleep.</summary>
    public static void Hard()
    {
        lock (Gate)
        {
            try { _writer?.Flush(); _file?.Flush(flushToDisk: true); } catch { }
        }
    }
}
