using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TuckPane.Services;

public static class AppLogger
{
    internal static DiagnosticLogWriter Writer { get; } = new(Path.Combine(AppPaths.LocalRoot, "diagnostics"));

    // Compatibility callers may format user content. It is intentionally never persisted.
    public static void Info(string message, [CallerMemberName] string caller = "") { }
    public static void Error(string message, Exception? exception = null, [CallerMemberName] string caller = "") =>
        Record(DiagnosticArea.Runtime, DiagnosticStage.Failed, exception: exception, origin: caller);

    public static void Lifecycle(string name, string details = "") => Record(DiagnosticArea.Lifecycle, name switch
    {
        "startup" => DiagnosticStage.Started,
        "exit-application" or "message-loop-ended" => DiagnosticStage.Exit,
        "exit-cancelled" => DiagnosticStage.Cancelled,
        "ui-exception" => DiagnosticStage.Failed,
        "exit-failed" => DiagnosticStage.Failed,
        _ => DiagnosticStage.Notice
    }, origin: name);

    private const int MaximumPerformanceBytes = 2 * 1024 * 1024;
    private static readonly object PerformanceGate = new();

    public static bool PerformanceTraceEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("TUCKPANE_PERF_TRACE"), "1", StringComparison.Ordinal);

    // Opt-in tracing only: samples are persisted exclusively while TUCKPANE_PERF_TRACE=1.
    public static void Performance(string message, [CallerMemberName] string caller = "")
    {
        if (!PerformanceTraceEnabled) return;
        try
        {
            string directory = Path.Combine(AppPaths.LocalRoot, "diagnostics");
            string line = $"{DateTimeOffset.UtcNow:O} {caller} {SanitizePerformance(message)}";
            lock (PerformanceGate)
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "performance.0.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > MaximumPerformanceBytes)
                    File.Move(path, Path.Combine(directory, "performance.1.jsonl"), overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { /* Tracing must never change application behavior. */ }
    }

    private static string SanitizePerformance(string message)
    {
        string sanitized = message.ReplaceLineEndings(" ");
        return sanitized.Length <= 512 ? sanitized : sanitized[..512];
    }

    public static async Task FlushAsync() => await Writer.FlushAsync(TimeSpan.FromSeconds(2));

    // CallerMemberName reports ".ctor" for constructors, which is not a usable code location.
    // A stack walk resolves the declaring type instead; this only runs for constructor failures.
    internal static string OriginFor(string caller)
    {
        if (caller is not (".ctor" or ".cctor")) return caller;
        try
        {
            foreach (StackFrame frame in new StackTrace(fNeedFileInfo: false).GetFrames())
            {
                Type? type = frame.GetMethod()?.DeclaringType;
                if (type is null || type == typeof(AppLogger)) continue;
                string name = SafeTypeName(type);
                return name.Length == 0 ? caller : $"{name}.{caller}";
            }
        }
        catch { }
        return caller;
    }

    private static string SafeTypeName(Type type)
    {
        var parts = new Stack<string>();
        for (Type? current = type; current is not null; current = current.DeclaringType) parts.Push(current.Name);
        string name = string.Join('.', parts);
        foreach (char character in name)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '.')) return "";
        return name;
    }

    internal static void Record(DiagnosticArea area, DiagnosticStage stage, Guid? operation = null,
        double? elapsedMs = null, int? count = null, Exception? exception = null, [CallerMemberName] string origin = "")
    {
        origin = OriginFor(origin);
        if (!DiagnosticRecord.ShouldRecord(stage, DiagnosticRecord.ClassifyFault(exception))) return;
        try { Writer.Write(DiagnosticRecord.Create(area, stage, operation, elapsedMs, count, exception, origin)); }
        catch { /* Diagnostic failures must not change application behavior. */ }
    }

    internal static Operation Begin(DiagnosticArea area, [CallerMemberName] string origin = "") => new(area, OriginFor(origin));
    internal sealed class Operation : IDisposable
    {
        internal Guid Id { get; } = Guid.NewGuid();
        private readonly DiagnosticArea _area;
        private readonly string _origin;
        private readonly long _started = Stopwatch.GetTimestamp();
        private DiagnosticStage _result = DiagnosticStage.Interrupted;
        private Exception? _exception;
        private int? _count;
        private bool _disposed;
        internal Operation(DiagnosticArea area, string origin) { _area = area; _origin = origin; }
        internal void Complete(int? count = null) { _result = DiagnosticStage.Completed; _count = count; }
        internal void Fail(Exception exception) { _result = exception is OperationCanceledException ? DiagnosticStage.Cancelled : DiagnosticStage.Failed; _exception = exception; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Record(_area, _result, Id, Stopwatch.GetElapsedTime(_started).TotalMilliseconds, _count, _exception, _origin);
        }
    }
}
