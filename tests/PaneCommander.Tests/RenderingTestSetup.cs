using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.Core;
using System.Windows.Media;

namespace PaneCommander.Tests;

[TestClass]
public sealed class RenderingTestSetup
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        var requested = Environment.GetEnvironmentVariable("PANECOMMANDER_TEST_RENDER_MODE") ?? "SoftwareOnly";
        var preference = Enum.Parse<RenderingPreference>(requested);
        RenderingConfiguration.ApplyBeforeWindowCreation(preference);
        context.WriteLine("WPF rendering preference before tests: " + RenderOptions.ProcessRenderMode);
    }
}
