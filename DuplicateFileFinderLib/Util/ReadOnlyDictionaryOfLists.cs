using System.Collections;

namespace DuplicateFileFinderLib.Util;

// Wraps a Dictionary<TKey, List<TValue>> in a readonly wrapper. i.e., an IReadOnlyDictionary<TKey, IReadOnlyList<TValue>>
// It doesn't wrap the List<TValue> values of the dictionary in a read only wrapper, just returns a IReadOnlyList type 
// This may have been overengineered!!!

public sealed class ReadOnlyDictionaryOfLists<TKey, TValue>(Dictionary<TKey, List<TValue>> source)
    : IReadOnlyDictionary<TKey, IReadOnlyList<TValue>>
    where TKey : notnull
{
    private readonly Dictionary<TKey, List<TValue>> _source = source ?? throw new ArgumentNullException(nameof(source));

    public IReadOnlyList<TValue> this[TKey key] => _source[key];

    public IEnumerable<TKey> Keys => _source.Keys;

    public IEnumerable<IReadOnlyList<TValue>> Values
        => new ValuesEnumerable(_source);

    public int Count => _source.Count;

    public bool ContainsKey(TKey key)
    {
        return _source.ContainsKey(key);
    }

    public bool TryGetValue(TKey key, out IReadOnlyList<TValue> value)
    {
        if (_source.TryGetValue(key, out var list))
        {
            value = list;
            return true;
        }

        value = null!;
        return false;
    }

    IEnumerator<KeyValuePair<TKey, IReadOnlyList<TValue>>> IEnumerable<KeyValuePair<TKey, IReadOnlyList<TValue>>>.
        GetEnumerator()
    {
        return new Enumerator(_source);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return new Enumerator(_source);
    }

    public Enumerator GetEnumerator()
    {
        return new Enumerator(_source);
    }

    public struct Enumerator : IEnumerator<KeyValuePair<TKey, IReadOnlyList<TValue>>>
    {
        private Dictionary<TKey, List<TValue>>.Enumerator _inner;

        internal Enumerator(Dictionary<TKey, List<TValue>> source)
        {
            _inner = source.GetEnumerator();
        }

        public KeyValuePair<TKey, IReadOnlyList<TValue>> Current
        {
            get
            {
                var current = _inner.Current;
                return new KeyValuePair<TKey, IReadOnlyList<TValue>>(current.Key, current.Value);
            }
        }

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            return _inner.MoveNext();
        }

        public void Reset()
        {
            ((IEnumerator)_inner).Reset();
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }

    private readonly struct ValuesEnumerable(Dictionary<TKey, List<TValue>> source) : IEnumerable<IReadOnlyList<TValue>>
    {
        // ReSharper disable once UnusedMember.Local
        public ValuesEnumerator GetEnumerator()
        {
            return new ValuesEnumerator(source);
        }

        IEnumerator<IReadOnlyList<TValue>> IEnumerable<IReadOnlyList<TValue>>.GetEnumerator()
        {
            return new ValuesEnumerator(source);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new ValuesEnumerator(source);
        }
    }

    private struct ValuesEnumerator(Dictionary<TKey, List<TValue>> source) : IEnumerator<IReadOnlyList<TValue>>
    {
        private Dictionary<TKey, List<TValue>>.ValueCollection.Enumerator _inner = source.Values.GetEnumerator();

        public IReadOnlyList<TValue> Current => _inner.Current;

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            return _inner.MoveNext();
        }

        public void Reset()
        {
            ((IEnumerator)_inner).Reset();
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }
}