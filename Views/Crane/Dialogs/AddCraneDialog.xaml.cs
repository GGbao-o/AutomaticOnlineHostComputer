using System.Windows;

namespace AutomaticOnlineHostComputer.Views.Crane.Dialogs;

public partial class AddCraneDialog : Window
{
    public AddCraneDialog()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
