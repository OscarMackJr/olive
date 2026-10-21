using Microsoft.Extensions.Logging;

namespace Olive.Tests;

// Captures every formatted message and exception message so tests can prove nothing secret is logged.
public sealed class InMemoryLogCapture : ILoggerProvider
{
    private readonly List<string> _entries = [];

    public string Text
    {
        get { lock (_entries) { return string.Join("\n", _entries); } }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

    public void Dispose() { }

    private void Add(string s) { lock (_entries) { _entries.Add(s); } }

    private sealed class CaptureLogger(InMemoryLogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Add(formatter(state, exception));
            if (exception is not null)
            {
                owner.Add(exception.ToString());
            }
        }
    }
}
