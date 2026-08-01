using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Tests.Support;

/// <summary>
/// An <see cref="ILogger{T}"/> that records what was logged, so a test can assert a DIAGNOSTIC actually
/// fires. NullLogger is the right default everywhere else; use this only where the log line is itself the
/// deliverable — e.g. the §B1.3 "thread resolved to 0 messages" guard, whose entire job is to make a
/// silent 200-with-empty visible.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message);

    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Entries => _entries;

    public IReadOnlyList<Entry> AtLevel(LogLevel level) =>
        _entries.Where(e => e.Level == level).ToList();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Add(new Entry(logLevel, formatter(state, exception)));
}
