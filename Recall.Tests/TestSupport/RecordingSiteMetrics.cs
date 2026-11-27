using Recall.Web.Services.Metrics;

namespace Recall.Tests.TestSupport;

/// <summary>An <see cref="ISiteMetrics"/> that keeps every key it was given, for assertions.</summary>
public sealed class RecordingSiteMetrics : ISiteMetrics
{
    private readonly List<string> _keys = [];

    public IReadOnlyList<string> Keys
    {
        get { lock (_keys) return _keys.ToList(); }
    }

    public void Increment(string key)
    {
        lock (_keys) _keys.Add(key);
    }

    public int CountOf(string key) => Keys.Count(k => k == key);
}
