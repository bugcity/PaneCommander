using System.Windows.Input;
using PaneCommander.Core;

namespace PaneCommander.App.ViewModels;

public sealed partial class MainViewModel
{
    private bool functionShift;
    private bool functionModifiersSupported = true;
    public Func<FileOperationKind, Task>? RequestSameFolderOperation { get; set; }
    public Func<Task>? RequestCreateFile { get; set; }
    public Func<Task>? RequestContextMenu { get; set; }

    public void SetFunctionModifiers(ModifierKeys modifiers)
    {
        functionShift = modifiers.HasFlag(ModifierKeys.Shift);
        functionModifiersSupported = modifiers is ModifierKeys.None or ModifierKeys.Shift;
        foreach (var action in Functions) action.SetShift(functionShift);
        NotifyFileCommands();
    }

    private IReadOnlyList<FunctionAction> CreateFunctions(IErrorLogger logger)
    {
        var names = new[] { "ヘルプ", "展開", "表示", "編集", "コピー", "移動", "作成", "削除", "メニュー", "終了" };
        var shifted = new[] { "—", "圧縮", "—", "—", "別名コピー", "名前変更", "ファイル作成", "—", "—", "メニュー" };
        return Enumerable.Range(1, 10).Select(number => new FunctionAction(number, names[number - 1],
            number is 1 or 9 ? "未計画" : number == 10 ? "M2" : "M4",
            new AsyncCommand(_ => ExecuteFunctionAsync(number), logger, _ => CanExecuteFunction(number)),
            number is 3 or 10 || number == 2 && archives is not null || number == 4 && shellEdit is not null || fileOperations is not null && number is 5 or 6 or 7 or 8,
            shifted[number - 1])).ToArray();
    }

    private bool CanExecuteFunction(int number)
    {
        if (!functionModifiersSupported) return false;
        if (!functionShift && number == 10) return true;
        if (operating || Workspace.ActivePane.SelectedTab.IsLoading) return false;
        var count = GetSelectedPaths?.Invoke().Length ?? 0;
        if (functionShift)
            return number switch
            {
                2 => RequestArchive is not null && CanCompressArchives,
                5 => fileOperations is not null && RequestSameFolderOperation is not null && count > 0,
                6 => fileOperations is not null && RequestSameFolderOperation is not null && count == 1,
                7 => fileOperations is not null && RequestCreateFile is not null,
                10 => RequestContextMenu is not null,
                _ => false
            };
        return number switch
        {
            2 => RequestArchive is not null && CanExtractArchives,
            3 => RequestPreview is not null && count == 1 && Workspace.ActivePane.SelectedTab.SelectedEntry is { IsDirectory: false } entry
                && entry.Size is not > PaneCommander.Infrastructure.TextPreviewReader.MaximumBytes,
            4 => CanEditSelected,
            5 or 6 => fileOperations is not null && RequestFileOperation is not null && count > 0,
            7 => fileOperations is not null && RequestFileOperation is not null,
            8 => fileOperations is not null && RequestFileOperation is not null && DeleteCommandEnabled && count > 0 && CanRecycleSelection(),
            _ => false
        };
    }

    private bool CanRecycleSelection()
    {
        // Reject known unsupported cases using existing metadata, without scanning folders in CanExecute.
        var paths = GetSelectedPaths?.Invoke() ?? [];
        var entries = Workspace.ActivePane.SelectedTab.Entries;
        return paths.All(path => !path.StartsWith(@"\\", StringComparison.Ordinal)
            && !entries.Any(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.FullPath, path) && entry.IsReparsePoint));
    }

    private Task ExecuteFunctionAsync(int number)
    {
        if (functionShift)
            return number switch
            {
                2 => RequestArchive!(true),
                5 => RequestSameFolderOperation!(FileOperationKind.Copy),
                6 => RequestSameFolderOperation!(FileOperationKind.Move),
                7 => RequestCreateFile!(),
                10 => RequestContextMenu!(),
                _ => Task.CompletedTask
            };
        if (number == 10) { RequestClose?.Invoke(); return Task.CompletedTask; }
        if (number == 2) return RequestArchive!(false);
        if (number == 3) return RequestPreview!(GetSelectedPaths!().Single());
        if (number == 4) return EditSelectedAsync();
        return RequestFileOperation!(number switch { 5 => FileOperationKind.Copy, 6 => FileOperationKind.Move, 7 => FileOperationKind.CreateDirectory, _ => FileOperationKind.Delete });
    }
}

public sealed class FunctionAction(int number, string name, string milestone, AsyncCommand command, bool implemented = false, string shiftName = "—") : ObservableObject
{
    private bool shift;
    public int Number => number;
    public string Name => name;
    public string Milestone => milestone;
    public AsyncCommand Command => command;
    public bool Implemented => implemented;
    public string Label => shift ? $"⇧F{Number} {shiftName}" : $"F{Number} {Name}";
    public string Description => shift ? $"Shift+F{Number}: " + (shiftName == "—" ? "この組み合わせには操作がありません" : shiftName)
        : Number == 10 ? "アプリケーションを終了" : Implemented ? Number == 6 ? "移動・名前変更" : Number == 8 ? "ごみ箱へ移動（完全削除はShift+Deleteで確認）" : Number == 7 ? "ディレクトリ作成" : Name : $"{Name}（未実装）";
    public void SetShift(bool value)
    {
        if (!Set(ref shift, value)) return;
        Raise(nameof(Label)); Raise(nameof(Description));
    }
}
