using AutomaticOnlineHostComputer.Views.Controller.Dialogs;
using System.Windows;
using System.Windows;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Controller;

public partial class ControllerManagementView : UserControl
{
    public ControllerManagementView()
    {
        InitializeComponent();
    }

        private void AddController_click(object sender, System.Windows.RoutedEventArgs e)
        {
            var dialog = new AddControllerDialog
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
        }
}
