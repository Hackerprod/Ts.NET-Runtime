using TypeSharp.Interop.Marshalling;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class MarshallerWrappingTests
{
    private sealed class Envelope
    {
        public List<int> Nested { get; } = new(13) { 3, 5, 7 };
    }

    private sealed class OrdinaryProperties
    {
        public int Item => 42;
        public object? Empty => null;
        public int Throws => throw new InvalidOperationException("ordinary getter");
        public static int Static => 99;
        public int WriteOnly { set { } }
    }

    private static Dictionary<string, TsValue> ObjectFields(TsValue value, string name)
    {
        var obj = Assert.IsType<TsObjectValue>(value).Value;
        Assert.Equal(name, obj.TypeName);
        return obj.Fields;
    }

    private static void Integer(Dictionary<string, TsValue> fields, string key, int expected)
    {
        Assert.True(fields.TryGetValue(key, out var value));
        Assert.Equal(expected, Assert.IsType<TsInt32Value>(value).Value);
    }

    private static void PresentNull(Dictionary<string, TsValue> fields, string key)
    {
        Assert.True(fields.TryGetValue(key, out var value));
        Assert.IsType<TsNull>(value);
        Assert.Same(TsValue.Null, value);
    }

    private static Dictionary<string, TsValue> Direct()
    {
        var actual = Marshaller.FromManaged(new List<int>(9) { 1, 2 });
        var fields = ObjectFields(actual, typeof(List<int>).Name);
        Integer(fields, "Count", 2);
        Integer(fields, "Capacity", 9);
        return fields;
    }

    private static Dictionary<string, TsValue> Arguments()
    {
        var result = Marshaller.FromManagedArray(new object?[] { new Envelope() }, new[] { typeof(Envelope) });
        var args = Assert.IsType<TsValue[]>(result);
        var outer = ObjectFields(Assert.Single(args), nameof(Envelope));
        Assert.True(outer.TryGetValue(nameof(Envelope.Nested), out var nested));
        var fields = ObjectFields(nested!, typeof(List<int>).Name);
        Integer(fields, "Count", 3);
        Integer(fields, "Capacity", 13);
        return fields;
    }

    internal static void Preflight(bool stock)
    {
        var direct = Direct();
        var nested = Arguments();
        if (stock)
        {
            PresentNull(direct, "Item");
            PresentNull(nested, "Item");
        }
        else
        {
            Assert.False(direct.ContainsKey("Item"));
            Assert.False(nested.ContainsKey("Item"));
        }
        new MarshallerWrappingTests().OrdinaryPropertiesArePreserved();
    }

    [Fact]
    public void DirectListOmitsIndexer()
    {
        var fields = Direct();
        Assert.False(fields.ContainsKey("Item"));
    }

    [Fact]
    public void ArgumentArrayNestedListOmitsIndexer()
    {
        var fields = Arguments();
        Assert.False(fields.ContainsKey("Item"));
    }

    [Fact]
    public void OrdinaryPropertiesArePreserved()
    {
        var fields = ObjectFields(Marshaller.FromManaged(new OrdinaryProperties()), nameof(OrdinaryProperties));
        Integer(fields, "Item", 42);
        PresentNull(fields, "Empty");
        PresentNull(fields, "Throws");
        Assert.False(fields.ContainsKey("Static"));
        Assert.False(fields.ContainsKey("WriteOnly"));
    }
}
