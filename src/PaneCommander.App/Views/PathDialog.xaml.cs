using System.Windows;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public partial class PathDialog : Window
{
    private readonly PathDialogViewModel model;
    public PathDialog(TabState target)
    {
        InitializeComponent();
        DataContext = model = new(target);
        Loaded += (_, _) => { Editor.Focus(); Editor.SelectAll(); };
        Closing += (_, _) => model.Cancel();
    }
    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (await model.SubmitAsync()) DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { model.Cancel(); DialogResult = false; }
}
