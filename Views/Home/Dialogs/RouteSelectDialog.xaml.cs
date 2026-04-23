using System.Windows;

namespace AutomaticOnlineHostComputer.Views.Home.Dialogs;

public partial class RouteSelectDialog : Window
{
    public RouteSelectDialog()
    {
        InitializeComponent();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
