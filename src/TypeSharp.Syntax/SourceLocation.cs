namespace TypeSharp.Syntax;

public readonly struct SourceLocation : IEquatable<SourceLocation>
{
    public string Source { get; }
    public int Line { get; }
    public int Column { get; }
    public int Offset { get; }

    public SourceLocation(string source, int line, int column, int offset)
    {
        Source = source;
        Line = line;
        Column = column;
        Offset = offset;
    }

    public override string ToString() => $"{Source}({Line},{Column})";
    public bool Equals(SourceLocation other) => Source == other.Source && Line == other.Line && Column == other.Column;
    public override bool Equals(object? obj) => obj is SourceLocation other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Source, Line, Column);
}

public readonly struct SourceRange : IEquatable<SourceRange>
{
    public SourceLocation Start { get; }
    public SourceLocation End { get; }

    public SourceRange(SourceLocation start, SourceLocation end)
    {
        Start = start;
        End = end;
    }

    public SourceRange Merge(SourceRange other) =>
        new(Start, other.End);

    public bool Equals(SourceRange other) => Start.Equals(other.Start) && End.Equals(other.End);
    public override bool Equals(object? obj) => obj is SourceRange other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Start, End);

    public static implicit operator SourceRange(SourceLocation loc) =>
        new(loc, loc);
}
