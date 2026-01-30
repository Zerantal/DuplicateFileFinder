namespace DuplicateFileFinderLib.Repository.Core.Helpers;

internal sealed class ProjectedReadOnlyDictionary<TKey, TSource, TValue>(
    IReadOnlyDictionary<TKey, TSource> source,
    Func<TSource, TValue> project)
    : IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly IReadOnlyDictionary<TKey, TSource> _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly Func<TSource, TValue> _project = project ?? throw new ArgumentNullException(nameof(project));

    public int Count => _source.Count;
    public IEnumerable<TKey> Keys => _source.Keys;

    public IEnumerable<TValue> Values
    {
        get
        {
            foreach (var kv in _source)
                yield return _project(kv.Value);
        }
    }

    public TValue this[TKey key] => _project(_source[key]);

    public bool ContainsKey(TKey key) => _source.ContainsKey(key);

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_source.TryGetValue(key, out var src))
        {
            value = _project(src);
            return true;
        }

        value = default!;
        return false;
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        foreach (var kv in _source)
            yield return new KeyValuePair<TKey, TValue>(kv.Key, _project(kv.Value));
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
