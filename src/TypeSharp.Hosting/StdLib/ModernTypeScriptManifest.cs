namespace TypeSharp.Hosting.StdLib;

public enum ModernTypeScriptFeatureKind
{
    CheckerOnly,
    Parser,
    RuntimeEmit,
    ModuleResolution,
    NotApplicable
}

public sealed record ModernTypeScriptFeature(
    string Version,
    string Name,
    ModernTypeScriptFeatureKind Kind,
    StandardCapabilityStatus Status,
    string Decision,
    string RuntimePolicy,
    IReadOnlyList<string> Tests);

public static class ModernTypeScriptManifest
{
    public static IReadOnlyList<ModernTypeScriptFeature> Features { get; } =
    [
        new(
            "4.9",
            "satisfies operator",
            ModernTypeScriptFeatureKind.CheckerOnly,
            StandardCapabilityStatus.Supported,
            "Validate assignability against the target type and preserve the original expression type.",
            "Fully erased; emits the operand bytecode only.",
            ["SatisfiesExpressionChecksTargetButPreservesExpressionType", "SatisfiesExpressionReportsStructuralMismatch", "ModernTypeScriptFixtures_ClassifySupportedFeatures"]),
        new(
            "5.0",
            "standard decorators",
            ModernTypeScriptFeatureKind.RuntimeEmit,
            StandardCapabilityStatus.Supported,
            "Class/member decorators are evaluated and invoked through the VM class model with standard-shaped context objects.",
            "Decorator calls are emitted explicitly; unsupported decorator forms remain diagnostics until the class model covers them.",
            ["ClassDecorators_EvaluateAndInvokeInOrder", "MethodDecorators_InvokeBeforeClassDecorators", "DecoratorsReceiveStandardShapeContext"]),
        new(
            "5.0",
            "const type parameters",
            ModernTypeScriptFeatureKind.CheckerOnly,
            StandardCapabilityStatus.Supported,
            "A const type parameter switches inline call arguments into const-like inference, preserving literal values and readonly object/array shapes while still enforcing constraints.",
            "Fully erased; the modifier does not affect emitted bytecode.",
            ["ConstTypeParameters_PreserveInlineObjectAndArrayLiterals", "ConstTypeParameters_FallBackToMutableConstraint"]),
        new(
            "5.7",
            "checks for never-initialized variables",
            ModernTypeScriptFeatureKind.CheckerOnly,
            StandardCapabilityStatus.Supported,
            "Locals declared without initializers are tracked by symbol; a read is rejected when no assignment to that symbol exists, including reads from nested functions.",
            "Checker-only diagnostic; no bytecode is emitted for invalid programs.",
            ["NeverInitializedVariable_IsRejectedInsideNestedFunction", "AssignedLaterVariable_IsNotClassifiedAsNeverInitialized"]),
        new(
            "5.0",
            "moduleResolution bundler and resolution customization flags",
            ModernTypeScriptFeatureKind.ModuleResolution,
            StandardCapabilityStatus.Unsupported,
            "The embedded runtime owns module resolution; tsc CLI/module-resolution switches are not accepted as language runtime features.",
            "Not emitted.",
            []),
        new(
            "5.7",
            "path rewriting for relative paths",
            ModernTypeScriptFeatureKind.ModuleResolution,
            StandardCapabilityStatus.Unsupported,
            "Path rewriting is a build/emit concern for JavaScript output and is outside this VM bytecode runtime.",
            "Not emitted.",
            []),
        new(
            "4.9-5.7",
            "editor, watcher, formatter, project references and tsc option changes",
            ModernTypeScriptFeatureKind.NotApplicable,
            StandardCapabilityStatus.Unsupported,
            "These changes do not affect executed TypeSharp source inside the embedded runtime.",
            "No parser, checker or bytecode surface.",
            [])
    ];

    public static ModernTypeScriptFeature? Find(string version, string name) =>
        Features.FirstOrDefault(feature =>
            string.Equals(feature.Version, version, StringComparison.Ordinal) &&
            string.Equals(feature.Name, name, StringComparison.Ordinal));
}
