using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.Alarms;
using AutomaticOnlineHostComputer.Views.Home.Dialogs;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class SafetyAlarmDialogLayoutTests
{
    [Fact]
    public void Approved_layout_binds_without_starting_app_or_connecting_devices()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var action = new FlowActionContext("L2-REAR-UNLOAD-示例", "2号线后天车下料", "4号天车", "P20261011/002", "ST606", "ST021", "ST606");
                action.BeginStep(FlowActionStep.MoveZSafeWithWorkpiece, new(1300, 400, 0), "持板后Z回安全高度，等待到位");
                using var context = FlowAlarmPresentationContext.Begin(action,
                    new WorkpieceCache { PlateNo = "P20261011", Sequence = "002", Length = 1500.5, Diameter = 280, BoreType = 2 },
                    "2号线", 4, "斜床下料");
                FlowAlarmPresentationContext.RecordFailure(new TimeoutException("天车Z轴运动超时，位置未确认。请先核实天车及工件位置。"));
                FlowAlarmPresentationContext.RecordCleanup("设备清零失败：通信中断，PLC参数可能残留", "斜床已回Idle，工件与请求快照已清", "下游缓存未确认交接");
                using var model = new SafetyAlarmDialogViewModel(context, (_, _) => Task.FromResult(CraneAlarmSnapshot.Unknown("测试模拟：设备连接超时")), update => update());
                model.RefreshAsync().GetAwaiter().GetResult();
                var window = new SafetyAlarmDialog(model);
                try
                {
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    var content = (FrameworkElement)window.Content;
                    content.Measure(new Size(816, 790));
                    content.Arrange(new Rect(0, 0, 816, 790));
                    content.UpdateLayout();
                    var boxes = Descendants(content).OfType<TextBox>().ToArray();
                    Assert.Equal(5, boxes.Length); // 完整异常默认折叠，不占用主说明空间。
                    Assert.All(boxes, box => Assert.True(box.IsReadOnly));
                    Assert.Contains(boxes, box => box.Text.Contains("版号：P20261011"));
                    Assert.Contains(boxes, box => box.Text.Contains("长度：1500.5 mm"));
                    Assert.Contains(boxes, box => box.Text.Contains("X6充磁反馈：未知"));
                    Assert.Contains(boxes, box => box.Text.Contains("设备清零失败"));
                    var buttons = Descendants(content).OfType<Button>().Select(button => button.Content?.ToString()).ToArray();
                    Assert.Contains("刷新现场状态", buttons);
                    Assert.Contains("复制详情", buttons);
                    Assert.Contains("关闭", buttons);
                    Assert.Contains(Descendants(content).OfType<ScrollViewer>(), viewer => viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
                    var expander = Assert.Single(Descendants(content).OfType<Expander>());
                    expander.IsExpanded = true;
                    content.UpdateLayout();
                    Assert.Contains(Descendants(content).OfType<TextBox>(), box => box.Text.Contains("TimeoutException"));
                    expander.IsExpanded = false;
                    content.UpdateLayout();
                    string? preview = Environment.GetEnvironmentVariable("ALARM_DIALOG_PREVIEW");
                    if (!string.IsNullOrWhiteSpace(preview))
                    {
                        var bitmap = new RenderTargetBitmap(816, 790, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(content);
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(preview);
                        png.Save(file);
                    }
                }
                finally { window.Close(); }
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "无设备窗口验证未及时结束");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
