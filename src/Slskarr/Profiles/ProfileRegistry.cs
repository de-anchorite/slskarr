using Slskarr.Search;

namespace Slskarr.Profiles;

public sealed class ProfileRegistry(IEnumerable<IMediaProfile> profiles)
{
    public IReadOnlyList<IMediaProfile> All { get; } = [.. profiles];

    public IMediaProfile? ForRequest(SearchRequest r) => All.FirstOrDefault(p => p.Handles(r));

    public IMediaProfile? ForSabCategory(string cat) =>
        All.FirstOrDefault(p => string.Equals(p.SabCategory, cat, StringComparison.OrdinalIgnoreCase));

    public IMediaProfile? ByName(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
