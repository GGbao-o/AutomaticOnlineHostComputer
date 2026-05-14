using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AutomaticOnlineHostComputer.Views.Home.Controls;

/// <summary>
/// 设备总览卡片控件（天车 / 机械手通用）。
/// 上半部分显示 5 行状态文字，下半部分 8 个操作按钮，
/// 每个按钮都通过 DependencyProperty 暴露：
///   - 文字（Btn1~8）
///   - 背景色（Btn1~8Brush）
///   - 命令（Btn1~8Command）
///   - ToolTip（Btn1~8Tip）
/// 状态行也支持文字颜色绑定（Line1~5Brush）。
/// 标题行左侧小圆点颜色由 ConnectedBrush 控制（绿=已连接，灰=未连接，红=故障）。
/// </summary>
public partial class OverviewDeviceCard : UserControl
{
    public OverviewDeviceCard()
    {
        InitializeComponent();
    }

    // ── 标题 ────────────────────────────────────────────────────────────
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    // ── 连接指示灯颜色 ──────────────────────────────────────────────────
    public static readonly DependencyProperty ConnectedBrushProperty =
        DependencyProperty.Register(nameof(ConnectedBrush), typeof(Brush), typeof(OverviewDeviceCard),
            new PropertyMetadata(Brushes.Gray));
    /// <summary>标题栏左侧圆点颜色：绿=已连接，灰=未连接，红=故障</summary>
    public Brush ConnectedBrush { get => (Brush)GetValue(ConnectedBrushProperty); set => SetValue(ConnectedBrushProperty, value); }

