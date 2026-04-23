using System.Windows;
using System.Windows.Controls;
using AutomaticOnlineHostComputer.Views.Crane.Dialogs;

namespace AutomaticOnlineHostComputer.Views.Crane;

public partial class CraneManagementView : UserControl
{
    public CraneManagementView()
    {
        InitializeComponent();
    }

    private void AddCrane_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddCraneDialog
        {
            Owner = Window.GetWindow(this)
        };

        dialog.ShowDialog();
    }
}
