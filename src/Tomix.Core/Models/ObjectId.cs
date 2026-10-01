using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tomix.Core.Models;

/// <summary>
/// A session-scoped object identity in a live model session (<see cref="ILiveModelSession"/>).
/// The ID is assigned when the model loads or the object is created, survives renames and moves,
/// and is never reused within the session, even after the object is removed. Printed and
/// serialized as <c>o</c> followed by lowercase base-36 digits, for example <c>o1k3</c>.
/// </summary>
/// <remarks>
/// IDs do not persist across sessions: clients must not store them. Anything that outlives a
/// session addresses objects by path (see ADR 0001 §2).
/// </remarks>
[JsonConverter(typeof(ObjectIdJsonConverter))]
public readonly record struct ObjectId : IComparable<ObjectId>
{
    private const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
    private const char Prefix = 'o';

    /// <param name="value">The ID's sequence number. Must be positive; zero is reserved for <c>default</c>.</param>
    public ObjectId(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    /// <summary>The ID's sequence number within its session.</summary>
    public long Value { get; }

    public int CompareTo(ObjectId other) => Value.CompareTo(other.Value);

    public override string ToString()
    {
        if (Value == 0)
            return string.Empty;

        Span<char> buffer = stackalloc char[14];
        var position = buffer.Length;
        var remaining = Value;
        while (remaining > 0)
        {
            buffer[--position] = Digits[(int)(remaining % 36)];
            remaining /= 36;
        }

        buffer[--position] = Prefix;
        return new string(buffer[position..]);
    }

    /// <summary>Parses the printed form (<c>o1k3</c>). Letters are accepted in either case.</summary>
    /// <exception cref="FormatException"><paramref name="text"/> is not an object ID.</exception>
    public static ObjectId Parse(string text)
        => TryParse(text, out var id)
            ? id
            : throw new FormatException($"'{text}' is not an object ID; expected 'o' followed by base-36 digits, for example 'o1k3'.");

    public static bool TryParse([NotNullWhen(true)] string? text, out ObjectId id)
    {
        id = default;
        if (text is null || text.Length < 2 || char.ToLowerInvariant(text[0]) != Prefix)
            return false;

        long value = 0;
        foreach (var c in text.AsSpan(1))
        {
            var digit = Digits.IndexOf(char.ToLowerInvariant(c), StringComparison.Ordinal);
            if (digit < 0 || value > (long.MaxValue - digit) / 36)
                return false;

            value = (value * 36) + digit;
        }

        if (value == 0)
            return false;

        id = new ObjectId(value);
        return true;
    }

    public static bool operator <(ObjectId left, ObjectId right) => left.CompareTo(right) < 0;

    public static bool operator <=(ObjectId left, ObjectId right) => left.CompareTo(right) <= 0;

    public static bool operator >(ObjectId left, ObjectId right) => left.CompareTo(right) > 0;

    public static bool operator >=(ObjectId left, ObjectId right) => left.CompareTo(right) >= 0;
}

/// <summary>Serializes <see cref="ObjectId"/> as its printed string form, also as a dictionary key.
/// Attribute-registered, so source-generated <see cref="JsonSerializerContext"/>s pick it up.</summary>
public sealed class ObjectIdJsonConverter : JsonConverter<ObjectId>
{
    public override ObjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && ObjectId.TryParse(reader.GetString(), out var id)
            ? id
            : throw new JsonException("Expected an object ID string such as \"o1k3\".");

    public override void Write(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());

    public override ObjectId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ObjectId.TryParse(reader.GetString(), out var id)
            ? id
            : throw new JsonException("Expected an object ID property name such as \"o1k3\".");

    public override void WriteAsPropertyName(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.ToString());
}
