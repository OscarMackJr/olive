using System.Globalization;

namespace Olive.Domain;

// D-05 / RESEARCH assumption A1: a JSON number token is "lossless" when its significant decimal
// digits and decimal exponent equal the shortest round-trip text of the double it parses to.
// It is the only reading under which 0.1, 1.0 and 1e0 are accepted while 9007199254740993 is not.
public static class JsonNumbers
{
    public static bool IsLossless(string rawToken)
    {
        if (!double.TryParse(rawToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
        {
            return false;
        }

        return Decompose(rawToken) == Decompose(d.ToString("R", CultureInfo.InvariantCulture));
    }

    // Returns (digits, n) meaning value = 0.DIGITS x 10^n; zero is ("", 0).
    internal static (string Digits, int N) Decompose(string token)
    {
        var s = token.TrimStart('-', '+');
        var exp = 0;
        var e = s.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            exp = int.Parse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            s = s[..e];
        }

        var dot = s.IndexOf('.');
        var intPart = dot >= 0 ? s[..dot] : s;
        var fracPart = dot >= 0 ? s[(dot + 1)..] : "";
        var digits = intPart + fracPart;
        var n = intPart.Length + exp;
        var lead = 0;
        while (lead < digits.Length && digits[lead] == '0')
        {
            lead++;
        }

        digits = digits[lead..];
        n -= lead;
        digits = digits.TrimEnd('0');
        return digits.Length == 0 ? ("", 0) : (digits, n);
    }
}
