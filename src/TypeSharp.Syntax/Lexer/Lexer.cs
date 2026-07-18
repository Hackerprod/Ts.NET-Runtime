namespace TypeSharp.Syntax;

public sealed class Lexer
{
    private readonly string _source;
    private readonly string _fileName;
    private int _position;
    private int _line = 1;
    private int _column = 1;

    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["void"] = TokenKind.VoidKeyword,
        ["bool"] = TokenKind.BoolKeyword,
        ["int8"] = TokenKind.Int8Keyword,
        ["uint8"] = TokenKind.UInt8Keyword,
        ["int16"] = TokenKind.Int16Keyword,
        ["uint16"] = TokenKind.UInt16Keyword,
        ["int32"] = TokenKind.Int32Keyword,
        ["uint32"] = TokenKind.UInt32Keyword,
        ["int64"] = TokenKind.Int64Keyword,
        ["uint64"] = TokenKind.UInt64Keyword,
        ["float32"] = TokenKind.Float32Keyword,
        ["float64"] = TokenKind.Float64Keyword,
        ["decimal"] = TokenKind.DecimalKeyword,
        ["bigint"] = TokenKind.BigintKeyword,
        ["string"] = TokenKind.StringKeyword,
        ["bytes"] = TokenKind.BytesKeyword,
        ["datetime"] = TokenKind.DateTimeKeyword,
        ["guid"] = TokenKind.GuidKeyword,
        ["number"] = TokenKind.NumberKeyword,
        ["any"] = TokenKind.AnyKeyword,
        ["true"] = TokenKind.TrueLiteral,
        ["false"] = TokenKind.FalseLiteral,
        ["null"] = TokenKind.NullLiteral,
        ["export"] = TokenKind.ExportKeyword,
        ["import"] = TokenKind.ImportKeyword,
        ["from"] = TokenKind.FromKeyword,
        ["as"] = TokenKind.AsKeyword,
        ["default"] = TokenKind.DefaultKeyword,
        ["class"] = TokenKind.ClassKeyword,
        ["interface"] = TokenKind.InterfaceKeyword,
        ["enum"] = TokenKind.EnumKeyword,
        ["type"] = TokenKind.TypeKeyword,
        ["func"] = TokenKind.FuncKeyword,
        ["function"] = TokenKind.FuncKeyword,
        ["async"] = TokenKind.AsyncKeyword,
        ["await"] = TokenKind.AwaitKeyword,
        ["return"] = TokenKind.ReturnKeyword,
        ["if"] = TokenKind.IfKeyword,
        ["else"] = TokenKind.ElseKeyword,
        ["while"] = TokenKind.WhileKeyword,
        ["for"] = TokenKind.ForKeyword,
        ["in"] = TokenKind.InKeyword,
        ["of"] = TokenKind.OfKeyword,
        ["new"] = TokenKind.NewKeyword,
        ["this"] = TokenKind.ThisKeyword,
        ["super"] = TokenKind.SuperKeyword,
        ["static"] = TokenKind.StaticKeyword,
        ["private"] = TokenKind.PrivateKeyword,
        ["public"] = TokenKind.PublicKeyword,
        ["protected"] = TokenKind.ProtectedKeyword,
        ["readonly"] = TokenKind.ReadonlyKeyword,
        ["let"] = TokenKind.LetKeyword,
        ["const"] = TokenKind.ConstKeyword,
        ["match"] = TokenKind.MatchKeyword,
        ["throw"] = TokenKind.ThrowKeyword,
        ["try"] = TokenKind.TryKeyword,
        ["catch"] = TokenKind.CatchKeyword,
        ["finally"] = TokenKind.FinallyKeyword,
    };

    public Lexer(string source, string fileName = "<input>")
    {
        _source = source;
        _fileName = fileName;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (_position < _source.Length)
        {
            var token = ReadNextToken();
            if (token.Kind != TokenKind.Whitespace && token.Kind != TokenKind.Comment && token.Kind != TokenKind.NewLine)
                tokens.Add(token);
        }
        tokens.Add(new Token(TokenKind.EOF, "", CurrentLocation()));
        return tokens;
    }

    private Token ReadNextToken()
    {
        var start = CurrentLocation();
        char c = Peek();

        if (char.IsWhiteSpace(c))
        {
            if (c == '\n')
            {
                _position++;
                _line++;
                _column = 1;
                return new Token(TokenKind.NewLine, "\n", start);
            }
            SkipWhitespace();
            return new Token(TokenKind.Whitespace, " ", start);
        }

        if (c == '/' && Peek(1) == '/')
        {
            SkipLineComment();
            return new Token(TokenKind.Comment, "", start);
        }

        if (c == '/' && Peek(1) == '*')
        {
            SkipBlockComment();
            return new Token(TokenKind.Comment, "", start);
        }

        if (c == '"' || c == '\'' || c == '`')
            return ReadString(start);

        if (c == '0' && Peek(1) == 'x')
            return ReadHexNumber(start);

        if (char.IsDigit(c))
            return ReadNumber(start);

        if (char.IsLetter(c) || c == '_' || c == '@')
            return ReadIdentifier(start);

        return ReadOperator(start);
    }

    private Token ReadNumber(SourceLocation start)
    {
        var sb = new System.Text.StringBuilder();
        bool isFloat = false;

        while (_position < _source.Length && char.IsDigit(Peek()))
        {
            sb.Append(Peek());
            _position++;
        }

        if (Peek() == '.' && char.IsDigit(Peek(1)))
        {
            isFloat = true;
            sb.Append(Peek());
            _position++;
            while (_position < _source.Length && char.IsDigit(Peek()))
            {
                sb.Append(Peek());
                _position++;
            }
        }

        if (Peek() == 'e' || Peek() == 'E')
        {
            isFloat = true;
            sb.Append(Peek());
            _position++;
            if (Peek() == '+' || Peek() == '-')
            {
                sb.Append(Peek());
                _position++;
            }
            while (_position < _source.Length && char.IsDigit(Peek()))
            {
                sb.Append(Peek());
                _position++;
            }
        }

        string text = sb.ToString();
        _column += text.Length;

        object value;
        if (isFloat)
        {
            value = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            long parsed = long.Parse(text);
            value = parsed;
        }

        var tok = new Token(isFloat ? TokenKind.FloatLiteral : TokenKind.IntegerLiteral, text, start, value);
        return tok;
    }

    private Token ReadHexNumber(SourceLocation start)
    {
        _position += 2;
        var sb = new System.Text.StringBuilder("0x");
        while (_position < _source.Length && IsHexDigit(Peek()))
        {
            sb.Append(Peek());
            _position++;
        }
        string text = sb.ToString();
        _column += text.Length;
        long value = Convert.ToInt64(text, 16);
        return new Token(TokenKind.IntegerLiteral, text, start, value);
    }

    private static bool IsHexDigit(char c) =>
        char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private Token ReadString(SourceLocation start)
    {
        char quote = Peek();
        _position++;
        _column++;
        var sb = new System.Text.StringBuilder();

        while (_position < _source.Length && Peek() != quote)
        {
            if (Peek() == '\\')
            {
                _position++;
                _column++;
                sb.Append(Peek() switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '\'' => '\'',
                    '"' => '"',
                    '`' => '`',
                    '0' => '\0',
                    _ => Peek()
                });
            }
            else
            {
                sb.Append(Peek());
            }
            _position++;
            _column++;
        }

        if (_position < _source.Length)
        {
            _position++;
            _column++;
        }

        return new Token(TokenKind.StringLiteral, sb.ToString(), start, sb.ToString());
    }

    private Token ReadIdentifier(SourceLocation start)
    {
        var sb = new System.Text.StringBuilder();
        bool isAt = Peek() == '@';
        if (isAt)
        {
            sb.Append(Peek());
            _position++;
            _column++;
        }

        while (_position < _source.Length && (char.IsLetterOrDigit(Peek()) || Peek() == '_'))
        {
            sb.Append(Peek());
            _position++;
            _column++;
        }

        string text = sb.ToString();

        if (!isAt && Keywords.TryGetValue(text, out var keyword))
            return new Token(keyword, text, start);

        return new Token(TokenKind.Identifier, text, start);
    }

    private Token ReadOperator(SourceLocation start)
    {
        char c = Peek();
        _position++;
        _column++;

        return c switch
        {
            '(' => new Token(TokenKind.OpenParen, "(", start),
            ')' => new Token(TokenKind.CloseParen, ")", start),
            '{' => new Token(TokenKind.OpenBrace, "{", start),
            '}' => new Token(TokenKind.CloseBrace, "}", start),
            '[' => new Token(TokenKind.OpenBracket, "[", start),
            ']' => new Token(TokenKind.CloseBracket, "]", start),
            ';' => new Token(TokenKind.Semicolon, ";", start),
            ':' => new Token(TokenKind.Colon, ":", start),
            ',' => new Token(TokenKind.Comma, ",", start),
            '.' => new Token(TokenKind.Dot, ".", start),
            '~' => new Token(TokenKind.Tilde, "~", start),
            '?' => Peek() switch
            {
                '.' => AdvanceAnd(TokenKind.QuestionDot, "?."),
                '?' => AdvanceAnd(TokenKind.QuestionQuestion, "??"),
                _ => new Token(TokenKind.Question, "?", start)
            },
            '+' => Peek() switch
            {
                '+' => AdvanceAnd(TokenKind.PlusPlus, "++"),
                '=' => AdvanceAnd(TokenKind.PlusEquals, "+="),
                _ => new Token(TokenKind.Plus, "+", start)
            },
            '-' => Peek() switch
            {
                '>' => AdvanceAnd(TokenKind.Arrow, "->"),
                '-' => AdvanceAnd(TokenKind.MinusMinus, "--"),
                '=' => AdvanceAnd(TokenKind.MinusEquals, "-="),
                _ => new Token(TokenKind.Minus, "-", start)
            },
            '*' => Peek() switch
            {
                '=' => AdvanceAnd(TokenKind.StarEquals, "*="),
                _ => new Token(TokenKind.Star, "*", start)
            },
            '/' => Peek() switch
            {
                '=' => AdvanceAnd(TokenKind.SlashEquals, "/="),
                _ => new Token(TokenKind.Slash, "/", start)
            },
            '%' => Peek() switch
            {
                '=' => AdvanceAnd(TokenKind.PercentEquals, "%="),
                _ => new Token(TokenKind.Percent, "%", start)
            },
            '&' => Peek() switch
            {
                '&' => AdvanceAnd(TokenKind.AmpersandAmpersand, "&&"),
                '=' => AdvanceAnd(TokenKind.AmpersandEquals, "&="),
                _ => new Token(TokenKind.Ampersand, "&", start)
            },
            '|' => Peek() switch
            {
                '|' => AdvanceAnd(TokenKind.PipePipe, "||"),
                '=' => AdvanceAnd(TokenKind.PipeEquals, "|="),
                _ => new Token(TokenKind.Pipe, "|", start)
            },
            '^' => Peek() switch
            {
                '=' => AdvanceAnd(TokenKind.CaretEquals, "^="),
                _ => new Token(TokenKind.Caret, "^", start)
            },
            '=' => Peek() switch
            {
                '=' => Peek(1) switch
                {
                    '=' => AdvanceAnd(TripleEqualsAnd(), "==="),
                    _ => AdvanceAnd(TokenKind.DoubleEquals, "==")
                },
                '>' => AdvanceAnd(TokenKind.FatArrow, "=>"),
                _ => new Token(TokenKind.Equals, "=", start)
            },
            '!' => Peek() switch
            {
                '=' => Peek(1) switch
                {
                    '=' => AdvanceAnd(TokenKind.StrictNotEquals, "!=="),
                    _ => AdvanceAnd(TokenKind.NotEquals, "!=")
                },
                _ => new Token(TokenKind.Bang, "!", start)
            },
            '<' => Peek() switch
            {
                '=' => AdvanceAnd(TokenKind.LessOrEqual, "<="),
                '<' => AdvanceAnd(TokenKind.ShiftLeft, "<<"),
                _ => new Token(TokenKind.LessThan, "<", start)
            },
            '>' => Peek() switch
            {
                '=' => AdvanceAnd(TokenKind.GreaterOrEqual, ">="),
                '>' => Peek(1) switch
                {
                    '>' => AdvanceAnd(TokenKind.ShiftRightUnsigned, ">>>"),
                    _ => AdvanceAnd(TokenKind.ShiftRight, ">>")
                },
                _ => new Token(TokenKind.GreaterThan, ">", start)
            },
            _ => new Token(TokenKind.Error, c.ToString(), start)
        };
    }

    private TokenKind TripleEqualsAnd() => TokenKind.TripleEquals;

    private Token AdvanceAnd(TokenKind kind, string text)
    {
        _position += text.Length - 1;
        _column += text.Length - 1;
        return new Token(kind, text, new SourceLocation(_fileName, _line, _column - text.Length, _position - text.Length));
    }

    private void SkipWhitespace()
    {
        while (_position < _source.Length && char.IsWhiteSpace(Peek()) && Peek() != '\n')
        {
            _position++;
            _column++;
        }
    }

    private void SkipLineComment()
    {
        while (_position < _source.Length && Peek() != '\n')
        {
            _position++;
            _column++;
        }
    }

    private void SkipBlockComment()
    {
        _position += 2;
        _column += 2;
        while (_position < _source.Length - 1)
        {
            if (Peek() == '*' && Peek(1) == '/')
            {
                _position += 2;
                _column += 2;
                return;
            }
            if (Peek() == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
            _position++;
        }
    }

    private char Peek(int offset = 0)
    {
        int idx = _position + offset;
        return idx < _source.Length ? _source[idx] : '\0';
    }

    private SourceLocation CurrentLocation() =>
        new(_fileName, _line, _column, _position);
}
