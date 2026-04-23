using System.Windows;
using System.Windows.Controls;
using AutomaticOnlineHostComputer.Views.Controller;
using AutomaticOnlineHostComputer.Views.Crane;
using AutomaticOnlineHostComputer.Views.Home;
using AutomaticOnlineHostComputer.Views.Machine;
using AutomaticOnlineHostComputer.Views.Process;

namespace AutomaticOnlineHostComputer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ContentHost.Content = new HomeView();
    }

    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is null)
        {
            return;
        }

        ContentHost.Content = menuItem.Tag.ToString() switch
        {
            "main" => new HomeView(),
            "crane" => new CraneManagementView(),
            "machine" => new MachineManagementView(),
            "controller" => new ControllerManagementView(),
            "process" => new ProcessManagementView(),
            _ => new HomeView(),
        };
    }
}
