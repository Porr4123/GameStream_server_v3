using System.Text;

namespace GameStream.Server.Diagnostics;

/// <summary>
/// Grava o log num arquivo de texto (com horário), além do console. O console some quando a janela fecha;
/// o arquivo fica e dá para mandar inteiro para quem for ajudar. Nunca derruba o servidor: se não der para
/// escrever (pasta sem permissão, disco cheio), simplesmente para de gravar.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public string Path { get; }

    public FileLoggerProvider(string path)
    {
        Path = path;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
        }
        catch
        {
            _writer = null;
        }
    }

    public bool IsActive => _writer is not null;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_lock)
        {
            if (_writer is null) return;
            try { _writer.WriteLine(line); }
            catch { _writer = null; }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _writer?.Dispose(); } catch { /* ignora */ }
            _writer = null;
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(FileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ')
              .Append(logLevel switch
              {
                  LogLevel.Trace => "TRC",
                  LogLevel.Debug => "DBG",
                  LogLevel.Information => "INF",
                  LogLevel.Warning => "AVISO",
                  LogLevel.Error => "ERRO",
                  LogLevel.Critical => "CRIT",
                  _ => "???"
              })
              .Append(' ').Append(formatter(state, exception));
            if (exception is not null) sb.Append('\n').Append(exception);

            _owner.Write(sb.ToString());
        }
    }
}
