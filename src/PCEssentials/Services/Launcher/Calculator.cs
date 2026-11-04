using System.Globalization;

namespace PCEssentials.Services.Launcher;

/// Evaluates what is typed into the launcher as arithmetic: + - * / % ^, parentheses, implied
/// multiplication ("2pi", "3(4+1)"), the constants pi and e, and sqrt, cbrt, abs, round, floor, ceil, ln,
/// log (base 10), log2, exp, sin, cos, tan, asin, acos, atan (radians), min and max. "x" and "×" multiply,
/// "÷" divides, and a comma or underscore between digits is a thousands separator.
public static class Calculator
{
    /// True only when the text is clearly maths, so a plain app name ("e", "pi", "max") never turns into a
    /// result: there must be an operator or a function call, and the whole text must parse.
    public static bool TryEvaluate(string text, out double value)
    {
        value = 0;
        text = text.Trim();
        if (text.StartsWith('=')) text = text[1..];
        if (text.Length == 0) return false;
        var looksLikeMaths = text.IndexOfAny(['+', '-', '*', '/', '^', '%', '(', '×', '÷']) >= 0
            || System.Text.RegularExpressions.Regex.IsMatch(text, @"\d\s*x\s*\d")
            // "2pi": a number followed by a constant. A bare number is not worth a result.
            || (char.IsDigit(text[0]) && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        if (!looksLikeMaths) return false;
        try
        {
            var p = new Parser(text);
            value = p.ParseAll();
            return !double.IsNaN(value);
        }
        catch (FormatException) { return false; }
    }

    /// Up to 12 significant digits, without float noise like 0.30000000000000004.
    public static string Format(double v)
    {
        if (double.IsPositiveInfinity(v)) return "∞";
        if (double.IsNegativeInfinity(v)) return "-∞";
        var r = Math.Round(v, 10);
        if (Math.Abs(r) >= 1e15 || (r != 0 && Math.Abs(r) < 1e-6)) return v.ToString("G12", CultureInfo.InvariantCulture);
        return r.ToString("#,0.##########", CultureInfo.InvariantCulture);
    }

    private sealed class Parser(string s)
    {
        private int _i;

        public double ParseAll()
        {
            var v = Sum();
            Skip();
            if (_i < s.Length) throw new FormatException();
            return v;
        }

        private void Skip() { while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++; }

        private bool Eat(char c)
        {
            Skip();
            if (_i < s.Length && s[_i] == c) { _i++; return true; }
            return false;
        }

        private double Sum()
        {
            var v = Product();
            while (true)
            {
                if (Eat('+')) v += Product();
                else if (Eat('-')) v -= Product();
                else return v;
            }
        }

        private double Product()
        {
            var v = Unary();
            while (true)
            {
                Skip();
                if (Eat('*') || Eat('×') || EatTimesX()) v *= Unary();
                else if (Eat('/') || Eat('÷')) v /= Unary();
                else if (Eat('%')) v %= Unary();
                // Implied multiplication: "2pi", "3(1+2)", "(1+2)(3+4)".
                else if (_i < s.Length && (s[_i] == '(' || char.IsLetter(s[_i]))) v *= Unary();
                else return v;
            }
        }

        /// "x" is multiplication only between operands ("3 x 4"), never the start of a name like "exp".
        private bool EatTimesX()
        {
            Skip();
            if (_i < s.Length && s[_i] is 'x' or 'X' && (_i + 1 >= s.Length || !char.IsLetter(s[_i + 1]))) { _i++; return true; }
            return false;
        }

        private double Unary()
        {
            if (Eat('-')) return -Unary();
            if (Eat('+')) return Unary();
            return Power();
        }

        private double Power()
        {
            var b = Atom();
            // Right-associative, and binds tighter than a leading minus: -2^2 = -4.
            if (Eat('^')) return Math.Pow(b, Unary());
            return b;
        }

        private double Atom()
        {
            Skip();
            if (_i >= s.Length) throw new FormatException();
            if (Eat('('))
            {
                var v = Sum();
                if (!Eat(')')) throw new FormatException();
                return v;
            }
            var c = s[_i];
            if (char.IsDigit(c) || c == '.') return Number();
            if (char.IsLetter(c)) return Name();
            throw new FormatException();
        }

        private double Number()
        {
            var start = _i;
            while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.' || (_i > start && IsGroupSeparator(_i)))) _i++;
            // Scientific notation: 1e6, 2.5E-3.
            if (_i < s.Length && s[_i] is 'e' or 'E' && _i + 1 < s.Length &&
                (char.IsDigit(s[_i + 1]) || (s[_i + 1] is '-' or '+' && _i + 2 < s.Length && char.IsDigit(s[_i + 2]))))
            {
                _i += 2;
                while (_i < s.Length && char.IsDigit(s[_i])) _i++;
            }
            var text = s[start.._i].Replace(",", "").Replace("_", "");
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new FormatException();
            return v;
        }

        /// "_" between digits, or "," followed by exactly three digits ("1,234"), groups thousands. Any other
        /// comma separates function arguments: max(1,2).
        private bool IsGroupSeparator(int i)
        {
            if (s[i] == '_') return i + 1 < s.Length && char.IsDigit(s[i + 1]);
            if (s[i] != ',') return false;
            var digits = 0;
            while (i + 1 + digits < s.Length && char.IsDigit(s[i + 1 + digits])) digits++;
            return digits == 3;
        }

        private double Name()
        {
            var start = _i;
            while (_i < s.Length && char.IsLetterOrDigit(s[_i])) _i++;
            var name = s[start.._i].ToLowerInvariant();
            switch (name)
            {
                case "pi" or "π": return Math.PI;
                case "e": return Math.E;
            }
            if (!Eat('(')) throw new FormatException();
            var args = new List<double> { Sum() };
            while (Eat(',')) args.Add(Sum());
            if (!Eat(')')) throw new FormatException();
            double One() => args.Count == 1 ? args[0] : throw new FormatException();
            return name switch
            {
                "sqrt" => Math.Sqrt(One()),
                "cbrt" => Math.Cbrt(One()),
                "abs" => Math.Abs(One()),
                "round" => Math.Round(One(), MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(One()),
                "ceil" => Math.Ceiling(One()),
                "ln" => Math.Log(One()),
                "log" => Math.Log10(One()),
                "log2" => Math.Log2(One()),
                "exp" => Math.Exp(One()),
                "sin" => Math.Sin(One()),
                "cos" => Math.Cos(One()),
                "tan" => Math.Tan(One()),
                "asin" => Math.Asin(One()),
                "acos" => Math.Acos(One()),
                "atan" => Math.Atan(One()),
                "min" => args.Min(),
                "max" => args.Max(),
                _ => throw new FormatException(),
            };
        }
    }
}
