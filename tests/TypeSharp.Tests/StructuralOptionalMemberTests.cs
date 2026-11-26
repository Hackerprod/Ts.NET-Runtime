using System.Collections.Generic;
using TypeSharp.Semantics.TypeSystem;
using Xunit;

namespace TypeSharp.Tests;

public sealed class StructuralOptionalMemberTests
{
    private static TsClassType Source() => new("Source");
    private static TsInterfaceType Contract() => new("Contract");
    private static TsProperty OptionalLabel() => new("label", new TsNullableType(TsType.String));
    private static TsProperty RequiredName() => new("name", TsType.String);
    private static TsMethod ReadMethod() => new("Read", TsType.String, new List<TsParameter>());

    [Fact]
    public void MissingOptionalDoesNotHideMissingRequiredProperty()
    {
        var source = Source();
        var target = Contract();
        target.Properties.Add("label", OptionalLabel());
        target.Properties.Add("name", RequiredName());
        bool actual = TsType.IsCompatibleWith(source, target);
        Assert.False(actual, $"missing-required: expected False; actual {actual}");
    }

    [Fact]
    public void MissingOptionalDoesNotHideWrongRequiredPropertyType()
    {
        var source = Source();
        var target = Contract();
        target.Properties.Add("label", OptionalLabel());
        target.Properties.Add("name", RequiredName());
        source.Fields.Add("name", new TsField("name", TsType.Bool));
        bool actual = TsType.IsCompatibleWith(source, target);
        Assert.False(actual, $"wrong-required: expected False; actual {actual}");
    }

    [Fact]
    public void MissingOptionalDoesNotHideMissingMethod()
    {
        var source = Source();
        var target = Contract();
        target.Properties.Add("label", OptionalLabel());
        target.Methods.Add("Read", ReadMethod());
        bool actual = TsType.IsCompatibleWith(source, target);
        Assert.False(actual, $"missing-method: expected False; actual {actual}");
    }

    [Fact]
    public void MissingOptionalWithMatchingRequiredMembersIsCompatible()
    {
        var source = Source();
        var target = Contract();
        target.Properties.Add("label", OptionalLabel());
        target.Properties.Add("name", RequiredName());
        target.Methods.Add("Read", ReadMethod());
        source.Fields.Add("name", new TsField("name", TsType.String));
        source.Methods.Add("Read", ReadMethod());
        bool actual = TsType.IsCompatibleWith(source, target);
        Assert.True(actual, $"matching-required: expected True; actual {actual}");
    }

    [Fact]
    public void MissingRequiredBeforeMissingOptionalIsIncompatible()
    {
        var source = Source();
        var target = Contract();
        target.Properties.Add("name", RequiredName());
        target.Properties.Add("label", OptionalLabel());
        bool actual = TsType.IsCompatibleWith(source, target);
        Assert.False(actual, $"required-first: expected False; actual {actual}");
    }

    [Fact]
    public void PresentOptionalWithWrongTypeIsIncompatible()
    {
        var source = Source();
        var target = Contract();
        target.Properties.Add("label", OptionalLabel());
        source.Fields.Add("label", new TsField("label", TsType.Bool));
        bool actual = TsType.IsCompatibleWith(source, target);
        Assert.False(actual, $"wrong-optional: expected False; actual {actual}");
    }
}
