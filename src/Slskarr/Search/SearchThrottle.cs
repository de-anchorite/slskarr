using Microsoft.Extensions.Options;
using Slskarr.Configuration;

namespace Slskarr.Search;

/// <summary>
/// Enforces the minimum gap between slskd searches and the hourly cap over a sliding one-hour window.
/// The window lives in memory; the worker seeds it from recent <c>SearchJobEntity.CompletedAt</c> values at startup.
/// </summary>
public sealed class SearchThrottle(IOptions<SlskarrOptions> options)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly SearchOptions _options = options.Value.Search;
    private readonly int _cap = Math.Max(1, options.Value.Search.HourlyCap);
    private readonly List<DateTimeOffset> _starts = [];
    private readonly Lock _lock = new();

    /// <summary>How long to wait before the next search may start; null when it may start now.</summary>
    public TimeSpan? DelayBeforeNext(DateTimeOffset now)
    {
        lock (_lock)
        {
            Prune(now);
            if (_starts.Count == 0)
            {
                return null;
            }

            var until = _starts[^1] + _options.MinGap;
            if (_starts.Count >= _cap)
            {
                // The window frees when the start that would push us over the cap leaves it.
                var freesAt = _starts[_starts.Count - _cap] + Window;
                if (freesAt > until)
                {
                    until = freesAt;
                }
            }

            return until > now ? until - now : null;
        }
    }

    public void Record(DateTimeOffset started)
    {
        lock (_lock)
        {
            var index = _starts.FindLastIndex(s => s <= started) + 1;
            _starts.Insert(index, started);
        }
    }

    public bool HourlyCapReached(DateTimeOffset now)
    {
        lock (_lock)
        {
            Prune(now);
            return _starts.Count >= _cap;
        }
    }

    private void Prune(DateTimeOffset now) => _starts.RemoveAll(s => s <= now - Window);
}
