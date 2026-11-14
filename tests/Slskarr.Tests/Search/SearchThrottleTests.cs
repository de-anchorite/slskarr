using Microsoft.Extensions.Options;
using Shouldly;
using Slskarr.Search;

namespace Slskarr.Tests.Search;

public class SearchThrottleTests
{
    private static readonly DateTimeOffset T0 = SearchHarness.Start;

    private static SearchThrottle Create(int hourlyCap, TimeSpan minGap)
    {
        var options = SearchHarness.DefaultOptions();
        options.Search.HourlyCap = hourlyCap;
        options.Search.MinGap = minGap;
        return new SearchThrottle(Options.Create(options));
    }

    [Fact]
    public void Throttle_min_gap_and_hourly_cap()
    {
        var throttle = Create(hourlyCap: 2, minGap: TimeSpan.FromSeconds(10));

        throttle.DelayBeforeNext(T0).ShouldBeNull();
        throttle.Record(T0);

        throttle.DelayBeforeNext(T0.AddSeconds(4)).ShouldBe(TimeSpan.FromSeconds(6));
        throttle.DelayBeforeNext(T0.AddSeconds(10)).ShouldBeNull();
        throttle.Record(T0.AddSeconds(10));
        throttle.HourlyCapReached(T0.AddSeconds(10)).ShouldBeTrue();

        // Third search: the hourly window is full until the first search is an hour old.
        throttle.DelayBeforeNext(T0.AddSeconds(30)).ShouldBe(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(30));
        throttle.DelayBeforeNext(T0.AddMinutes(59)).ShouldBe(TimeSpan.FromMinutes(1));
        throttle.DelayBeforeNext(T0.AddHours(1)).ShouldBeNull();
        throttle.HourlyCapReached(T0.AddHours(1)).ShouldBeFalse();
    }

    [Fact]
    public void Seeded_records_count_toward_the_window()
    {
        var throttle = Create(hourlyCap: 1, minGap: TimeSpan.Zero);
        throttle.Record(T0.AddMinutes(-90));
        throttle.DelayBeforeNext(T0).ShouldBeNull();

        throttle.Record(T0.AddMinutes(-30));
        throttle.DelayBeforeNext(T0).ShouldBe(TimeSpan.FromMinutes(30));
    }
}
