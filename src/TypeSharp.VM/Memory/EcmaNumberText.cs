using System.Globalization;
using System.Numerics;
using TypeSharp.VM.Interpreter;

namespace TypeSharp.VM.Memory;

// Pure string Number grammar shared by native conversions; each caller
// still decides which runtime values enter it and under what resource policy.
internal static class EcmaNumberText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static double Parse(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> s = text;
        while (!s.IsEmpty && Builtins.IsEcmaTrimWhitespace(s[0])) s = s[1..];
        while (!s.IsEmpty && Builtins.IsEcmaTrimWhitespace(s[^1])) s = s[..^1];

        if (s.IsEmpty) return 0;
        if (s.SequenceEqual("Infinity") || s.SequenceEqual("+Infinity")) return double.PositiveInfinity;
        if (s.SequenceEqual("-Infinity")) return double.NegativeInfinity;
        if (s.Length >= 2 && s[0] == '0')
        {
            int shift = s[1] switch { 'x' or 'X' => 4, 'b' or 'B' => 1, 'o' or 'O' => 3, _ => 0 };
            if (shift != 0)
                return ParseRadix(s[2..], shift);
        }

        // Validate the complete JS decimal grammar before letting CLR parse it:
        // its parser also recognizes other spellings and trailing NULs.
        int i = s[0] is '+' or '-' ? 1 : 0;
        int digits = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        }
        if (digits == 0) return double.NaN;
        if (i < s.Length && s[i] is 'e' or 'E')
        {
            i++;
            if (i < s.Length && s[i] is '+' or '-') i++;
            int exponent = i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            if (i == exponent) return double.NaN;
        }
        return i == s.Length && double.TryParse(s, NumberStyles.Float, Invariant, out var parsed)
            ? parsed : double.NaN;
    }

    private static double ParseRadix(ReadOnlySpan<char> digits, int shift)
    {
        if (digits.IsEmpty) return double.NaN;
        BigInteger value = BigInteger.Zero;
        bool overflow = false;
        foreach (char ch in digits)
        {
            int digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'f' => ch - 'a' + 10,
                >= 'A' and <= 'F' => ch - 'A' + 10,
                _ => -1
            };
            if (digit < 0 || digit >= (1 << shift)) return double.NaN;
            if (overflow) continue; // Still validate every digit.
            value = (value << shift) + digit;
            overflow = value.GetBitLength() > 1024;
        }
        return overflow ? double.PositiveInfinity : BoundedIntegerToDouble(value);
    }

    // Parse bounded exact decimal text once for correctly rounded binary64.
    internal static double BoundedIntegerToDouble(BigInteger value) =>
        value.GetBitLength() > 1024 ? (value.Sign < 0 ? double.NegativeInfinity : double.PositiveInfinity)
            : double.Parse(value.ToString(Invariant), NumberStyles.Float, Invariant);
}
