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
    /// 任务添加按钮 打开添加任务页面
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
    /// 线路选取按钮 打开页面
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
