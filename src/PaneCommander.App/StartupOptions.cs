using System.IO;

namespace PaneCommander.App;

public sealed record StartupOptions(string ProfileDirectory, string? DiagnosticsDirectory, bool VisitTabs = false)
{
    public static StartupOptions Parse(string[] args)
    {
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaneCommander");
        string? diagnostics = null;
        var visitTabs = false;
        for (var i = 0; i < args.Length; i += 2)
        {
            if (args[i] == "--diagnostics-workload" && i + 1 < args.Length && args[i + 1] == "visit-tabs") { visitTabs = true; continue; }
            if (i + 1 >= args.Length || args[i] is not ("--profile-directory" or "--diagnostics-directory") || !Path.IsPathFullyQualified(args[i + 1]))
                throw new ArgumentException("--profile-directory / --diagnostics-directory には絶対パスを指定してください。");
            if (args[i] == "--profile-directory") profile = args[i + 1]; else diagnostics = args[i + 1];
        }
        if (visitTabs && diagnostics is null) throw new ArgumentException("診断ワークロードには診断出力先が必要です。");
        return new(profile, diagnostics, visitTabs);
    }
}
