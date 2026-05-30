using System.Windows;

namespace AutomaticOnlineHostComputer.Views.Home.Dialogs;

public partial class RouteSelectDialog : Window
{
    /// <summary>用户选择的线路: 1 或 2</summary>
    public int SelectedLine { get; private set; } = 1;

    public RouteSelectDialog()
    {
        InitializeComponent();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        SelectedLine = RadioLine2.IsChecked == true ? 2 : 1;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
