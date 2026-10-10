using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using AutomaticOnlineHostComputer.Views.Motion.Controls;
using Xunit;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class ForkManualControlLayoutTests
{
    [Theory]
    [InlineData(760)]
    [InlineData(1100)]
    public async Task Panel_binds_all_commands_and_keeps_fork_above_existing_controls(int width)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var vm = new ForkManualControlViewModel((_, _, _) => throw new InvalidOperationException("布局测试禁止连接设备"));
                vm.LoadMappings(new[] { new MachineManagementRowVm { StationCode = "ST711", Ip = "192.168.2.88", Port = 9000 } });
                var panel = new ManualMotionControlPanel { DataContext = new { ForkManualControl = vm } };
                panel.Measure(new Size(width, 900));
                panel.Arrange(new Rect(0, 0, width, 900));
                panel.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

                var group = Assert.IsType<GroupBox>(panel.FindName("ForkManualGroup"));
                var selector = Assert.IsType<ComboBox>(panel.FindName("ForkLineSelector"));
                Assert.Equal(2, selector.Items.Count);
                Assert.Same(vm.SelectedLine, selector.SelectedItem);
                var buttons = Descendants(group).OfType<Button>().ToArray();
                Assert.Equal(5, buttons.Length);
                Assert.Equal(new[]
                {
                    vm.RefreshStatusCommand, vm.GoStandbyCommand, vm.FeedToBoringCommand,
                    vm.PickFromBoringCommand, vm.StandbyToPos3Command
                }, buttons.Select(b => b.Command));
                Assert.All(buttons, b =>
                {
                    Assert.True(b.IsEnabled);
                    Assert.True(b.ActualWidth >= b.MinWidth);
                    var bounds = b.TransformToAncestor(group).TransformBounds(new Rect(b.RenderSize));
                    Assert.True(bounds.Left >= 0 && bounds.Right <= group.ActualWidth);
                    Assert.True(bounds.Top >= 0 && bounds.Bottom <= group.ActualHeight);
                });
                var existing = Descendants(panel).OfType<GroupBox>().Single(g => Equals(g.Header, "设备选择"));
                var forkBottom = group.TransformToAncestor(panel).Transform(new Point(0, group.ActualHeight)).Y;
                var existingTop = existing.TransformToAncestor(panel).Transform(new Point(0, 0)).Y;
                Assert.True(existingTop >= forkBottom);
                Assert.Contains(Descendants(group).OfType<TextBlock>(), t => t.Text == "未知");

                // 可选离线截图，既不启动应用入口，也不创建PLC连接。
                var previewDir = Environment.GetEnvironmentVariable("MANUAL_FORK_PREVIEW_DIR");
                if (!string.IsNullOrEmpty(previewDir))
                {
                    Directory.CreateDirectory(previewDir);
                    var bitmap = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(panel);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(previewDir, $"manual-fork-{width}.png"));
                    encoder.Save(stream);
                }
                finished.SetResult();
            }
            catch (Exception ex) { finished.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
