using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;

namespace PaneCommander.Tests;

[TestClass]
public sealed class DropDestinationTests
{
    [TestMethod]
    public void DropCapturesDirectoryAndSourceArrayAndPreservesModifierRules()
    {
        var sources = new[] { @"C:\source\payload.txt", @"C:\source\second.txt" };
        var request = DropPolicy.CreateRequest(sources, @"C:\target", false, false)!;
        Assert.AreEqual(@"C:\target", request.Destination); Assert.AreEqual(FileOperationKind.Move, request.Kind);
        sources[0] = @"C:\other\changed.txt"; Assert.AreEqual(@"C:\source\payload.txt", request.Sources[0]);
        Assert.AreEqual(FileOperationKind.Copy, DropPolicy.CreateRequest(sources, @"C:\target", true, false)!.Kind);
        Assert.AreEqual(FileOperationKind.Move, DropPolicy.CreateRequest(sources, @"D:\target", false, true)!.Kind);
        Assert.AreEqual(FileOperationKind.Copy, DropPolicy.CreateRequest(sources, @"D:\target", false, false)!.Kind);
        Assert.IsNull(DropPolicy.CreateRequest(sources, @"C:\target", true, true));
        Assert.IsNull(DropPolicy.CreateRequest(sources, @"C:\target", false, false, true));
    }
    [TestMethod]
    public void SameFolderDropsNeverGenerateImplicitNamesOrChangeData()
    {
        foreach (var control in new[] { false, true })
        {
            Assert.IsNull(DropPolicy.CreateRequest([@"C:\source\payload.txt"], @"c:\SOURCE\", control, false));
            Assert.IsNull(DropPolicy.CreateRequest([@"C:\source\one.txt", @"C:\source\two.txt"], @"C:\source", control, false));
        }
    }
    [TestMethod]
    public void InvalidAndSelfDescendantDestinationsCannotDispatchOperations()
    {
        Assert.IsNull(DropPolicy.CreateRequest([], @"C:\target", false, false));
        Assert.ThrowsExactly<ArgumentException>(() => DropPolicy.CreateRequest([@"C:\source\file"], "relative", false, false));
        Assert.ThrowsExactly<ArgumentException>(() => DropPolicy.CreateRequest([@"C:\source"], @"C:\source\child", true, false));
    }
}
