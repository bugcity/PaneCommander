using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class M2Tests
{
    [TestMethod]
    public void FileAgeUnitsAndEdgeCases()
    {
        var now = new DateTime(2026, 10, 12, 14, 0, 0); var settings = new AppearanceSettings();
        foreach (var (minutes, text) in new[] { (0d, "0 m"), (59d, "59 m"), (60d, "1 h"), (1440d, "1 d"), (43200d, "1 M"), (525600d, "1 y"), (-5d, "+5 m") })
            Assert.AreEqual(text, FileAge.Present(now.AddMinutes(-minutes), now, settings).Text);
        Assert.AreEqual("—", FileAge.Present(null, now, settings).Text);
        Assert.AreEqual(settings.Foreground, FileAge.Present(now, now, settings with { ColorFileAge = false }).Color);
        Assert.AreEqual("2026-10-12 14:00:00", new FileEntry("x", "x", false, 0, now).ModifiedText);
    }

    [TestMethod]
    public void FileAgeGradientIsContinuousMonotonicAndCustomizable()
    {
        var now = new DateTime(2026, 10, 12); var settings = new AppearanceSettings();
        Assert.AreEqual(0d, FileAge.Hue(0));
        Assert.IsTrue(FileAge.Hue(8760) > 210, "古いファイルは青系");
        var previous = -1d;
        for (var hours = 0d; hours < 100000; hours = hours * 1.1 + .001)
        {
            var hue = FileAge.Hue(hours); Assert.IsTrue(hue > previous && hue < 240); previous = hue;
        }
        foreach (var boundary in new[] { 1d, 24d, 168d, 720d })
            Assert.IsTrue(Math.Abs(FileAge.Hue(boundary - .0001) - FileAge.Hue(boundary + .0001)) < .01, "旧区分境界で飛ばない");
        var fresh = FileAge.Present(now, now, settings).Color;
        Assert.IsTrue(Convert.ToInt32(fresh[1..3], 16) > Convert.ToInt32(fresh[5..7], 16), "新しいファイルは赤系");
        Assert.AreEqual("#808080", FileAge.Present(now.AddDays(-10), now, settings with { FileAgeSaturation = 0, FileAgeLightness = .5 }).Color);
        Assert.AreEqual("#000000", FileAge.Present(now, now, settings with { FileAgeLightness = 0 }).Color);
        Assert.AreEqual("#FFFFFF", FileAge.Present(now, now, settings with { FileAgeLightness = 1 }).Color);
    }

    [TestMethod]
    public void InvalidSettingsAreRejected()
    {
        Assert.AreEqual(0, new AppearanceSettings().Validate().Count);
        Assert.AreEqual(0, AppearanceSettings.Light().Validate().Count);
        foreach (var value in new[] { new AppearanceSettings { FontSize = double.NaN }, new AppearanceSettings { ActiveBorder = "red" }, new AppearanceSettings { FileAgeSaturation = 1.01 }, new AppearanceSettings { FileAgeLightness = -.01 }, new AppearanceSettings { NameWidth = 0 }, new AppearanceSettings { RowHeight = 24, FontSize = 32 } })
            Assert.IsTrue(value.Validate().Count > 0);
    }

    [TestMethod]
    public async Task SettingsRoundTripReplacementAndCorruptionFallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaneCommanderSettings-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "日本語 設定.json"); var store = new JsonSettingsStore(path);
        try
        {
            Assert.IsNull((await store.LoadAsync()).Error);
            var value = AppearanceSettings.Light() with { FontSize = 18, NameWidth = 70, ShowSize = false };
            await store.SaveAsync(value); Assert.AreEqual(value, (await store.LoadAsync()).Settings);
            await store.SaveAsync(new()); Assert.AreEqual(new AppearanceSettings(), (await store.LoadAsync()).Settings);
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
            await File.WriteAllTextAsync(path, "{broken"); Assert.IsNotNull((await store.LoadAsync()).Error);
            await File.WriteAllTextAsync(path, "{\"FontSize\":99}"); Assert.IsNotNull((await store.LoadAsync()).Error);
            await File.WriteAllTextAsync(path, "{\"FontSize\":16,\"T2Hours\":-1,\"T1Color\":\"red\",\"RelativeDates\":false}");
            var legacy = await store.LoadAsync(); Assert.IsNull(legacy.Error); Assert.AreEqual(16d, legacy.Settings.FontSize);
            Assert.IsTrue(legacy.Settings.ShowFileAge && legacy.Settings.ShowModified && legacy.Settings.ColorFileAge);
            await store.SaveAsync(legacy.Settings); Assert.IsFalse((await File.ReadAllTextAsync(path)).Contains("T2Hours"));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(new() { FontSize = 99 }));
        }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }

    [TestMethod]
    public async Task PathDialogTargetsCapturedTabAndPreservesFolderOnFailureOrCancel()
    {
        using var main = new MainViewModel(new FakeFileSystem(), new FakeLogger()); await main.Workspace.InitializeAsync();
        var tab = main.Left.State.SelectedTab; var right = main.Right.State.SelectedTab.CurrentPath;
        var dialog = new PathDialogViewModel(tab); Assert.AreEqual(tab.CurrentPath, dialog.Path);
        dialog.Path = "FAIL"; Assert.IsFalse(await dialog.SubmitAsync()); Assert.IsTrue(dialog.Error.Length > 0);
        var original = tab.CurrentPath; dialog.Path = "cancel"; dialog.Cancel(); Assert.IsFalse(await dialog.SubmitAsync()); Assert.AreEqual(original, tab.CurrentPath);
        var success = new PathDialogViewModel(tab) { Path = "success" }; main.Workspace.Activate(PaneSide.Right);
        Assert.IsTrue(await success.SubmitAsync()); Assert.AreEqual("success", tab.CurrentPath); Assert.AreEqual(right, main.Right.State.SelectedTab.CurrentPath);
    }

    [TestMethod]
    public async Task PathDialogCancelInFlightDoesNotCommit()
    {
        using var tab = new TabState("original", new CancellableFileSystem(), new FakeLogger());
        var dialog = new PathDialogViewModel(tab) { Path = "slow" }; var task = dialog.SubmitAsync(); dialog.Cancel();
        Assert.IsFalse(await task); Assert.AreEqual("original", tab.CurrentPath);
    }

    [TestMethod]
    public void FunctionKeysHaveStandardLabelsAndDisabledCommandsCannotClose()
    {
        using var main = new MainViewModel(new FakeFileSystem(), new FakeLogger()); var closes = 0; main.RequestClose = () => closes++;
        CollectionAssert.AreEqual(new[] { "ヘルプ", "展開", "表示", "編集", "コピー", "移動", "作成", "削除", "メニュー", "終了" }, main.Functions.Select(f => f.Name).ToArray());
        foreach (var function in main.Functions.Take(9)) { Assert.IsFalse(function.Command.CanExecute(null)); function.Command.Execute(null); }
        Assert.AreEqual(0, closes); main.Functions[9].Command.Execute(null); Assert.AreEqual(1, closes);
        Assert.IsTrue(main.Functions.Where(f => f.Number is 1 or 9).All(f => f.Milestone == "未計画"));
    }

}
