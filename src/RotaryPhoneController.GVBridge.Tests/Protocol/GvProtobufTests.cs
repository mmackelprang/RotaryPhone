using System.Text.Json;
using RotaryPhoneController.GVBridge.Protocol;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Protocol;

public class GvProtobufTests
{
    [Fact]
    public void GetString_ReturnsValueAtPath()
    {
        var json = JsonDocument.Parse("""[null,null,null,null,"Hello","t.+19193718044"]""");
        var result = GvProtobuf.GetString(json.RootElement, 4);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void GetString_ReturnsNullForMissingIndex()
    {
        var json = JsonDocument.Parse("""["a","b"]""");
        Assert.Null(GvProtobuf.GetString(json.RootElement, 5));
    }

    [Fact]
    public void GetInt_ReturnsValueFromNestedArray()
    {
        var json = JsonDocument.Parse("""[[[[1,null,69]]]]""");
        var inner = GvProtobuf.GetArray(json.RootElement, 0, 0, 0);
        Assert.NotNull(inner);
        Assert.Equal(69, GvProtobuf.GetInt(inner!.Value, 2));
    }

    [Fact]
    public void BuildRequest_CreatesPositionalArray()
    {
        var json = GvProtobuf.BuildArray(null, null, null, null, "Hello", "t.+1919");
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Hello", doc.RootElement[4].GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement[0].ValueKind);
    }
}
