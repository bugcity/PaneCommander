using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace PaneCommander.ShellActionProbe;

/// <summary>Harmless fixture executable: records invocation; never interprets input or modifies a document.</summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var executable = Environment.ProcessPath!;
        var receipt = Path.ChangeExtension(executable, ".launch.json");
        File.WriteAllText(receipt+".tmp", JsonSerializer.Serialize(new
        { Executable=executable, Arguments=args, WorkingDirectory=Environment.CurrentDirectory, Pid=Environment.ProcessId, Timestamp=DateTimeOffset.UtcNow }));
        File.Move(receipt+".tmp", receipt, overwrite:true);
        if (Environment.GetEnvironmentVariable("PANECOMMANDER_SHELL_PROBE_UI") != "1") return;
        new Application().Run(new Window
        {
            Title="PaneCommander harmless Shell Open receipt", Width=680, Height=200,
            Content=new TextBlock { Text="Shell Open receipt\n"+executable+"\nArguments: "+args.Length,
                TextWrapping=TextWrapping.Wrap, Margin=new Thickness(20) }
        });
    }
}
