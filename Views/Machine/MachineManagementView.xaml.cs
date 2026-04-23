using AutomaticOnlineHostComputer.Views.Machine.Dialogs;
using System.Windows;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Machine;

public partial class MachineManagementView : UserControl
{
    public MachineManagementView()
    {
        InitializeComponent();
    }

    private void AddMachine_click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new AddMachineDialog
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();

    }
}
