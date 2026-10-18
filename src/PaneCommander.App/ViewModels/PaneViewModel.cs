using PaneCommander.Core;

namespace PaneCommander.App.ViewModels;

public sealed class PaneViewModel
{
    public PaneViewModel(PaneState state, IErrorLogger logger)
    {
        State = state;
        Navigate = new(_ => State.SelectedTab.NavigateAsync(State.SelectedTab.InputPath), logger);
        Back = new(_ => State.SelectedTab.BackAsync(), logger);
        Forward = new(_ => State.SelectedTab.ForwardAsync(), logger);
        Up = new(async _ =>
        {
            var tab = State.SelectedTab;
            await tab.UpAsync();
            if (ReferenceEquals(State.SelectedTab, tab) && !tab.IsLoading) RequestListFocus?.Invoke(tab);
        }, logger);
        Refresh = new(_ => State.SelectedTab.RefreshAsync(), logger);
        Open = new(target => State.SelectedTab.OpenAsync(target as FileEntry ?? State.SelectedTab.SelectedEntry), logger);
        AddTab = new(_ => { State.AddTab(); return Task.CompletedTask; }, logger);
        CloseTab = new(tab => { State.CloseTab(tab as TabState ?? State.SelectedTab); return Task.CompletedTask; }, logger);
        Cancel = new(_ => { State.SelectedTab.CancelLoading(); return Task.CompletedTask; }, logger);
        GoToPath = new(_ => RequestPath?.Invoke(State.SelectedTab) ?? Task.CompletedTask, logger);
        ConfigureColumns = new(_=>RequestColumns?.Invoke(State.Side)??Task.CompletedTask,logger);
        ToggleHiddenFiles = new(_ => { State.SelectedTab.ShowHiddenFiles = !State.SelectedTab.ShowHiddenFiles; return Task.CompletedTask; }, logger);
    }
    public PaneState State { get; }
    public AsyncCommand Navigate { get; }
    public AsyncCommand Back { get; }
    public AsyncCommand Forward { get; }
    public AsyncCommand Up { get; }
    public AsyncCommand Refresh { get; }
    public AsyncCommand Open { get; }
    public AsyncCommand AddTab { get; }
    public AsyncCommand CloseTab { get; }
    public AsyncCommand Cancel { get; }
    public AsyncCommand GoToPath { get; }
    public AsyncCommand ConfigureColumns { get; }
    public AsyncCommand ToggleHiddenFiles { get; }
    public Func<PaneSide,Task>? RequestColumns {get;set;}
    public Func<TabState, Task>? RequestPath { get; set; }
    public Action<TabState>? RequestListFocus { get; set; }
}
