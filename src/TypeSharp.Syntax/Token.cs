namespace TypeSharp.Syntax;

public enum TokenKind
{
    // Literals
    IntegerLiteral,
    FloatLiteral,
    StringLiteral,
    TrueLiteral,
    FalseLiteral,
    NullLiteral,
    BytesLiteral,

    // Identifiers & Keywords
    Identifier,
    TypeIdentifier,

    // Type Keywords
    VoidKeyword,
    BoolKeyword,
    Int8Keyword,
    UInt8Keyword,
    Int16Keyword,
    UInt16Keyword,
    Int32Keyword,
    UInt32Keyword,
    Int64Keyword,
    UInt64Keyword,
    Float32Keyword,
    Float64Keyword,
    DecimalKeyword,
    BigintKeyword,
    StringKeyword,
    BytesKeyword,
    DateTimeKeyword,
    GuidKeyword,
    NumberKeyword,
    AnyKeyword,

    // Declaration Keywords
    ExportKeyword,
    ImportKeyword,
    FromKeyword,
    AsKeyword,
    DefaultKeyword,
    ClassKeyword,
    InterfaceKeyword,
    EnumKeyword,
    TypeKeyword,
    FuncKeyword,
    AsyncKeyword,
    AwaitKeyword,
    ReturnKeyword,
    IfKeyword,
    ElseKeyword,
    SwitchKeyword,
    CaseKeyword,
    WhileKeyword,
    DoKeyword,
    ForKeyword,
    BreakKeyword,
    ContinueKeyword,
    InKeyword,
    OfKeyword,
    NewKeyword,
    ThisKeyword,
    SuperKeyword,
    StaticKeyword,
    AbstractKeyword,
    PrivateKeyword,
    PublicKeyword,
    ProtectedKeyword,
    ReadonlyKeyword,
    GetKeyword,
    SetKeyword,
    LetKeyword,
    ConstKeyword,
    MatchKeyword,
    ThrowKeyword,
    TryKeyword,
    CatchKeyword,
    FinallyKeyword,
    MatchKeyword2,
    YieldKeyword,
    FromKeyword2,
    PromiseKeyword,
    InstanceofKeyword,
    ExtendsKeyword,
    DeleteKeyword,

    // Punctuation
    OpenParen,
    CloseParen,
    OpenBrace,
    CloseBrace,
    OpenBracket,
    CloseBracket,
    Semicolon,
    Colon,
    Comma,
    Dot,
    DotDotDot,
    Question,
    QuestionDot,
    QuestionQuestion,
    Arrow,
    FatArrow,
    Equals,
    DoubleEquals,
    TripleEquals,
    NotEquals,
    StrictNotEquals,
    LessThan,
    GreaterThan,
    LessOrEqual,
    GreaterOrEqual,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Ampersand,
    Pipe,
    Caret,
    Tilde,
    Bang,
    Hash,
    PlusPlus,
    MinusMinus,
    PlusEquals,
    MinusEquals,
    StarEquals,
    SlashEquals,
    PercentEquals,
    AmpersandEquals,
    PipeEquals,
    CaretEquals,
    ShiftLeft,
    ShiftRight,
    ShiftRightUnsigned,
    ShiftLeftEquals,
    ShiftRightEquals,
    StarStar,
    StarStarEquals,
    AmpersandAmpersand,
    PipePipe,
    AmpersandAmpersandEquals,
    PipePipeEquals,
    QuestionQuestionEquals,

    // `template ${…} literal` — Value holds the cooked inner text.
    TemplateLiteral,

    // Special
    EOF,
    Error,
    NewLine,
    Comment,
    Whitespace,
    Nop,
}

public sealed class Token
{
    public TokenKind Kind { get; }
    public string Text { get; }
    public SourceLocation Location { get; }
    public object? Value { get; }
    // Set only by the lexer for a bare Bang after intervening line terminator trivia.
    // Manually constructed tokens have no such source information.
    internal bool HasPrecedingLineTerminator { get; set; }

    // Sorted UTF-16 offsets in the cooked text, present only for lexer-escaped dollars.
    private readonly int[]? _escapedTemplateDollarOffsets;

    public Token(TokenKind kind, string text, SourceLocation location, object? value = null)
        : this(kind, text, location, value, null)
    {
    }

    internal Token(TokenKind kind, string text, SourceLocation location, object? value,
        int[]? escapedTemplateDollarOffsets)
    {
        Kind = kind;
        Text = text;
        Location = location;
        Value = value;
        _escapedTemplateDollarOffsets = escapedTemplateDollarOffsets;
    }

    internal bool IsEscapedTemplateDollar(int offset) =>
        _escapedTemplateDollarOffsets is { } offsets && Array.BinarySearch(offsets, offset) >= 0;

    public override string ToString() => $"{Kind}: {Text}";
}
