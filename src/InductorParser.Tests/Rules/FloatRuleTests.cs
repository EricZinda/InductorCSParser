using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for Rules.Float(). The grammar is "[+-]?\d+\.\d+":
// an optional leading + or - (matching Integer()'s sign handling),
// one or more digits, a literal '.', and one or more digits.
//
// Float() used to compose Integer() for both the integer and
// fractional parts, and Integer() carries its own optional leading
// + or -. That made Float silently accept extra signs in the
// fractional position and stacked at the front, which a grammar
// using Float as a numeric literal isn't expecting. See backlog
// fl0a for the full write-up. The fix inlines digit-only runs and
// keeps the leading sign at the front only.
[TestFixture]
public class FloatRuleTests
{
    [Test]
    public void Matches_unsigned_decimal()
    {
        var result = Float().Parse("3.14");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Matches_negative_decimal()
    {
        var result = Float().Parse("-3.14");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Matches_positive_decimal_with_leading_plus()
    {
        // Symmetry with Integer(), which already accepts a leading
        // '+'. A grammar that lets the user write "+5" should also
        // accept "+5.5" without surprise.
        var result = Float().Parse("+3.14");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Rejects_double_leading_minus()
    {
        // The grammar is "an optional leading + or -" (at most one).
        // Two minus signs in a row aren't a decimal.
        var result = Float().Parse("--3.14");
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Rejects_mixed_double_leading_signs()
    {
        // Two leading sign characters of any combination aren't a
        // decimal.
        Assert.That(Float().Parse("+-3.14").Success, Is.False);
        Assert.That(Float().Parse("-+3.14").Success, Is.False);
        Assert.That(Float().Parse("++3.14").Success, Is.False);
    }

    [Test]
    public void Rejects_sign_inside_fractional_part()
    {
        // The fractional part is "one or more digits" with no sign.
        // A '+' or '-' between the dot and the digits isn't a
        // decimal.
        Assert.That(Float().Parse("3.+14").Success, Is.False);
        Assert.That(Float().Parse("3.-14").Success, Is.False);
    }
}
