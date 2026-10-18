using System.IO;
using System.Text.Json;

namespace PaneCommander.App.Views;

// Explicit test-process opt-in; no watcher, timer or normal-session log file.
internal static class DropDiagnostics
{
    public static void Write(string stage, string? destination = null, string[]? sources = null, string? result = null)
    {
        var path = Environment.GetEnvironmentVariable("PANECOMMANDER_DND_TRACE");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return;
        try { File.AppendAllText(path, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, stage, destination, sources, result }) + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
