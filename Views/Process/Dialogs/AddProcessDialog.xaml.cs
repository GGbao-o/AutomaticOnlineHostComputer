using System;
using System.Windows;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Process.Dialogs;

/// <summary>
/// AddProcessDialog.xaml 的交互逻辑
/// </summary>
public partial class AddProcessDialog : Window
{
    public AddProcessDialog()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();

    }
}
