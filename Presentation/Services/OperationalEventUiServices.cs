using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AutomaticOnlineHostComputer.Presentation.Services;

/// <summary>UI 线程调度抽象，测试可注入立即执行版本。</summary>
public interface IUiDispatcher
{
    void BeginInvoke(Action action);
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void BeginInvoke(Action action)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);
}

/// <summary>剪贴板抽象，测试可注入内存版本。</summary>
public interface IClipboardService
{
    void SetText(string text);
}

public sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text) => System.Windows.Clipboard.SetText(text);
}

/// <summary>导出保存对话框抽象，测试可注入虚拟版本。</summary>
public interface IOperationalEventSaveDialog
{
    OperationalEventSaveTarget? ShowSaveDialog(string defaultFileName, string csvFilter, string txtFilter);
}

public sealed class WpfOperationalEventSaveDialog : IOperationalEventSaveDialog
{
    public OperationalEventSaveTarget? ShowSaveDialog(string defaultFileName, string csvFilter, string txtFilter)
    {
        var dlg = new SaveFileDialog
        {
            FileName = defaultFileName,
            Filter = $"{csvFilter}|{txtFilter}",
            FilterIndex = 1,
            AddExtension = true,
            DefaultExt = "csv"
        };
        bool? result = dlg.ShowDialog();
        if (result != true || string.IsNullOrWhiteSpace(dlg.FileName)) return null;

        string normalized = dlg.FileName.Trim();
        string ext = System.IO.Path.GetExtension(normalized).ToLowerInvariant();
        var format = ext == ".txt" ? OperationalEventExportFormat.Txt : OperationalEventExportFormat.Csv;
        return new OperationalEventSaveTarget(normalized, format);
    }
}

public enum OperationalEventExportFormat { Csv, Txt }

public sealed record OperationalEventSaveTarget(string Path, OperationalEventExportFormat Format);

/// <summary>异步导出服务，后台执行文件写入。</summary>
public interface IOperationalEventExportService
{
    Task ExportAsync(OperationalEventSaveTarget target,
        System.Collections.Generic.IReadOnlyList<Service.OperationalEvents.OperationalEvent> events,
        Service.OperationalEvents.OperationalEventFormatter formatter,
        CancellationToken cancellationToken = default);
}

public sealed class OperationalEventExportService : IOperationalEventExportService
{
    public async Task ExportAsync(OperationalEventSaveTarget target,
        System.Collections.Generic.IReadOnlyList<Service.OperationalEvents.OperationalEvent> events,
        Service.OperationalEvents.OperationalEventFormatter formatter,
        CancellationToken cancellationToken = default)
    {
        if (target.Format == OperationalEventExportFormat.Csv)
            await Infrastructure.Export.OperationalEventExporter.WriteCsvAsync(target.Path, events, cancellationToken);
        else
            await Infrastructure.Export.OperationalEventExporter.WriteTextAsync(target.Path, events, formatter, cancellationToken);
    }
}
