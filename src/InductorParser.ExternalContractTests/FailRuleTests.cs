// Why this project lives outside InductorParser.Tests
//
// This assembly is deliberately NOT named in any InternalsVisibleTo
// grant in src/InductorParser/InductorParser.csproj. With IVT in
// place, C# treats the holder of the grant as "same assembly" for
// override-matching purposes: a `protected internal abstract` base
// in InductorParser can only be overridden with `protected internal
// override` from inside InductorParser.Tests, and that modifier
// would also accept an `internal abstract` base. Narrowing the
// modifier would silently not be caught.
//
// Verified empirically: in InductorParser.Tests, declaring the
// override as `protected override` fails with CS0507 because the
// override can't change the access modifier from the base's
// `protected internal`. C# wants `protected internal override`
// there. The only way to write an override the way a real external
// consumer would is from an assembly with no IVT grant. This is
// that assembly.
//
// The check is primarily a compile-time concern: if any surface
// the consumer rules touch is narrowed, FailRule.cs / OneRuneRule.cs
// / RepeatRule.cs fail to build and `dotnet build` fails. The
// smoke tests below are a thin runtime check that the rule's
// TryParseRule override actually fires at parse time.

using NUnit.Framework;

namespace InductorParser.ExternalContractTests;

[TestFixture]
public class FailRuleTests
{
    [Test]
    public void FailRule_fires_at_parse_time_and_records_its_message()
    {
        var result = new FailRule("nope").Parse("anything");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("nope"));
    }
}
