using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AutomaticOnlineHostComputer.Infrastructure.Logging;

/// <summary>
/// 接管 Console.WriteLine，将日志按引擎作用域写入按天轮转的文件。
/// 日志设施是旁路能力：文件系统故障只退化为原 Console 输出，绝不向业务流程抛出异常。
/// </summary>
public sealed class EngineLogRouter : TextWriter
{
    public const string Line1 = "line1";
    public const string Line2 = "line2";
    public const string Balance = "balance";
    public const string Grinding = "grinding";
    public const string Shared = "shared";

    private static readonly AsyncLocal<string?> CurrentScope = new();
    private static readonly string[] Destinations = [Line1, Line2, Balance, Grinding, Shared];
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private const int RetentionDays = 30;

    private readonly object _writeLock = new();
    private readonly TextWriter _fallback;
    private readonly string _rootDirectory;
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.Ordinal);
    private readonly Timer _flushTimer;
    private string? _activeDateFolder;
    private bool _disposed;

    public EngineLogRouter(TextWriter fallback, string rootDirectory)
    {
        _fallback = fallback ?? TextWriter.Null;
        _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory) ? @"D:\BSH\logs" : rootDirectory;

        // 清理失败只意味着旧文件以后再清，不阻断日志初始化或程序启动。
        TryCleanExpiredDirectories();
        _flushTimer = new Timer(_ => FlushBufferedWriters(), null, FlushInterval, FlushInterval);
    }

    public override Encoding Encoding => _fallback.Encoding;

    /// <summary>
    /// 在当前异步执行上下文内标记日志归属。Dispose 后恢复调用前的归属，避免 UI/调度器串线。
    /// </summary>
    public static IDisposable BeginScope(string? scope)
    {
        string? previous = CurrentScope.Value;
        CurrentScope.Value = NormalizeScope(scope);
        return new ScopeLease(previous);
    }

    public override void WriteLine()
        => WriteLine(string.Empty);

    public override void WriteLine(string? value)
    {
        string destination = ResolveDestination(value);
        string displayValue = OperationalLog.DecorateLegacy(value);
        DateTime now = DateTime.Now;

        lock (_writeLock)
        {
            if (_disposed)
            {
                WriteFallback(value, now);
                return;
            }

            try
            {
                EnsureWritersForDate(now);
                if (!_writers.TryGetValue(destination, out var writer))
                {
                    WriteFallback(value, now);
                    return;
                }

                WriteMessage(writer, displayValue, now);
                WriteFallback(displayValue, now);
            }
            catch
            {
                // 不能让日志目录不可用、跨天轮转或磁盘写入失败影响现场流程。
                WriteFallback(value, now);
            }
        }
    }

    /// <summary>
    /// 当前项目没有 Console.Write 调用；保留原 Console 行为，避免未来的非换行片段被错误拼接或加伪时间戳。
    /// </summary>
    public override void Write(char value)
    {
        try { _fallback.Write(value); } catch { }
    }

    public override void Write(string? value)
    {
        try { _fallback.Write(value); } catch { }
    }

    private static string ResolveDestination(string? message)
    {
        string scope = NormalizeScope(CurrentScope.Value);
        if (!string.Equals(scope, Shared, StringComparison.Ordinal)) return scope;

        string text = (message ?? string.Empty).TrimStart();
        if (StartsWith(text, "[Line1Front]") || StartsWith(text, "[Line1Rear]") || StartsWith(text, "[后引擎]"))
            return Line1;
        if (StartsWith(text, "[Line2Front]") || StartsWith(text, "[Line2Rear]") || StartsWith(text, "[后引擎2]"))
            return Line2;
        if (StartsWith(text, "[平衡引擎]") || StartsWith(text, "[平衡引擎#"))
            return Balance;
        if (StartsWith(text, "[GrindingEngine]"))
            return Grinding;

        return Shared;
    }

    private static bool StartsWith(string text, string prefix)
        => text.StartsWith(prefix, StringComparison.Ordinal);

    private static string NormalizeScope(string? scope)
    {
        return scope switch
        {
            Line1 => Line1,
            Line2 => Line2,
            Balance => Balance,
            Grinding => Grinding,
            _ => Shared
        };
    }

    private void EnsureWritersForDate(DateTime now)
    {
        string dateFolder = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (string.Equals(_activeDateFolder, dateFolder, StringComparison.Ordinal) && _writers.Count == Destinations.Length)
            return;

        CloseWritersNoThrow();
        Directory.CreateDirectory(Path.Combine(_rootDirectory, dateFolder));

        foreach (string destination in Destinations)
        {
            string path = Path.Combine(_rootDirectory, dateFolder, $"{destination}.log");
            _writers[destination] = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false
            };
        }

        _activeDateFolder = dateFolder;
    }

    private static void WriteMessage(TextWriter writer, string? value, DateTime now)
    {
        string timestamp = now.ToString("[yyyy-MM-dd HH:mm:ss.fff] ", CultureInfo.InvariantCulture);
        string text = value ?? string.Empty;
        using var reader = new StringReader(text);
        string? line;
        bool wroteAnyLine = false;
        while ((line = reader.ReadLine()) != null)
        {
            wroteAnyLine = true;
            if (line.Length == 0) writer.WriteLine();
            else writer.WriteLine(timestamp + line);
        }

        // Console.WriteLine("") 和末尾无换行的普通消息都要保留一次 WriteLine 语义。
        if (!wroteAnyLine) writer.WriteLine();
    }

    private void WriteFallback(string? value, DateTime now)
    {
        try
        {
            string timestamp = now.ToString("[yyyy-MM-dd HH:mm:ss.fff] ", CultureInfo.InvariantCulture);
            string text = value ?? string.Empty;
            using var reader = new StringReader(text);
            string? line;
            bool wroteAnyLine = false;
            while ((line = reader.ReadLine()) != null)
            {
                wroteAnyLine = true;
                if (line.Length == 0) _fallback.WriteLine();
                else _fallback.WriteLine(timestamp + line);
            }
            if (!wroteAnyLine) _fallback.WriteLine();
        }
        catch
        {
            // 原始 Console 同样不可用时，日志只能静默丢弃；绝不能影响业务线程。
        }
    }

    private void FlushBufferedWriters()
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            foreach (StreamWriter writer in _writers.Values)
            {
                try { writer.Flush(); } catch { }
            }
        }
    }

    private void TryCleanExpiredDirectories()
    {
        try
        {
            if (!Directory.Exists(_rootDirectory)) return;
            DateTime cutoff = DateTime.Today.AddDays(-RetentionDays);
            foreach (string directory in Directory.GetDirectories(_rootDirectory))
            {
                string name = Path.GetFileName(directory);
                if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime day) || day >= cutoff)
                    continue;
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }
        catch { }
    }

    private void CloseWritersNoThrow()
    {
        foreach (StreamWriter writer in _writers.Values)
        {
            try { writer.Flush(); } catch { }
            try { writer.Dispose(); } catch { }
        }
        _writers.Clear();
        _activeDateFolder = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flushTimer.Dispose();
            lock (_writeLock)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    CloseWritersNoThrow();
                }
            }
        }
        base.Dispose(disposing);
    }

    private sealed class ScopeLease : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public ScopeLease(string? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentScope.Value = _previous;
        }
    }
}
