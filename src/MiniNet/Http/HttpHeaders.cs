using System.Collections;

namespace MiniNet.Http;

public sealed class HttpHeaders : IEnumerable<KeyValuePair<string, string>>
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _values.Count;

    public string? this[string name]
    {
        get => _values.TryGetValue(name, out var value) ? value : null;
        set
        {
            if (value is null) _values.Remove(name);
            else _values[name] = value;
        }
    }

    public bool ContainsKey(string name) => _values.ContainsKey(name);

    public bool TryGetValue(string name, out string value) => _values.TryGetValue(name, out value!);

    public bool Remove(string name) => _values.Remove(name);

    public void Append(string name, string value)
    {
        _values[name] = _values.TryGetValue(name, out var existing) ? existing + ", " + value : value;
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
