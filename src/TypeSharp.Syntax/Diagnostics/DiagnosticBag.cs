namespace TypeSharp.Syntax.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info,
    Hint,
}

public sealed class Diagnostic
{
    public DiagnosticSeverity Severity { get; }
    public string Message { get; }
    public SourceLocation Location { get; }

    public Diagnostic(DiagnosticSeverity severity, string message, SourceLocation location)
    {
        Severity = severity;
        Message = message;
        Location = location;
    }

    public override string ToString() => $"[{Severity}] {Location}: {Message}";
}

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = new();

    public IReadOnlyList<Diagnostic> All => _diagnostics;
    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public int ErrorCount => _diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

    public void Add(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    public void Error(string message, SourceLocation location) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, location));

    public void Warning(string message, SourceLocation location) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, message, location));

    public void Info(string message, SourceLocation location) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, message, location));

    public void Clear() => _diagnostics.Clear();

    public IEnumerable<Diagnostic> GetErrors() => _diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    public string FormatAll() => string.Join("\n", _diagnostics.Select(d => d.ToString()));
}
