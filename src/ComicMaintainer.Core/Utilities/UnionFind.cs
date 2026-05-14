namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Disjoint-set / union-find data structure with path compression. Used to
/// collapse arbitrary chains of equivalent identifiers (e.g. series titles
/// linked together via aliases) into a single representative key.
/// </summary>
public sealed class UnionFind<T> where T : notnull
{
    private readonly Dictionary<T, T> _parent;

    public UnionFind(IEqualityComparer<T> comparer)
    {
        _parent = new Dictionary<T, T>(comparer);
    }

    public bool Contains(T value) => _parent.ContainsKey(value);

    public void Add(T value)
    {
        if (!_parent.ContainsKey(value))
        {
            _parent[value] = value;
        }
    }

    public T Find(T value)
    {
        if (!_parent.TryGetValue(value, out var parent))
        {
            _parent[value] = value;
            return value;
        }

        // Path compression (iterative to avoid stack overflows on pathological chains).
        var root = value;
        while (!_parent[root].Equals(root))
        {
            root = _parent[root];
        }

        var current = value;
        while (!_parent[current].Equals(root))
        {
            var next = _parent[current];
            _parent[current] = root;
            current = next;
        }
        return root;
    }

    public void Union(T a, T b)
    {
        Add(a);
        Add(b);
        var rootA = Find(a);
        var rootB = Find(b);
        if (!rootA.Equals(rootB))
        {
            _parent[rootA] = rootB;
        }
    }
}
