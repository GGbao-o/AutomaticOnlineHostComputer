using System.Windows.Controls;
using System.Windows;
using AutomaticOnlineHostComputer.Views.Home.Dialogs;

namespace AutomaticOnlineHostComputer.Views.Home;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddTaskDialog
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private void RouteSelect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RouteSelectDialog
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }
}
