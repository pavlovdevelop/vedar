using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Vedar.Infrastructure.Security;

namespace Vedar.Infrastructure.Logging;

/// <summary>
/// Minimal rolling file logger. One file per day, 7 days retention, 5 MB cap per file.
/// Every line passes through <see cref="SecretRedactor"/> so API keys can never reach the log.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int RetentionDays = 7;

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _dir = directory;
        MinimumLevel = minimumLevel;
        Directory.CreateDirectory(_dir);
        CleanupOldFiles();
        _writer = Task.Run(WriteLoopAsync);
    }

    public LogLevel MinimumLevel { get; set; }

    public string CurrentFile => Path.Combine(_dir, $"vedar-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, c => new FileLogger(this, c));

    internal void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var file = CurrentFile;
                if (File.Exists(file) && new FileInfo(file).Length > MaxFileBytes)
                {
                    continue;
                }

                await File.AppendAllTextAsync(file, line, Encoding.UTF8).ConfigureAwait(false);
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }

    private void CleanupOldFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "vedar-*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-RetentionDays))
                {
                    File.Delete(f);
                }
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Shutting down.
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _shortCategory = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider.MinimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append(" [").Append(Short(logLevel)).Append("] ")
              .Append(_shortCategory).Append(": ")
              .Append(formatter(state, exception));
            if (exception is not null)
            {
                sb.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
                if (logLevel >= LogLevel.Error)
                {
                    sb.AppendLine().Append(exception.StackTrace);
                }
            }

            sb.AppendLine();
            provider.Enqueue(SecretRedactor.Redact(sb.ToString()));
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
