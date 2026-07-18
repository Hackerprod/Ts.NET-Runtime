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
    WhileKeyword,
    ForKeyword,
    InKeyword,
    OfKeyword,
    NewKeyword,
    ThisKeyword,
    SuperKeyword,
    StaticKeyword,
    PrivateKeyword,
    PublicKeyword,
    ProtectedKeyword,
    ReadonlyKeyword,
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
    AmpersandAmpersand,
    PipePipe,

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

    public Token(TokenKind kind, string text, SourceLocation location, object? value = null)
    {
        Kind = kind;
        Text = text;
        Location = location;
        Value = value;
    }

    public override string ToString() => $"{Kind}: {Text}";
}
