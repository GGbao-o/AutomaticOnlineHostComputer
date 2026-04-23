using System.Windows;
using System.Windows.Controls;


// 在文件顶部添加对应的命名空间
using AutomaticOnlineHostComputer.Views.Process.Dialogs;  // 根据你的实际路径调整
namespace AutomaticOnlineHostComputer.Views.Process;
public partial class ProcessManagementView : UserControl
{
    public ProcessManagementView()
    {
        InitializeComponent();
    }

    private void AddProcess_click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new AddProcessDialog
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
        
    }
}
