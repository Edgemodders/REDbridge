using WolvenKit.Common;
using WolvenKit.Core.Interfaces;

namespace REDbridge;

/// <summary>
/// WolvenKit's logger interface, written to stderr so stdout stays free for the protocol.
/// One line per message: a timestamp, a level, the text.
/// </summary>
public sealed class StderrLoggerService : ILoggerService
{
    private readonly object _gate = new();

    public LoggerVerbosity LoggerVerbosity { get; private set; } = LoggerVerbosity.Normal;

    public void SetLoggerVerbosity(LoggerVerbosity verbosity) => LoggerVerbosity = verbosity;

    // The lines a call wants back (bridge 0.15.0, #14): set by Capture for the calling flow only, so
    // what WolvenKit says during one call does not leak into another running at the same time.
    private static readonly AsyncLocal<List<(string Level, string Text)>?> Captured = new();

    /// <summary>Collects this flow's info, warning and error lines into <paramref name="into"/> until disposed.</summary>
    public static IDisposable Capture(List<(string Level, string Text)> into)
    {
        var before = Captured.Value;
        Captured.Value = into;
        return new Restore(() => Captured.Value = before);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public void Info(string s) => Write("info ", s);
    public void Warning(string s) => Write("warn ", s);
    public void Error(string msg) => Write("error", msg);
    public void Error(Exception exception) => Write("error", exception.ToString());
    public void Success(string msg) => Write("ok   ", msg);

    public void Debug(string msg)
    {
        if (LoggerVerbosity >= LoggerVerbosity.Diagnostic)
        {
            Write("debug", msg);
        }
    }

    private void Write(string level, string text)
    {
        lock (_gate)
        {
            Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {text}");
            Captured.Value?.Add((level.Trim(), text));
        }
    }
}
