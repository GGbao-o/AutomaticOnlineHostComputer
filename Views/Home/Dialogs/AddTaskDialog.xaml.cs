using System.Windows;

namespace AutomaticOnlineHostComputer.Views.Home.Dialogs;

public partial class AddTaskDialog : Window
{
    public AddTaskDialog()
    {
        InitializeComponent();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
