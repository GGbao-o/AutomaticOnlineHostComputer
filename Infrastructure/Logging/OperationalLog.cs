using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AutomaticOnlineHostComputer.Infrastructure.Logging;

/// <summary>
/// 仅用于日志显示的动作上下文。绝不读取或改变设备、缓存、锁、流程状态和文件路由。
/// </summary>
public static class OperationalLog
{
    private sealed record ActionContext(string Scope, string Device, string ActionId,
        string? Workpiece, string? Source, string? Target);

    private static readonly AsyncLocal<ActionContext?> Current = new();

    public static IDisposable BeginAction(string scope, string device, string actionId,
        string? workpiece = null, string? source = null, string? target = null)
    {
        ActionContext? previous = Current.Value;
        Current.Value = new ActionContext(scope ?? string.Empty, device ?? string.Empty, actionId ?? string.Empty,
            workpiece, source, target);
        return new Lease(previous);
    }

    public static void Info(string eventName, string conclusion, params (string Name, object? Value)[] fields)
        => TryWrite(OperationalLogLevel.Information, eventName, conclusion, fields);

    public static void Warn(string eventName, string conclusion, params (string Name, object? Value)[] fields)
        => TryWrite(OperationalLogLevel.Warning, eventName, conclusion, fields);

    public static void Error(string eventName, string conclusion, params (string Name, object? Value)[] fields)
        => TryWrite(OperationalLogLevel.Error, eventName, conclusion, fields);

    public static string Format(OperationalLogLevel level, string eventName, string conclusion,
        params (string Name, object? Value)[] fields)
    {
        var headers = new List<string> { $"[{GetLevelName(level)}]" };
        ActionContext? context = Current.Value;
        if (context is not null)
        {
            AppendHeader(headers, context.Scope);
            AppendHeader(headers, context.Device);
            if (!string.IsNullOrWhiteSpace(context.ActionId)) headers.Add($"[动作={context.ActionId}]");
            AppendNamedHeader(headers, "工件", context.Workpiece);
            AppendNamedHeader(headers, "来源", context.Source);
            AppendNamedHeader(headers, "目标", context.Target);
        }
        AppendHeader(headers, eventName);
        string values = string.Join("；", fields
            .Where(field => field.Value is not null && !string.IsNullOrWhiteSpace(field.Value.ToString()))
            .Select(field => $"{field.Name}={field.Value}"));
        return string.IsNullOrWhiteSpace(values)
            ? $"{string.Concat(headers)} {conclusion}"
            : $"{string.Concat(headers)} {conclusion} | {values}";
    }

    /// <summary>由日志路由器在已确定文件归属后调用；只给旧格式补显示上下文，不参与路由。</summary>
    public static string DecorateLegacy(string? message)
    {
        try
        {
            if (Current.Value is null || string.IsNullOrWhiteSpace(message) || message.TrimStart().StartsWith("[信息]", StringComparison.Ordinal) || message.TrimStart().StartsWith("[警告]", StringComparison.Ordinal) || message.TrimStart().StartsWith("[错误]", StringComparison.Ordinal))
                return message ?? string.Empty;
            OperationalLogLevel level = message.Contains("✘", StringComparison.Ordinal) || message.Contains("异常", StringComparison.Ordinal) ? OperationalLogLevel.Error
                : message.Contains("⚠", StringComparison.Ordinal) || message.Contains("等待", StringComparison.Ordinal) ? OperationalLogLevel.Warning : OperationalLogLevel.Information;
            return Format(level, "流程记录", message.Trim());
        }
        catch { return message ?? string.Empty; }
    }

    private static void TryWrite(OperationalLogLevel level, string eventName, string conclusion,
        (string Name, object? Value)[] fields)
    {
        try { Console.WriteLine(Format(level, eventName, conclusion, fields)); } catch { }
    }

    private static void AppendHeader(ICollection<string> headers, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) headers.Add($"[{value}]");
    }

    private static void AppendNamedHeader(ICollection<string> headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) headers.Add($"[{name}={value}]");
    }

    private static string GetLevelName(OperationalLogLevel level) => level switch
    {
        OperationalLogLevel.Warning => "警告",
        OperationalLogLevel.Error => "错误",
        _ => "信息"
    };

    private sealed class Lease(ActionContext? previous) : IDisposable
    {
        private readonly ActionContext? _previous = previous;
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            Current.Value = _previous;
            _disposed = true;
        }
    }
}

public enum OperationalLogLevel { Information, Warning, Error }
