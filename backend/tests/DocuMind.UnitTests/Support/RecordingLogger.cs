using Microsoft.Extensions.Logging;

namespace DocuMind.UnitTests.Support;

/// <summary>Captures formatted messages and attached exceptions to check that delivery logs keep secrets private.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(string Message, Exception? Exception)> Entries { get; } = [];

    /// <summary>No external logging scope is needed for isolated service tests.</summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>Captures every level so privacy assertions inspect all messages from the operation.</summary>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <summary>Records output in memory; test credentials and token values are never printed.</summary>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((formatter(state, exception), exception));
}
