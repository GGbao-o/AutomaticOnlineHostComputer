using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutomaticOnlineHostComputer.Views.Home.Controls;
/// <summary>
/// 纯粹的依赖属性定义模式，没有业务逻辑，只负责暴露可绑定属性。这是 WPF 自定义控件的标准做法。
/// </summary>
public partial class StatusCard : UserControl
{
    public StatusCard()
    {
        // Console.WriteLine("自定义控件 statusCard");
        InitializeComponent();
    }
/// <summary>
/// 依赖属性
/// </summary>
    public static readonly DependencyProperty CardTitleProperty = DependencyProperty.Register(
        nameof(CardTitle), typeof(string), typeof(StatusCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty Status1Property = DependencyProperty.Register(
        nameof(Status1), typeof(string), typeof(StatusCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty Status2Property = DependencyProperty.Register(
        nameof(Status2), typeof(string), typeof(StatusCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IpTextProperty = DependencyProperty.Register(
        nameof(IpText), typeof(string), typeof(StatusCard), new PropertyMetadata("未配置IP"));
/// <summary>
/// 配套的clr包装器
/// </summary>
    public string CardTitle
    {
        get => (string)GetValue(CardTitleProperty);
        set => SetValue(CardTitleProperty, value);
    }

    public string Status1
    {
        get => (string)GetValue(Status1Property);
        set => SetValue(Status1Property, value);
    }

    public string Status2
    {
        get => (string)GetValue(Status2Property);
        set => SetValue(Status2Property, value);
    }

    public string IpText
    {
        get => (string)GetValue(IpTextProperty);
        set => SetValue(IpTextProperty, value);
    }

    // ── 连接状态颜色（绑定到 StationCardViewModel.ConnectedBrush） ──
    public static readonly DependencyProperty ConnectedBrushProperty = DependencyProperty.Register(
        nameof(ConnectedBrush), typeof(Brush), typeof(StatusCard),
        new PropertyMetadata(Brushes.Gray));
    public Brush ConnectedBrush
    {
        get => (Brush)GetValue(ConnectedBrushProperty);
        set => SetValue(ConnectedBrushProperty, value);
    }

    /// <summary>Status1 文字颜色</summary>
    public static readonly DependencyProperty Status1BrushProperty = DependencyProperty.Register(
        nameof(Status1Brush), typeof(Brush), typeof(StatusCard),
        new PropertyMetadata(Brushes.Black));
    public Brush Status1Brush
    {
        get => (Brush)GetValue(Status1BrushProperty);
        set => SetValue(Status1BrushProperty, value);
    }

    /// <summary>Status2 文字颜色</summary>
    public static readonly DependencyProperty Status2BrushProperty = DependencyProperty.Register(
        nameof(Status2Brush), typeof(Brush), typeof(StatusCard),
        new PropertyMetadata(Brushes.Black));
    public Brush Status2Brush
    {
        get => (Brush)GetValue(Status2BrushProperty);
        set => SetValue(Status2BrushProperty, value);
    }
}