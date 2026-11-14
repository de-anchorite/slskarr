using Slskarr.Profiles;

namespace Slskarr.Search;

/// <summary>Orders release candidates by a configurable, lexicographic list of criteria, then applies the result cap.</summary>
public sealed class Ranker
{
    public IReadOnlyList<ReleaseCandidate> Rank(
        IEnumerable<ReleaseCandidate> candidates,
        SearchRequest request,
        IMediaProfile profile,
        IReadOnlyList<string> criteria,
        int cap)
    {
        var scored = candidates.Select(c => (Candidate: c, Bucket: NameMatch.Score(c.Name, request))).ToList();
        var comparisons = criteria.Select(name => Build(name, profile)).ToList();

        // LINQ OrderBy is stable, so equal hits keep their input order.
        return scored
            .OrderBy(x => x, Comparer<(ReleaseCandidate Candidate, MatchBucket Bucket)>.Create((x, y) =>
            {
                foreach (var compare in comparisons)
                {
                    var result = compare(x, y);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                return 0;
            }))
            .Select(x => x.Candidate)
            .Take(cap)
            .ToList();
    }

    private static Comparison<(ReleaseCandidate Candidate, MatchBucket Bucket)> Build(string criterion, IMediaProfile profile) =>
        criterion switch
        {
            "NameMatch" => (x, y) => y.Bucket.CompareTo(x.Bucket),
            "FreeSlot" => (x, y) => y.Candidate.FreeSlot.CompareTo(x.Candidate.FreeSlot),
            "QueueLength" => (x, y) => x.Candidate.QueueLength.CompareTo(y.Candidate.QueueLength),
            "UploadSpeed" => (x, y) => y.Candidate.UploadSpeed.CompareTo(x.Candidate.UploadSpeed),
            "Format" => (x, y) => profile.FormatRank(x.Candidate.Name.Format).CompareTo(profile.FormatRank(y.Candidate.Name.Format)),
            _ => throw new ArgumentException($"Unknown ranking criterion '{criterion}'.", nameof(criterion)),
        };
}
