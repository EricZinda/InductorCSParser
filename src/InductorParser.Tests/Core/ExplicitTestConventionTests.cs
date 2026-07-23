using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace InductorParser.Tests;

// ./test.sh --all runs every [Explicit] test, and it finds them by
// category filter (UnicodeConformance, DeepCampaign,
// RequiresNetwork), because NUnit only runs an [Explicit] test when
// the filter picks it out by name or category. That arrangement
// breaks silently if someone adds an [Explicit] test without a
// category the script knows: the new test would run in no
// configuration at all and nobody would notice.
//
// This fixture closes that hole. It reflects over every test in the
// assembly and fails the everyday suite the moment an [Explicit]
// test appears outside the known categories. The category also says
// why a test is opt-in: RequiresNetwork tests download UCD files
// from unicode.org, DeepCampaign tests run for minutes. Adding a new
// category means updating test.sh and the list here together.
[TestFixture]
public class ExplicitTestConventionTests
{
    // The categories test.sh --all selects.
    private static readonly string[] KnownExplicitCategories =
    {
        "UnicodeConformance",
        "DeepCampaign",
        "RequiresNetwork",
    };

    [Test]
    public void Every_explicit_test_has_a_category_the_test_script_knows()
    {
        var offenders = new SortedSet<string>();

        foreach (Type type in typeof(ExplicitTestConventionTests).Assembly.GetTypes())
        {
            bool fixtureIsExplicit = type.IsDefined(typeof(ExplicitAttribute), inherit: true);
            string[] fixtureCategories = CategoriesOf(type);

            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                bool isTest = method.IsDefined(typeof(TestAttribute), inherit: true)
                    || method.IsDefined(typeof(TestCaseAttribute), inherit: true)
                    || method.IsDefined(typeof(TestCaseSourceAttribute), inherit: true);
                if (!isTest) continue;

                bool isExplicit = fixtureIsExplicit
                    || method.IsDefined(typeof(ExplicitAttribute), inherit: true);
                if (!isExplicit) continue;

                bool hasKnownCategory = fixtureCategories
                    .Concat(CategoriesOf(method))
                    .Any(category => KnownExplicitCategories.Contains(category));
                if (!hasKnownCategory) offenders.Add($"{type.Name}.{method.Name}");
            }
        }

        Assert.That(offenders, Is.Empty,
            "Every [Explicit] test needs a [Category] from the known set "
            + $"({string.Join(", ", KnownExplicitCategories)}) so ./test.sh --all "
            + "reaches it. Tag the tests above, or update test.sh and this "
            + "list together if a new category is needed.");
    }

    private static string[] CategoriesOf(MemberInfo member) =>
        member.GetCustomAttributes<CategoryAttribute>(inherit: true)
            .Select(attribute => attribute.Name)
            .ToArray();
}
