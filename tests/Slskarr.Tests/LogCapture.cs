using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Slskarr.Tests;

/// <summary>Collects formatted log lines; usable as an <see cref="ILoggerProvider"/> or for a single typed logger.</summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries => [.. _entries];

    public IEnumerable<string> Messages(LogLevel min = LogLevel.Information) =>
        Entries.Where(e => e.Level >= min).Select(e => e.Message);

    public ILogger<T> For<T>() => new Logger<T>(new SingleProviderFactory(this));

    public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Capturing(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner._entries.Enqueue((logLevel, category, formatter(state, exception)));
    }

    private sealed class SingleProviderFactory(LogCapture provider) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void AddProvider(ILoggerProvider p)
        {
        }

        public void Dispose()
        {
        }
    }
}
