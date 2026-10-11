using System.Windows;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

namespace AutomaticOnlineHostComputer.Views.Home.Dialogs;

public partial class SafetyAlarmDialog : Window
{
    private readonly SafetyAlarmDialogViewModel _model;
    internal SafetyAlarmDialog(SafetyAlarmDialogViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 40);
        MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 40);
        Closed += (_, _) => _model.Dispose();
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await _model.RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _model.RefreshAsync();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_model.CopyText); }
        catch (Exception ex) { MessageBox.Show(this, $"复制失败：{ex.Message}", "复制详情"); }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
