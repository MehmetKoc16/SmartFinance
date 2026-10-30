using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SmartFinance.Tests;

/// Yazilan log kayitlarini bellekte tutan basit logger saglayici. Hem
/// bicimlenmis metin hem de yapilandirilmis alanlar saklanir; "loga kisisel
/// veri sizmadi" testleri ikisine birden bakabilsin diye.
public sealed class YakalayiciLoggerProvider : ILoggerProvider
{
    public record Kayit(string Category, LogLevel Level, EventId EventId, string Message,
        IReadOnlyList<KeyValuePair<string, object?>> Alanlar, Exception? Exception)
    {
        /// Mesaj + tum alan degerleri: "bu metin logda hic gecmiyor mu" kontrolu icin.
        public string TumMetin => Message + " | " + string.Join(" | ", Alanlar.Select(a => $"{a.Key}={a.Value}"));
    }

    private readonly ConcurrentQueue<Kayit> _kayitlar = new();

    public IReadOnlyList<Kayit> Kayitlar => _kayitlar.ToArray();

    public ILogger CreateLogger(string categoryName) => new YakalayiciLogger(this, categoryName);

    public ILogger<T> CreateLogger<T>() => new Logger<T>(new TekSaglayiciFactory(this));

    public void Dispose() { }

    private sealed class YakalayiciLogger(YakalayiciLoggerProvider sahip, string kategori) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var alanlar = state as IReadOnlyList<KeyValuePair<string, object?>>
                ?? Array.Empty<KeyValuePair<string, object?>>();
            sahip._kayitlar.Enqueue(new Kayit(kategori, logLevel, eventId, formatter(state, exception),
                alanlar.ToArray(), exception));
        }
    }

    private sealed class TekSaglayiciFactory(YakalayiciLoggerProvider saglayici) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => saglayici.CreateLogger(categoryName);
        public void Dispose() { }
    }
}
