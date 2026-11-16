using Microsoft.EntityFrameworkCore;
using Shouldly;
using Slskarr.Data;
using Slskarr.Profiles;
using Slskarr.Search;
using Slskarr.Soulseek;

namespace Slskarr.Tests.Search;

public class SearchWorkerTests
{
    private sealed class StubProfile(Func<IEnumerable<SlskdSearchResponse>, IReadOnlyList<ReleaseCandidate>> candidates) : IMediaProfile
    {
        public string Name => "stub";
        public IReadOnlyList<int> Categories => [1];
        public string SabCategory => "stub";
        public bool CoalesceBySuperset => false;
    public NewznabCategory CapsCategory => new(1, "Stub", []);
    public IReadOnlyList<int> ItemCategories(HitEntity hit) => [];
    public IReadOnlyList<(string Name, string Value)> ItemAttributes(HitEntity hit) => [];
        public IReadOnlyList<string> Ranking => [];
        public bool Handles(SearchRequest r) => true;
        public string BuildQuery(SearchRequest r) => r.Query ?? "x";
        public IReadOnlyList<ReleaseCandidate> ToCandidates(IEnumerable<SlskdSearchResponse> responses) => candidates(responses);
        public int FormatRank(string format) => 0;
    }

    private static ReleaseCandidate Candidate(string format, params CandidateFile[] files) =>
        new("stub", "peer", @"Music\Artist\Album", files.Sum(f => f.Size),
            new ParsedName("Artist", "Album", null, format, "Album"), $"Artist - Album [{format}]", true, 0, 100, files);

    private static CandidateFile File(string name, long size, string subdir = "") =>
        new($@"Music\Artist\Album\{name}", size, subdir, 320, null, 44100, 200, false);

    private static async Task<(SearchHarness H, List<HitEntity> Hits)> Run(params ReleaseCandidate[] candidates)
    {
        var h = new SearchHarness();
        var profile = new StubProfile(_ => candidates);
        var registry = new ProfileRegistry([profile]);
        var throttle = new SearchThrottle(h.Options);
        var coordinator = new SearchCoordinator(registry, h.Db, h.Options, throttle, h.Time);
        var worker = new SearchWorker(coordinator, throttle, registry, new Ranker(), h.Slskd, h.Db, h.Options, h.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SearchWorker>.Instance);
        var search = coordinator.SearchAsync(new SearchRequest("album", null, null, []), CancellationToken.None);
        await SearchHarness.Until(() => search.IsCompleted || h.Jobs().Any(j => j.State == SearchJobState.Queued), "queued");
        (await worker.RunOnceAsync(CancellationToken.None)).ShouldBeTrue();
        await search.Within();
        return (h, h.Hits());
    }

    [Fact]
    public async Task Stored_hit_has_candidate_files()
    {
        var (h, _) = await Run(Candidate("FLAC", File("01.flac", 100), File("02.flac", 250, "CD2")));
        using (h)
        {
            using var db = h.Db.CreateDbContext();
            var hit = await db.Hits.Include(x => x.Files).SingleAsync();
            hit.Files.Count.ShouldBe(2);
            hit.Size.ShouldBe(350);
            hit.Files.Single(f => f.Subdir == "CD2").LengthSeconds.ShouldBe(200);
            hit.Files.ShouldAllBe(f => f.BitRate == 320 && f.SampleRate == 44100 && f.IsVbr == false);
        }
    }

    [Fact]
    public async Task Same_folder_two_formats_are_two_hits()
    {
        var (h, hits) = await Run(Candidate("FLAC", File("01.flac", 100)), Candidate("MP3", File("01.mp3", 10)));
        using (h)
        {
            hits.Count.ShouldBe(2);
            hits.Select(x => x.Format).Order().ShouldBe(["FLAC", "MP3"]);
        }
    }
}
