using System.Windows;

namespace AutomaticOnlineHostComputer.Views.Machine.Dialogs
{
    public partial class AddMachineDialog : Window
    {
        public AddMachineDialog()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}