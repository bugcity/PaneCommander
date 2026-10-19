using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;

namespace PaneCommander.Tests;

[TestClass]
public sealed class ShortcutColumnTests
{
    [TestMethod]
    public async Task NamedBatchStopsOnAbortAndRefreshesOnlyAfterTheBatch()
    {
        var files=new FakeFileSystem();var operations=new AbortSecond();
        using var model=new PaneCommander.App.ViewModels.MainViewModel(files,new FakeLogger(),fileOperations:operations);
        await model.Workspace.Left.SelectedTab.NavigateAsync(@"C:\left");await model.Workspace.Right.SelectedTab.NavigateAsync(@"C:\right");
        var before=files.ReadCount;
        var requests=new[]{"a","b","c"}.Select(n=>SameFolderOperation.Create(FileOperationKind.Copy,@"C:\left\"+n+".txt",n+"-copy.txt")).ToArray();
        Assert.IsTrue((await model.ExecuteFileOperationBatchAsync(requests,IntPtr.Zero)).Aborted);
        Assert.AreEqual(2,operations.Calls);Assert.AreEqual(before+2,files.ReadCount,"左右の選択タブをバッチ終了後に各1回だけ更新");
        StringAssert.Contains(model.OperationStatus,"1/3");Assert.IsFalse(model.IsFileOperationRunning);
    }
    private sealed class AbortSecond:IFileOperationService
    {
        public int Calls;
        public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request,IntPtr owner,CancellationToken token=default)=>Task.FromResult(new FileOperationResult(++Calls==2));
    }
    [TestMethod]
    public void NarrowColumnsHideInConfirmedPriorityAndRestoreWithoutChangingPreferences()
    {
        var layout=new ColumnLayout();var saved=layout.Columns.Select(c=>c.Width).ToArray();
        var narrow=AdaptiveColumns.Calculate(layout,300,_=>50);
        Assert.AreEqual(0,narrow["LastWriteTime"]);Assert.AreEqual(0,narrow["Size"]);
        Assert.IsTrue(narrow["Kind"]>0);Assert.IsTrue(narrow["FileAge"]>0);Assert.IsTrue(narrow["Name"]>=120);
        var tiny=AdaptiveColumns.Calculate(layout,180,_=>50);
        Assert.IsTrue(tiny["Name"]>0);Assert.AreEqual(0,tiny["FileAge"]);Assert.AreEqual(0,tiny["Kind"]);
        Assert.AreEqual(180,tiny.Values.Sum());
        var restored=AdaptiveColumns.Calculate(layout,900,_=>50);
        Assert.IsTrue(restored.Values.All(w=>w>0));CollectionAssert.AreEqual(saved,layout.Columns.Select(c=>c.Width).ToArray());
    }
    [TestMethod]
    public void ExplicitlyHiddenAndFixedNameColumnsStayBoundedAndDoNotReappear()
    {
        var layout=new ColumnLayout{NameFillsRemaining=false,Columns=[new("Name",true,400),new("Kind",false,70),new("FileAge",true,80),new("Size",true,100),new("LastWriteTime",false,160)]};
        foreach(var width in new[]{0d,40,200,400,1000})
        {var result=AdaptiveColumns.Calculate(layout,width,_=>50);Assert.IsTrue(result.Values.Sum()<=width);Assert.AreEqual(0,result["Kind"]);Assert.AreEqual(0,result["LastWriteTime"]);}
        Assert.AreEqual(400,AdaptiveColumns.Calculate(layout,1000,_=>50)["Name"]);
    }
    [TestMethod]
    public void ManualNameWidthHidesMetadataInPriorityAndRestoresWhenNarrowed()
    {
        var layout = new ColumnLayout { NameFillsRemaining = false };
        ColumnLayout NameWidth(double width) => layout with
        { Columns = layout.Columns.Select(c => c.Key == "Name" ? c with { Width = width } : c).ToArray() };
        var ordinary = AdaptiveColumns.Calculate(NameWidth(180), 600, _ => 50);
        Assert.IsTrue(ordinary.Values.All(w => w > 0));
        foreach (var (width, omitted) in new[] { (250d, 1), (400d, 2), (500d, 3), (560d, 4) })
        {
            var result = AdaptiveColumns.Calculate(NameWidth(width), 600, _ => 50);
            Assert.AreEqual(width, result["Name"], "手動指定幅を先に確保する");
            var order = new[] { "LastWriteTime", "Size", "FileAge", "Kind" };
            for (var i = 0; i < order.Length; i++)
                Assert.AreEqual(i >= omitted, result[order[i]] > 0, order[i]);
            Assert.IsTrue(result.Values.Sum() <= 600);
        }
        var oversized = AdaptiveColumns.Calculate(NameWidth(1000), 600, _ => 50);
        Assert.AreEqual(600d, oversized["Name"]);
        Assert.AreEqual(600d, oversized.Values.Sum());
        CollectionAssert.AreEquivalent(ordinary.ToArray(), AdaptiveColumns.Calculate(NameWidth(180), 600, _ => 50).ToArray());
        Assert.IsTrue(layout.Columns.All(c => c.Visible), "自動省略は保存された表示指定を変えない");
    }
    [TestMethod]
    public void SameFolderNamesMustBeExplicitAndPreserveDestinationAndOperation()
    {
        Assert.IsFalse(SameFolderOperation.IsValidName("a.txt",null,FileOperationKind.Copy));
        Assert.IsFalse(SameFolderOperation.IsValidName("a.txt","A.txt",FileOperationKind.Copy));
        Assert.IsFalse(SameFolderOperation.IsValidName("a.txt","../a.txt",FileOperationKind.Move));
        var copy=SameFolderOperation.Create(FileOperationKind.Copy,@"C:\work\a.txt","b.txt");
        Assert.AreEqual(@"C:\work",copy.Destination);Assert.AreEqual("b.txt",copy.NewName);Assert.AreEqual(FileOperationKind.Copy,copy.Kind);
        var rename=SameFolderOperation.Create(FileOperationKind.Move,@"C:\work\a.txt","A.txt");
        Assert.AreEqual(@"C:\work",rename.Destination);Assert.AreEqual(FileOperationKind.Move,rename.Kind);
    }
}
