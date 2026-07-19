using System.Globalization;
using System.Numerics;

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
        ["boolean"] = TokenKind.BoolKeyword,
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
        ["extends"] = TokenKind.ExtendsKeyword,
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
        ["break"] = TokenKind.BreakKeyword,
        ["continue"] = TokenKind.ContinueKeyword,
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

        if (c == '`')
            return ReadTemplateLiteral(start);

        if (c == '"' || c == '\'')
            return ReadString(start);

        if (c == '0' && IsIntegerBasePrefix(Peek(1)))
            return ReadPrefixedInteger(start);

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

        if (!isFloat && Peek() == 'n')
        {
            _position++;
            _column++;
            var bigintValue = BigInteger.Parse(text, CultureInfo.InvariantCulture);
            return new Token(TokenKind.IntegerLiteral, text + "n", start, bigintValue);
        }

        if (Peek() == 'u' && Peek(1) == '6' && Peek(2) == '4')
        {
            _position += 3;
            _column += 3;
            string u64Text = text;
            ulong u64Value = ulong.Parse(u64Text);
            var tok64 = new Token(TokenKind.IntegerLiteral, u64Text + "u64", start, u64Value);
            return tok64;
        }

        if (Peek() == 'i' && Peek(1) == '6' && Peek(2) == '4')
        {
            _position += 3;
            _column += 3;
            string i64Text = text;
            long i64Value = long.Parse(i64Text);
            var tokI64 = new Token(TokenKind.IntegerLiteral, i64Text + "i64", start, i64Value);
            return tokI64;
        }

        if (Peek() == 'm')
        {
            _position++;
            _column++;
            decimal decValue = decimal.Parse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture);
            return new Token(TokenKind.FloatLiteral, text + "m", start, decValue);
        }

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

    private Token ReadPrefixedInteger(SourceLocation start)
    {
        char prefix = Peek(1);
        int radix = char.ToLowerInvariant(prefix) switch
        {
            'x' => 16,
            'b' => 2,
            'o' => 8,
            _ => throw new InvalidOperationException($"Unsupported integer prefix '{prefix}'")
        };

        _position += 2;
        var sb = new System.Text.StringBuilder();
        sb.Append('0');
        sb.Append(prefix);

        var digits = new System.Text.StringBuilder();
        while (_position < _source.Length)
        {
            char digit = Peek();
            if (digit == '_')
            {
                sb.Append(digit);
                _position++;
                continue;
            }

            if (!IsDigitForRadix(digit, radix))
            {
                break;
            }

            sb.Append(digit);
            digits.Append(digit);
            _position++;
        }

        if (digits.Length == 0)
        {
            return new Token(TokenKind.IntegerLiteral, sb.ToString(), start, 0L);
        }

        bool isBigInt = Peek() == 'n';
        if (isBigInt)
        {
            sb.Append('n');
            _position++;
        }

        var text = sb.ToString();
        _column += text.Length;
        var parsed = ParseIntegerDigits(digits.ToString(), radix);
        if (isBigInt)
        {
            return new Token(TokenKind.IntegerLiteral, text, start, parsed);
        }

        return new Token(TokenKind.IntegerLiteral, text, start, (long)parsed);
    }

    private static bool IsIntegerBasePrefix(char c) =>
        c is 'x' or 'X' or 'b' or 'B' or 'o' or 'O';

    private static bool IsDigitForRadix(char c, int radix)
    {
        int value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1
        };

        return value >= 0 && value < radix;
    }

    private static BigInteger ParseIntegerDigits(string digits, int radix)
    {
        BigInteger value = BigInteger.Zero;
        foreach (var digit in digits)
        {
            value *= radix;
            value += digit switch
            {
                >= '0' and <= '9' => digit - '0',
                >= 'a' and <= 'f' => digit - 'a' + 10,
                >= 'A' and <= 'F' => digit - 'A' + 10,
                _ => 0
            };
        }

        return value;
    }

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

    // Backtick template: the raw inner text (escapes resolved, `${expr}`
    // placeholders preserved verbatim) travels in one token; the parser
    // splits and sub-parses the interpolations.
    private Token ReadTemplateLiteral(SourceLocation start)
    {
        _position++;
        _column++;
        var sb = new System.Text.StringBuilder();

        while (_position < _source.Length && Peek() != '`')
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
                    '`' => '`',
                    '$' => '$',
                    _ => Peek()
                });
                _position++;
                _column++;
                continue;
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
            sb.Append(Peek());
            _position++;
        }

        if (_position < _source.Length)
        {
            _position++;
            _column++;
        }

        return new Token(TokenKind.TemplateLiteral, sb.ToString(), start, sb.ToString());
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
                '*' => Peek(1) switch
                {
                    '=' => AdvanceAnd(TokenKind.StarStarEquals, "**="),
                    _ => AdvanceAnd(TokenKind.StarStar, "**")
                },
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
                '<' => Peek(1) switch
                {
                    '=' => AdvanceAnd(TokenKind.ShiftLeftEquals, "<<="),
                    _ => AdvanceAnd(TokenKind.ShiftLeft, "<<")
                },
                _ => new Token(TokenKind.LessThan, "<", start)
            },
            '>' => Peek() switch
            {
                '=' => AdvanceAnd(TokenKind.GreaterOrEqual, ">="),
                '>' => Peek(1) switch
                {
                    '>' => AdvanceAnd(TokenKind.ShiftRightUnsigned, ">>>"),
                    '=' => AdvanceAnd(TokenKind.ShiftRightEquals, ">>="),
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
