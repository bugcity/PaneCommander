using PaneCommander.Core;

namespace PaneCommander.Infrastructure;

public sealed class ErrorLogger : IErrorLogger
{
    private readonly string logDirectory;
    private readonly object gate = new();
    public ErrorLogger(string? logDirectory = null)
    {
        this.logDirectory = logDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaneCommander", "Logs");
        try
        {
            if (Directory.Exists(this.logDirectory)) foreach (var file in Directory.EnumerateFiles(this.logDirectory, "*.log"))
            {
                var name = Path.GetFileName(file);
                if ((name.Length == 14 || name.EndsWith(".archive.log", StringComparison.Ordinal) && name.Length == 22)
                    && DateOnly.TryParseExact(name[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day)
                    && day <= DateOnly.FromDateTime(DateTime.Now).AddDays(-30)) File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Archive(string stage, string[] targets, string? result, Exception? error)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(Path.Combine(logDirectory, $"{DateTime.Now:yyyy-MM-dd}.archive.log"),
                    System.Text.Json.JsonSerializer.Serialize(new { time = DateTimeOffset.Now, stage, targets, result, error = error?.ToString() }) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Log(string context, Exception error)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(logDirectory);
                // 本文・例外メッセージ・対象パスを保存しない。
                File.AppendAllText(Path.Combine(logDirectory, $"{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTimeOffset.Now:O}\t{context}\t{error.GetType().Name}\t0x{error.HResult:X8}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ログ失敗で操作を落とさない */ }
    }
}
