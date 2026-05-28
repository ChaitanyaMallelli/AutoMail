using System.Collections.Concurrent;

namespace JobAutomation.Services;

public class TelegramProgressTracker
{
    private static readonly ConcurrentDictionary<long, List<string>> _progress = new();
    private static readonly ConcurrentDictionary<long, int> _latestJobIds = new();

    public static void UpdateProgress(long chatId, string step)
    {
        _progress.AddOrUpdate(chatId, 
            new List<string> { step }, 
            (key, list) => {
                var prefix = step.Split(':')[0] + ":";
                var index = list.FindIndex(s => s.StartsWith(prefix));
                if (index >= 0)
                {
                    list[index] = step;
                }
                else
                {
                    list.Add(step);
                }
                return list;
            });
    }

    public static void ResetProgress(long chatId)
    {
        _progress.TryRemove(chatId, out _);
        _latestJobIds.TryRemove(chatId, out _);
    }

    public static List<string> GetProgress(long chatId)
    {
        return _progress.TryGetValue(chatId, out var list) ? list : new List<string>();
    }

    public static void SetLatestJobId(long chatId, int jobId)
    {
        _latestJobIds[chatId] = jobId;
    }

    public static int GetLatestJobId(long chatId)
    {
        return _latestJobIds.TryGetValue(chatId, out var id) ? id : 0;
    }

    private static DateTime _lastExecutionStartTime = DateTime.MinValue;

    public static void RecordExecutionStart()
    {
        _lastExecutionStartTime = DateTime.UtcNow;
    }

    public static bool IsRecentExecutionActive()
    {
        // If an execution started in the last 15 seconds
        return (DateTime.UtcNow - _lastExecutionStartTime).TotalSeconds < 15;
    }

    public static ConcurrentDictionary<long, List<string>> AllProgress => _progress;
}
