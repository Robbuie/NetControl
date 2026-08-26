using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NetControl.Core.Persistence;

/// <summary>
/// Builds the JSON that goes in <see cref="EventRecord.Detail"/>.
///
/// Hand-written rather than reflected over an anonymous type, for two reasons: the property order
/// stays the one written here, which makes the column readable by eye in a SQL browser; and there
/// is no reflection, so single-file or trimmed publishing stays warning-free.
///
/// Null and empty values are dropped. A detail column full of <c>"gateway": null</c> is noise, and
/// the absence of a key already means "not present".
/// </summary>
public sealed class EventDetail
{
    private readonly List<KeyValuePair<string, object>> _values = [];

    public bool IsEmpty => _values.Count == 0;

    public EventDetail Add(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _values.Add(new KeyValuePair<string, object>(name, value));
        }

        return this;
    }

    public EventDetail Add(string name, long value)
    {
        _values.Add(new KeyValuePair<string, object>(name, value));
        return this;
    }

    public EventDetail Add(string name, bool value)
    {
        _values.Add(new KeyValuePair<string, object>(name, value));
        return this;
    }

    public EventDetail Add(string name, object? value) => Add(name, value?.ToString());

    /// <summary>
    /// Raw bytes as upper-case hex. Options and status payloads go in here: the point of keeping
    /// them is to be able to compare them against a capture, and hex is what a capture shows.
    /// </summary>
    public EventDetail AddHex(string name, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return this;
        }

        var text = new StringBuilder(value.Length * 2);
        foreach (byte b in value)
        {
            text.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        _values.Add(new KeyValuePair<string, object>(name, text.ToString()));
        return this;
    }

    /// <summary>Null when nothing was added, so the column stays NULL rather than holding "{}".</summary>
    public string? ToJson()
    {
        if (IsEmpty)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach ((string name, object value) in _values)
            {
                switch (value)
                {
                    case string s:
                        writer.WriteString(name, s);
                        break;
                    case long l:
                        writer.WriteNumber(name, l);
                        break;
                    case bool b:
                        writer.WriteBoolean(name, b);
                        break;
                    default:
                        writer.WriteString(name, value.ToString());
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public override string ToString() => ToJson() ?? "{}";
}
