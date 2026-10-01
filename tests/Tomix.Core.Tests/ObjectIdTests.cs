using System.Text.Json;
using System.Text.Json.Serialization;
using Tomix.Core.Models;

namespace Tomix.Core.Tests;

/// <summary>
/// The printed form of <see cref="ObjectId"/> and its JSON contract, including through a
/// source-generated context (the protocol and AOT path).
/// </summary>
public sealed class ObjectIdTests
{
    [Theory]
    [InlineData(1L, "o1")]
    [InlineData(35L, "oz")]
    [InlineData(36L, "o10")]
    [InlineData(1767L, "o1d3")]
    [InlineData(long.MaxValue, "o1y2p0ij32e8e7")]
    public void ToString_PrintsPrefixedBase36(long value, string expected)
    {
        var id = new ObjectId(value);
        Assert.Equal(expected, id.ToString());
        Assert.Equal(id, ObjectId.Parse(expected));
    }

    [Fact]
    public void Parse_AcceptsUppercase()
        => Assert.Equal(new ObjectId(1767), ObjectId.Parse("O1D3"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("o")]
    [InlineData("o0")]
    [InlineData("x12")]
    [InlineData("o1-2")]
    [InlineData("o1y2p0ij32e8e8")] // long.MaxValue + 1
    public void TryParse_RejectsNonIds(string? text)
        => Assert.False(ObjectId.TryParse(text, out _));

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Constructor_RejectsNonPositive(long value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectId(value));

    [Fact]
    public void Json_RoundTripsThroughSourceGeneratedContext()
    {
        var id = new ObjectId(1767);

        var json = JsonSerializer.Serialize(id, LiveSessionJsonContext.Default.ObjectId);

        Assert.Equal("\"o1d3\"", json);
        Assert.Equal(id, JsonSerializer.Deserialize(json, LiveSessionJsonContext.Default.ObjectId));
    }

    [Fact]
    public void Json_RoundTripsAsDictionaryKey()
    {
        var map = new Dictionary<ObjectId, string> { [new ObjectId(36)] = "Sales/Amount" };

        var json = JsonSerializer.Serialize(map, LiveSessionJsonContext.Default.DictionaryObjectIdString);

        Assert.Equal("{\"o10\":\"Sales/Amount\"}", json);
        Assert.Equal(map, JsonSerializer.Deserialize(json, LiveSessionJsonContext.Default.DictionaryObjectIdString));
    }

    [Fact]
    public void Json_RejectsMalformedId()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"nope\"", LiveSessionJsonContext.Default.ObjectId));

    [Fact]
    public void ModelChangeBatch_SerializesToTheEventShape()
    {
        var batch = new ModelChangeBatch(42, "t17", new ChangeOrigin("mcp-1", ChangeOriginKind.Apply),
        [
            new ModelChange(new ObjectId(1767), ModelObjectKind.Measure, ModelChangeKind.Renamed,
                "Sales/Sales Amount", OldPath: "Sales/Total Sales"),
            new ModelChange(new ObjectId(1773), ModelObjectKind.Measure, ModelChangeKind.Modified,
                "Sales/Margin %", Properties: ["Expression"])
        ]);

        var json = JsonSerializer.Serialize(batch, LiveSessionJsonContext.Default.ModelChangeBatch);

        Assert.Equal(
            """{"version":42,"transaction":"t17","origin":{"client":"mcp-1","kind":"apply"},"changes":[""" +
            """{"id":"o1d3","objectKind":"Measure","change":"renamed","path":"Sales/Sales Amount","oldPath":"Sales/Total Sales"},""" +
            """{"id":"o1d9","objectKind":"Measure","change":"modified","path":"Sales/Margin %","properties":["Expression"]}]}""",
            json);
        Assert.Equivalent(batch, JsonSerializer.Deserialize(json, LiveSessionJsonContext.Default.ModelChangeBatch), strict: true);
    }

    [Fact]
    public void ModelObject_OmitsIdWhenNull()
    {
        var obj = new ModelObject("Amount", ModelObjectKind.Measure, "Sales/Amount", null, null, null, false, null, []);

        Assert.DoesNotContain("\"id\"", JsonSerializer.Serialize(obj, LiveSessionJsonContext.Default.ModelObject));
        Assert.Contains("\"id\":\"o1\"",
            JsonSerializer.Serialize(obj with { Id = new ObjectId(1) }, LiveSessionJsonContext.Default.ModelObject));
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ObjectId))]
[JsonSerializable(typeof(Dictionary<ObjectId, string>))]
[JsonSerializable(typeof(ModelChangeBatch))]
[JsonSerializable(typeof(ModelObject))]
internal sealed partial class LiveSessionJsonContext : JsonSerializerContext;
