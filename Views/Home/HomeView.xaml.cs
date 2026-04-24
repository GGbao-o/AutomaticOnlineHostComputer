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

    /// <summary>
    /// �������Ӱ�ť ����������ҳ��
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddTaskDialog
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// ��·ѡȡ��ť ��ҳ��
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void RouteSelect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RouteSelectDialog
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }
}