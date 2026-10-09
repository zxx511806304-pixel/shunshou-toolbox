using System.Globalization;

namespace Shunshou.Core;

/// <summary>
/// Evaluates plain arithmetic typed into the quick launcher:
/// + - * / % ^ with parentheses and decimals; × ÷ （） and thousands separators are accepted.
/// </summary>
public static class QuickCalculateService
{
    /// <summary>True only when the whole text is one complete, finite arithmetic expression.</summary>
    public static bool TryEvaluate(string? text, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 120 || !LooksLikeExpression(text)) return false;
        var parser = new Parser(text.Replace('×', '*').Replace('÷', '/').Replace('（', '(').Replace('）', ')')
            .Replace(" ", "").Replace(",", ""));
        double value;
        try
        {
            value = parser.ParseExpression();
        }
        catch (FormatException)
        {
            return false;
        }
        if (!parser.AtEnd || double.IsNaN(value) || double.IsInfinity(value)) return false;
        result = value;
        return true;
    }

    /// <summary>Compact invariant display without trailing zeros, for the result row and the clipboard.</summary>
    public static string Format(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>Requires at least one digit before an operator, so plain numbers and prose never match.</summary>
    private static bool LooksLikeExpression(string text)
    {
        var digitSeen = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsDigit(c)) digitSeen = true;
            else if (c is '+' or '*' or '/' or '%' or '^' or '×' or '÷') { if (digitSeen) return true; }
            else if (c == '-' && i > 0 && digitSeen) return true;
        }
        return false;
    }

    private sealed class Parser(string text)
    {
        private int _index;
        internal bool AtEnd => _index >= text.Length;

        internal double ParseExpression()
        {
            double value = ParseTerm();
            while (true)
            {
                if (Consume('+')) value += ParseTerm();
                else if (Consume('-')) value -= ParseTerm();
                else return value;
            }
        }

        private double ParseTerm()
        {
            double value = ParsePower();
            while (true)
            {
                if (Consume('*')) value *= ParsePower();
                else if (Consume('/')) value /= ParsePower();
                else if (Consume('%')) value %= ParsePower();
                else return value;
            }
        }

        private double ParsePower()
        {
            double value = ParseUnary();
            // Right-associative: 2^3^2 is 2^(3^2).
            return Consume('^') ? Math.Pow(value, ParsePower()) : value;
        }

        private double ParseUnary()
        {
            if (Consume('+')) return ParseUnary();
            if (Consume('-')) return -ParseUnary();
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            if (Consume('('))
            {
                double value = ParseExpression();
                if (!Consume(')')) throw new FormatException("缺少右括号");
                return value;
            }
            int start = _index;
            while (_index < text.Length && (char.IsDigit(text[_index]) || text[_index] == '.')) _index++;
            if (start == _index ||
                !double.TryParse(text.AsSpan(start, _index - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number))
                throw new FormatException("需要数字");
            return number;
        }

        private bool Consume(char c)
        {
            if (_index >= text.Length || text[_index] != c) return false;
            _index++;
            return true;
        }
    }
}
