namespace TypeSharp.Syntax.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info,
    Hint,
}

public enum DiagnosticCode
{
    None = 0,

    // Parser (1xxx)
    TS1001 = 1001,
    TS1002 = 1002,
    TS1003 = 1003,
    TS1004 = 1004,

    // Binder (2xxx)
    TS2001 = 2001,
    TS2002 = 2002,
    TS2003 = 2003,
    TS2004 = 2004,
    TS2005 = 2005,
    TS2006 = 2006,
    TS2007 = 2007,
    TS2008 = 2008,
    TS2009 = 2009,
    TS2010 = 2010,
    TS2011 = 2011,
    TS2012 = 2012,
    TS2013 = 2013,
    TS2014 = 2014,
    TS2015 = 2015,
    TS2016 = 2016,
    TS2017 = 2017,
    TS2018 = 2018,

    // Compilation (3xxx)
    TS3001 = 3001,
    TS3002 = 3002,
    TS3003 = 3003,
    TS3004 = 3004,

    // IR / Bytecode (4xxx)
    TS4001 = 4001,

    // Runtime (5xxx)
    TS5001 = 5001,
}

public sealed class Diagnostic
{
    public DiagnosticSeverity Severity { get; }
    public DiagnosticCode Code { get; }
    public string Message { get; }
    public SourceLocation Location { get; }
    public string? ModuleId { get; set; }

    public Diagnostic(DiagnosticSeverity severity, string message, SourceLocation location,
        DiagnosticCode code = DiagnosticCode.None)
    {
        Severity = severity;
        Message = message;
        Location = location;
        Code = code;
    }

    public override string ToString()
    {
        var code = Code != DiagnosticCode.None ? $" [{Code}]" : "";
        var module = ModuleId != null ? $" ({ModuleId})" : "";
        return $"{Location}{module}{code}: {Severity.ToString().ToLowerInvariant()}: {Message}";
    }
}

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = new();

    public IReadOnlyList<Diagnostic> All => _diagnostics;
    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public int ErrorCount => _diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

    public void Add(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    public void AddRange(DiagnosticBag other)
    {
        _diagnostics.AddRange(other._diagnostics);
    }

    public void Error(string message, SourceLocation location, DiagnosticCode code = DiagnosticCode.None) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, location, code));

    public void Warning(string message, SourceLocation location, DiagnosticCode code = DiagnosticCode.None) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, message, location, code));

    public void Info(string message, SourceLocation location, DiagnosticCode code = DiagnosticCode.None) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, message, location, code));

    public void Hint(string message, SourceLocation location, DiagnosticCode code = DiagnosticCode.None) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Hint, message, location, code));

    public void Clear() => _diagnostics.Clear();

    public IEnumerable<Diagnostic> GetErrors() => _diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    public IEnumerable<Diagnostic> GetWarnings() => _diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning);

    public string FormatAll() => string.Join("\n", _diagnostics.Select(d => d.ToString()));

    public void SetModule(string moduleId)
    {
        foreach (var d in _diagnostics)
            d.ModuleId = moduleId;
    }
}
