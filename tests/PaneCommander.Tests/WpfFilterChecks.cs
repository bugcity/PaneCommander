using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;
public sealed partial class WpfIntegrationTests
{
    private static void VerifyFilterUi(Application application)
    {
        var files=new FilterFiles();using var model=new MainViewModel(files,new FakeLogger());var window=new MainWindow{DataContext=model,ShowActivated=false,ShowInTaskbar=false,Left=-20000,Top=-20000};
        try
        {
            window.Show();Pump();CompleteWithDispatcher(model.Workspace.Left.SelectedTab.NavigateAsync(@"C:\filter\left"));CompleteWithDispatcher(model.Workspace.Right.SelectedTab.NavigateAsync(@"C:\filter\right"));Pump();
            var left=(PaneView)window.FindName("LeftPane");var right=(PaneView)window.FindName("RightPane");var list=left.CurrentFileList!;var tab=model.Workspace.Left.SelectedTab;
            var reads=files.Reads;Assert.AreEqual(10000,list.Items.Count);Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(list));
            var selected=tab.Entries.Single(e=>e.Name=="monthly_report.pdf");list.SelectedItem=selected;
            var source=PresentationSource.FromVisual(window)!;
            RaiseKey(list,source,Key.OemQuestion);Pump();var editor=(TextBox)left.FindName("FilterBox");Assert.IsTrue(editor.IsVisible);
            editor.Text="report";Pump();Assert.AreEqual(4,list.Items.Count);Assert.AreSame(selected,list.SelectedItem);Assert.AreEqual(10000,right.CurrentFileList!.Items.Count);
            var text=Descendants<HighlightedFileName>(left).Single(t=>t.FileName=="report_old_REPORT.txt");Assert.AreEqual("report_old_REPORT.txt",new TextRange(text.ContentStart,text.ContentEnd).Text);
            var matches=text.Inlines.OfType<Run>().Where(r=>r.FontWeight==FontWeights.Bold).ToArray();Assert.AreEqual(2,matches.Length);CollectionAssert.AreEqual(new[]{"report","REPORT"},matches.Select(r=>r.Text).ToArray());
            foreach(var settings in new[]{new AppearanceSettings(),AppearanceSettings.Light()})
            {
                CompleteWithDispatcher(model.ApplySettingsAsync(settings with {LeftColumns = new ColumnLayout{Columns=[new("Name",true,300),new("Kind",false,64),new("Size",false,92),new("FileAge",true,90),new("LastWriteTime",false,150)]}}));Pump();
                var cell=Descendants<HighlightedFileName>(left).First(t=>t.FileName=="monthly_report.pdf");var run=cell.Inlines.OfType<Run>().Single(r=>r.FontWeight==FontWeights.Bold);
                Assert.AreEqual(ThemeManager.Brush(settings.FilterMatchForeground).ToString(),run.Foreground.ToString());Assert.IsNull(cell.Background);
                Assert.IsTrue(Contrast(settings.FilterMatchForeground,settings.Background)>=4.5);Assert.IsTrue(Contrast(settings.FilterMatchForeground,settings.ActiveSelection)>=4.5);Assert.IsTrue(Contrast(settings.FilterMatchForeground,settings.InactiveSelection)>=4.5);
                Capture(window,settings.ColorSchemeName=="Light"?"filter-light.png":"filter-dark.png");
            }
            RaiseKey(editor,source,Key.Enter);Pump();Assert.AreEqual("report",tab.FilterText);Assert.AreEqual(4,list.Items.Count);
            Assert.IsTrue(Keyboard.FocusedElement is ListView or ListViewItem);
            tab.FilterText="REPORT_";Pump();Assert.AreEqual(1,list.Items.Count);tab.FilterText="report";Pump();Assert.AreEqual(4,list.Items.Count);
            tab.FilterText="no such name";Pump();Assert.AreEqual(0,list.Items.Count);
            tab.FilterText="";Pump();Assert.AreEqual(10000,list.Items.Count);Assert.IsFalse(Descendants<HighlightedFileName>(left).SelectMany(t=>t.Inlines.OfType<Run>()).Any(r=>r.FontWeight==FontWeights.Bold));
            tab.FilterText="report";Pump();RaiseKey(list,source,Key.Escape);Pump();Assert.AreEqual("",tab.FilterText);Assert.AreEqual(10000,list.Items.Count);
            var other=model.Workspace.Left.AddTab();Pump();CompleteWithDispatcher(other.NavigateAsync(@"C:\filter\other"));Pump();other.FilterText="item-099";Pump();Assert.AreEqual(100,left.CurrentFileList!.Items.Count);
            model.Workspace.Left.SelectedTab=tab;Pump();Assert.AreEqual(10000,left.CurrentFileList!.Items.Count);Assert.AreEqual("",tab.FilterText);
            var beforeInputReads=files.Reads;tab.FilterText="report";Pump();model.Workspace.Right.SelectedTab.FilterText=".csv";Pump();Assert.AreEqual(4,left.CurrentFileList!.Items.Count);Assert.AreEqual(1,right.CurrentFileList!.Items.Count);Assert.AreEqual(beforeInputReads,files.Reads);
            model.Workspace.Toggle();Pump();Assert.AreEqual("report",tab.FilterText);
            Assert.AreEqual(4,left.CurrentFileList!.Items.Count);tab.ClearFilter();Pump();Assert.AreEqual(10000,left.CurrentFileList!.Items.Count);
            Assert.IsFalse(Descendants<HighlightedFileName>(left).Where(c=>!c.IsVisible).Any(c=>c.Inlines.Count>0),"非表示の詳細一覧にはRunを保持・生成しない");
            Assert.IsTrue(Descendants<ListViewItem>(window).Count()<150);Assert.AreEqual(2,Descendants<FolderView>(window).Count());
            Capture(window,"filter-cleared-hierarchy.png");
            files.IncludeDirectory=true;CompleteWithDispatcher(tab.NavigateAsync(@"C:\filter\directories"));Pump();
            var folder=tab.Entries.Single(e=>e.IsDirectory);left.CurrentFileList!.SelectedItem=folder;Pump();var previewReads=files.Reads;
            tab.FilterText="fold";Pump();Assert.AreSame(folder,left.CurrentFileList.SelectedItem);Assert.AreEqual(previewReads,files.Reads,"一致する選択ディレクトリのプレビューをフィルターで再取得しない");
            tab.FilterText="folder";Pump();tab.FilterText="fold";Pump();Assert.AreEqual(previewReads,files.Reads);
            var box=(TextBox)left.FindName("FilterBox");left.BeginFilter();Pump();RaiseKey(box,source,Key.Escape);Pump();Assert.AreEqual("",tab.FilterText);Assert.IsFalse(box.IsVisible);
        }
        finally {window.Close();Pump();}
    }
    private static double Contrast(string first,string second)
    {double L(string hex){var c=(Color)ColorConverter.ConvertFromString(hex);double V(byte b){var v=b/255d;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}return .2126*V(c.R)+.7152*V(c.G)+.0722*V(c.B);}var a=L(first);var b=L(second);return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);}
    private sealed class FilterFiles:IFileSystemService
    {
        public int Reads;public bool IncludeDirectory;public Task<FolderSnapshot> ReadFolderAsync(string path,CancellationToken token){Reads++;var names=new[]{"report.xlsx","monthly_report.pdf","2026_REPORT.csv","report_old_REPORT.txt"};var entries=Enumerable.Range(0,10000).Select(i=>new FileEntry(Path.Combine(path,i<4?names[i]:$"item-{i:D5}.txt"),i<4?names[i]:$"item-{i:D5}.txt",false,i,DateTime.Now.AddDays(-i))).ToList();if(IncludeDirectory)entries.Add(new(Path.Combine(path,"folder"),"folder",true,null,null));return Task.FromResult(new FolderSnapshot(path,entries));}
        public Task OpenFileAsync(FileEntry entry,CancellationToken token)=>Task.CompletedTask;
    }
}