    // ── 状态文字（Line1~Line5）─────────────────────────────────────────
    public static readonly DependencyProperty Line1Property = DependencyProperty.Register(nameof(Line1), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Line2Property = DependencyProperty.Register(nameof(Line2), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Line3Property = DependencyProperty.Register(nameof(Line3), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Line4Property = DependencyProperty.Register(nameof(Line4), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Line5Property = DependencyProperty.Register(nameof(Line5), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public string Line1 { get => (string)GetValue(Line1Property); set => SetValue(Line1Property, value); }
    public string Line2 { get => (string)GetValue(Line2Property); set => SetValue(Line2Property, value); }
    public string Line3 { get => (string)GetValue(Line3Property); set => SetValue(Line3Property, value); }
    public string Line4 { get => (string)GetValue(Line4Property); set => SetValue(Line4Property, value); }
    public string Line5 { get => (string)GetValue(Line5Property); set => SetValue(Line5Property, value); }

    // ── 状态文字颜色（Line1~5Brush，默认黑）──────────────────────────
    public static readonly DependencyProperty Line1BrushProperty = DependencyProperty.Register(nameof(Line1Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(Brushes.Black));
    public static readonly DependencyProperty Line2BrushProperty = DependencyProperty.Register(nameof(Line2Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(Brushes.Black));
    public static readonly DependencyProperty Line3BrushProperty = DependencyProperty.Register(nameof(Line3Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(Brushes.Black));
    public static readonly DependencyProperty Line4BrushProperty = DependencyProperty.Register(nameof(Line4Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(Brushes.Black));
    public static readonly DependencyProperty Line5BrushProperty = DependencyProperty.Register(nameof(Line5Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(Brushes.Black));
    public Brush Line1Brush { get => (Brush)GetValue(Line1BrushProperty); set => SetValue(Line1BrushProperty, value); }
    public Brush Line2Brush { get => (Brush)GetValue(Line2BrushProperty); set => SetValue(Line2BrushProperty, value); }
    public Brush Line3Brush { get => (Brush)GetValue(Line3BrushProperty); set => SetValue(Line3BrushProperty, value); }
    public Brush Line4Brush { get => (Brush)GetValue(Line4BrushProperty); set => SetValue(Line4BrushProperty, value); }
    public Brush Line5Brush { get => (Brush)GetValue(Line5BrushProperty); set => SetValue(Line5BrushProperty, value); }

    // ── 按钮文字（Btn1~8）──────────────────────────────────────────────
    public static readonly DependencyProperty Btn1Property = DependencyProperty.Register(nameof(Btn1), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn2Property = DependencyProperty.Register(nameof(Btn2), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn3Property = DependencyProperty.Register(nameof(Btn3), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn4Property = DependencyProperty.Register(nameof(Btn4), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn5Property = DependencyProperty.Register(nameof(Btn5), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn6Property = DependencyProperty.Register(nameof(Btn6), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn7Property = DependencyProperty.Register(nameof(Btn7), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn8Property = DependencyProperty.Register(nameof(Btn8), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public string Btn1 { get => (string)GetValue(Btn1Property); set => SetValue(Btn1Property, value); }
    public string Btn2 { get => (string)GetValue(Btn2Property); set => SetValue(Btn2Property, value); }
    public string Btn3 { get => (string)GetValue(Btn3Property); set => SetValue(Btn3Property, value); }
    public string Btn4 { get => (string)GetValue(Btn4Property); set => SetValue(Btn4Property, value); }
    public string Btn5 { get => (string)GetValue(Btn5Property); set => SetValue(Btn5Property, value); }
    public string Btn6 { get => (string)GetValue(Btn6Property); set => SetValue(Btn6Property, value); }
    public string Btn7 { get => (string)GetValue(Btn7Property); set => SetValue(Btn7Property, value); }
    public string Btn8 { get => (string)GetValue(Btn8Property); set => SetValue(Btn8Property, value); }

    // ── 按钮背景色（Btn1~8Brush，默认蓝）──────────────────────────────
    private static readonly Brush DefaultBtnBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x86, 0xC8));
    public static readonly DependencyProperty Btn1BrushProperty = DependencyProperty.Register(nameof(Btn1Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(DefaultBtnBrush));
    public static readonly DependencyProperty Btn2BrushProperty = DependencyProperty.Register(nameof(Btn2Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(DefaultBtnBrush));
    public static readonly DependencyProperty Btn3BrushProperty = DependencyProperty.Register(nameof(Btn3Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(DefaultBtnBrush));
    public static readonly DependencyProperty Btn4BrushProperty = DependencyProperty.Register(nameof(Btn4Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(DefaultBtnBrush));
    public static readonly DependencyProperty Btn5BrushProperty = DependencyProperty.Register(nameof(Btn5Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(DefaultBtnBrush));
    public static readonly DependencyProperty Btn6BrushProperty = DependencyProperty.Register(nameof(Btn6Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(DefaultBtnBrush));
    public static readonly DependencyProperty Btn7BrushProperty = DependencyProperty.Register(nameof(Btn7Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(DefaultBtnBrush));
    public static readonly DependencyProperty Btn8BrushProperty = DependencyProperty.Register(nameof(Btn8Brush), typeof(Brush), typeof(OverviewDeviceCard), new PropertyMetadata(DefaultBtnBrush));
    public Brush Btn1Brush { get => (Brush)GetValue(Btn1BrushProperty); set => SetValue(Btn1BrushProperty, value); }
    public Brush Btn2Brush { get => (Brush)GetValue(Btn2BrushProperty); set => SetValue(Btn2BrushProperty, value); }
    public Brush Btn3Brush { get => (Brush)GetValue(Btn3BrushProperty); set => SetValue(Btn3BrushProperty, value); }
    public Brush Btn4Brush { get => (Brush)GetValue(Btn4BrushProperty); set => SetValue(Btn4BrushProperty, value); }
    public Brush Btn5Brush { get => (Brush)GetValue(Btn5BrushProperty); set => SetValue(Btn5BrushProperty, value); }
    public Brush Btn6Brush { get => (Brush)GetValue(Btn6BrushProperty); set => SetValue(Btn6BrushProperty, value); }
    public Brush Btn7Brush { get => (Brush)GetValue(Btn7BrushProperty); set => SetValue(Btn7BrushProperty, value); }
    public Brush Btn8Brush { get => (Brush)GetValue(Btn8BrushProperty); set => SetValue(Btn8BrushProperty, value); }

    // ── 按钮命令（Btn1~8Command）────────────────────────────────────────
    public static readonly DependencyProperty Btn1CommandProperty = DependencyProperty.Register(nameof(Btn1Command), typeof(ICommand), typeof(OverviewDeviceCard), new PropertyMetadata(null));
    public static readonly DependencyProperty Btn2CommandProperty = DependencyProperty.Register(nameof(Btn2Command), typeof(ICommand), typeof(OverviewDeviceCard), new PropertyMetadata(null));
    public static readonly DependencyProperty Btn3CommandProperty = DependencyProperty.Register(nameof(Btn3Command), typeof(ICommand), typeof(OverviewDeviceCard), new PropertyMetadata(null));
    public static readonly DependencyProperty Btn4CommandProperty = DependencyProperty.Register(nameof(Btn4Command), typeof(ICommand), typeof(OverviewDeviceCard), new PropertyMetadata(null));
    public static readonly DependencyProperty Btn5CommandProperty = DependencyProperty.Register(nameof(Btn5Command), typeof(ICommand), typeof(OverviewDeviceCard), new PropertyMetadata(null));
    public static readonly DependencyProperty Btn6CommandProperty = DependencyProperty.Register(nameof(Btn6Command), typeof(ICommand), typeof(OverviewDeviceCard), new PropertyMetadata(null));
    public static readonly DependencyProperty Btn7CommandProperty = DependencyProperty.Register(nameof(Btn7Command), typeof(ICommand), typeof(OverviewDeviceCard), new PropertyMetadata(null));
    public static readonly DependencyProperty Btn8CommandProperty = DependencyProperty.Register(nameof(Btn8Command), typeof(ICommand), typeof(OverviewDeviceCard), new PropertyMetadata(null));
    public ICommand? Btn1Command { get => (ICommand?)GetValue(Btn1CommandProperty); set => SetValue(Btn1CommandProperty, value); }
    public ICommand? Btn2Command { get => (ICommand?)GetValue(Btn2CommandProperty); set => SetValue(Btn2CommandProperty, value); }
    public ICommand? Btn3Command { get => (ICommand?)GetValue(Btn3CommandProperty); set => SetValue(Btn3CommandProperty, value); }
    public ICommand? Btn4Command { get => (ICommand?)GetValue(Btn4CommandProperty); set => SetValue(Btn4CommandProperty, value); }
    public ICommand? Btn5Command { get => (ICommand?)GetValue(Btn5CommandProperty); set => SetValue(Btn5CommandProperty, value); }
    public ICommand? Btn6Command { get => (ICommand?)GetValue(Btn6CommandProperty); set => SetValue(Btn6CommandProperty, value); }
    public ICommand? Btn7Command { get => (ICommand?)GetValue(Btn7CommandProperty); set => SetValue(Btn7CommandProperty, value); }
    public ICommand? Btn8Command { get => (ICommand?)GetValue(Btn8CommandProperty); set => SetValue(Btn8CommandProperty, value); }

    // ── 按钮 ToolTip（Btn1~8Tip）────────────────────────────────────────
    public static readonly DependencyProperty Btn1TipProperty = DependencyProperty.Register(nameof(Btn1Tip), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn2TipProperty = DependencyProperty.Register(nameof(Btn2Tip), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn3TipProperty = DependencyProperty.Register(nameof(Btn3Tip), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn4TipProperty = DependencyProperty.Register(nameof(Btn4Tip), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn5TipProperty = DependencyProperty.Register(nameof(Btn5Tip), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn6TipProperty = DependencyProperty.Register(nameof(Btn6Tip), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn7TipProperty = DependencyProperty.Register(nameof(Btn7Tip), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty Btn8TipProperty = DependencyProperty.Register(nameof(Btn8Tip), typeof(string), typeof(OverviewDeviceCard), new PropertyMetadata(string.Empty));
    public string Btn1Tip { get => (string)GetValue(Btn1TipProperty); set => SetValue(Btn1TipProperty, value); }
    public string Btn2Tip { get => (string)GetValue(Btn2TipProperty); set => SetValue(Btn2TipProperty, value); }
    public string Btn3Tip { get => (string)GetValue(Btn3TipProperty); set => SetValue(Btn3TipProperty, value); }
    public string Btn4Tip { get => (string)GetValue(Btn4TipProperty); set => SetValue(Btn4TipProperty, value); }
    public string Btn5Tip { get => (string)GetValue(Btn5TipProperty); set => SetValue(Btn5TipProperty, value); }
    public string Btn6Tip { get => (string)GetValue(Btn6TipProperty); set => SetValue(Btn6TipProperty, value); }
    public string Btn7Tip { get => (string)GetValue(Btn7TipProperty); set => SetValue(Btn7TipProperty, value); }
    public string Btn8Tip { get => (string)GetValue(Btn8TipProperty); set => SetValue(Btn8TipProperty, value); }
}
