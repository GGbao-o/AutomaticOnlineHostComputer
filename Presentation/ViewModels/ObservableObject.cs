using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels;

/*
 * ╔══════════════════════════════════════════════════════════════════╗
 * ║                    ObservableObject 基类                         ║
 * ╠══════════════════════════════════════════════════════════════════╣
 * ║  WPF 数据绑定原理：                                               ║
 * ║  WPF 的 Binding 系统在绑定属性时，会检查数据对象是否实现了          ║
 * ║  INotifyPropertyChanged 接口。                                   ║
 * ║                                                                  ║
 * ║  如果实现了，当属性值变化时调用 PropertyChanged 事件，            ║
 * ║  WPF 就会自动刷新绑定了该属性的 UI 控件（TextBlock、Label等）。   ║
 * ║                                                                  ║
 * ║  如果没有实现（普通 POCO 类），UI 只在第一次绑定时读取值，          ║
 * ║  之后属性变化 UI 不会更新，只能整体替换 ItemsSource 才能刷新。    ║
 * ╚══════════════════════════════════════════════════════════════════╝
 */

/// <summary>
/// 所有 ViewModel 的基类，封装了 INotifyPropertyChanged 的通用实现。
/// 任何需要支持 WPF 双向/单向数据绑定的 ViewModel 都应继承此类。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /*
     * INotifyPropertyChanged 接口只有一个成员：
     *   event PropertyChangedEventHandler? PropertyChanged;
     *
     * 当属性值发生变化时，触发这个事件，WPF 绑定引擎监听后会更新 UI。
     */

    /// <summary>
    /// 属性变化通知事件。
    /// WPF 绑定引擎会自动订阅此事件，无需手动订阅。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 触发属性变化通知，通知 WPF 更新绑定到 <paramref name="propertyName"/> 的 UI 元素。
    /// </summary>
    /// <param name="propertyName">
    ///   属性名称。
    ///   使用 [CallerMemberName] 特性，调用时可以不传参数，
    ///   编译器会自动填入调用方的属性名（魔法：零运行时反射开销）。
    /// </param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// 设置属性值并在值真正发生变化时自动触发通知。
    /// 这是在 ViewModel 属性 setter 里的标准写法，避免不必要的 UI 刷新。
    /// </summary>
    /// <typeparam name="T">属性类型</typeparam>
    /// <param name="field">属性对应的私有字段（通过 ref 直接写入）</param>
    /// <param name="value">新值</param>
    /// <param name="propertyName">属性名（由编译器自动填充，通常不传）</param>
    /// <returns>true = 值已变化并触发了通知；false = 值相同，未触发通知</returns>
    /// <example>
    /// <code>
    /// private string _name = string.Empty;
    /// public string Name
    /// {
    ///     get => _name;
    ///     set => SetField(ref _name, value);  // 自动通知，一行搞定
    /// }
    /// </code>
    /// </example>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        // EqualityComparer 用于值比较，避免引用类型踩坑
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;   // 值没变，跳过，不触发事件

        field = value;                          // 写入新值
        OnPropertyChanged(propertyName);        // 通知 WPF 更新 UI
        return true;
    }
}
