using PaneCommander.Core;

namespace PaneCommander.Infrastructure;

public sealed class ErrorLogger : IErrorLogger
{
    private readonly string logDirectory;
    private readonly object gate = new();
    public ErrorLogger(string? logDirectory = null) => this.logDirectory = logDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaneCommander", "logs");
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
