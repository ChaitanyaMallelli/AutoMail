using System.Collections.Concurrent;

namespace JobAutomation.Services;

/// <summary>
/// Live, in-memory state for the on-demand "Apply from Links File" run.
/// Tracks running flag, cancellation, per-post results, and counts — polled by the
/// dashboard to show progress on screen. Process-global (single run at a time), like
/// <see cref="TelegramProgressTracker"/>.
/// </summary>
public static class FileApplyProgressTracker
{
    public record Entry(string Status, string Company, string Role, string Detail, string Time);

    private static readonly object _lock = new();
    private static bool _running;
    private static CancellationTokenSource? _cts;
    private static readonly List<Entry> _entries = new();
    private static int _total, _processed, _applied, _skipped, _failed;
    private static DateTime _startedAt;

    public static bool IsRunning { get { lock (_lock) return _running; } }

    /// <summary>Begin a run; resets counters and returns a cancellation token to drive the loop.</summary>
    public static CancellationToken Begin(int total)
    {
        lock (_lock)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _running = true;
            _entries.Clear();
            _total = total;
            _processed = _applied = _skipped = _failed = 0;
            _startedAt = DateTime.UtcNow;
            return _cts.Token;
        }
    }

    /// <summary>Request the running loop to stop (cancels the token).</summary>
    public static void RequestStop()
    {
        lock (_lock) { _cts?.Cancel(); }
    }

    public static void Complete()
    {
        lock (_lock) { _running = false; }
    }

    /// <summary>Record the end-status of one processed post.</summary>
    public static void Record(string status, string company, string role, string detail)
    {
        lock (_lock)
        {
            _processed++;
            switch (status)
            {
                case "Applied": _applied++; break;
                case "Skipped": _skipped++; break;
                default: _failed++; break;
            }
            _entries.Insert(0, new Entry(
                status,
                string.IsNullOrWhiteSpace(company) ? "Unknown" : company,
                string.IsNullOrWhiteSpace(role) ? "Unknown" : role,
                detail ?? "",
                DateTime.UtcNow.ToString("HH:mm:ss")));
            // Keep the on-screen list bounded.
            if (_entries.Count > 300) _entries.RemoveAt(_entries.Count - 1);
        }
    }

    /// <summary>Snapshot for the status JSON endpoint (Gemini daily usage passed in by the caller).</summary>
    public static object Snapshot(int geminiUsed, int geminiMax)
    {
        lock (_lock)
        {
            return new
            {
                running = _running,
                total = _total,
                processed = _processed,
                applied = _applied,
                skipped = _skipped,
                failed = _failed,
                geminiUsed,
                geminiMax,
                entries = _entries.ToList(),
            };
        }
    }
}
