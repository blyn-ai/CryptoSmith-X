using System.Reflection;
using System.Text.RegularExpressions;
using CryptoSmithX.WebApp.Admin.Data;

namespace CryptoSmithX.WebApp.Admin.Tests;

/// <summary>
/// Dapper materialises a record POSITIONALLY, not by name. A query whose aliases carry exactly the
/// right names in the wrong order does not warn and does not mis-map — it throws
/// <c>InvalidOperationException: a parameterless default constructor or one matching signature ...
/// is required</c>, at request time, on the first row, in production.
///
/// That is not hypothetical. Adding one count to the dashboard put <c>CollectedInstruments</c> after
/// <c>TradingInstruments</c> in the SQL and after <c>KnownInstruments</c> in the record. Every unit
/// test passed — none of them touch a database, so none of them materialise anything — and the admin
/// dashboard answered a blank page until the exception was read.
///
/// This test needs no database. It pairs each SQL constant with the record whose constructor takes
/// exactly its set of aliases, then asserts the two agree on ORDER as well as membership. The
/// pairing is by parameter set rather than by a hand-written table so that a query added later is
/// covered without anyone remembering to add it here.
/// </summary>
public sealed class QueryShapeTests
{
    private static readonly Regex Alias = new("as\\s+\"(?<name>[A-Za-z0-9_]+)\"", RegexOptions.Compiled);

    public static TheoryData<string> SqlConstants()
    {
        var data = new TheoryData<string>();
        foreach (var f in SqlFields())
        {
            data.Add(f.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SqlConstants))]
    public void A_querys_aliases_are_in_the_same_order_as_the_record_it_materialises(string fieldName)
    {
        var field = SqlFields().Single(f => f.Name == fieldName);
        var sql = (string)field.GetRawConstantValue()!;
        var aliases = Alias.Matches(sql).Select(m => m.Groups["name"].Value).ToList();

        // Fewer than two aliases cannot be out of order, and a query with none maps to a scalar.
        if (aliases.Count < 2)
        {
            return;
        }

        var target = Candidates()
            .Select(t => (Type: t, Ctor: Primary(t)))
            .Where(x => x.Ctor is not null)
            .FirstOrDefault(x => x.Ctor!.GetParameters().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(aliases));

        // No record claims this shape: the query feeds a tuple, a scalar, or a hand-built object.
        if (target.Ctor is null)
        {
            return;
        }

        var expected = target.Ctor.GetParameters().Select(p => p.Name).ToList();
        Assert.True(
            expected.SequenceEqual(aliases, StringComparer.Ordinal),
            $"{fieldName} maps to {target.Type.Name}, which Dapper fills positionally.\n"
            + $"  record: {string.Join(", ", expected)}\n"
            + $"  query : {string.Join(", ", aliases)}");
    }

    /// <summary>
    /// Health answers "how stale is the data WE are responsible for", and a row has to clear two
    /// different owners to count. Dropping either half is a permanent false alarm, and both have
    /// been shipped: scoped by the venue's <c>status</c> alone, hyperliquid read degraded at a
    /// measured 9.0 h while the 25 instruments we collect were 11 s behind; scoped by
    /// <c>collect</c> alone, kraken reads degraded forever over one contract the venue delisted,
    /// which no collector can ever refresh again.
    ///
    /// Asserted on the SQL rather than through a database because that is where the decision lives.
    /// The counts beside it are still allowed to speak for the venue — that column is labelled as
    /// the venue's — so this names the one subquery instead of banning a phrase from the file.
    /// </summary>
    [Fact]
    public void The_worst_age_behind_the_health_rule_takes_both_owners_into_account()
    {
        var sql = (string)SqlFields().Single(f => f.Name == "ExSql").GetRawConstantValue()!;
        var lines = sql.Split('\n');

        var subquery = lines
            .Select((text, index) => (text, index))
            .First(l => l.text.Contains("\"WorstAgeSeconds\"", StringComparison.Ordinal));

        // The subquery spans the three lines ending at the alias.
        var body = string.Join(" ", lines[(subquery.index - 2)..(subquery.index + 1)]);

        Assert.Contains("i.collect", body, StringComparison.Ordinal);
        Assert.Contains("i.status = 'trading'", body, StringComparison.Ordinal);
    }

    private static IEnumerable<FieldInfo> SqlFields() =>
        typeof(DashboardStore).Assembly.GetTypes()
            .Where(t => t.Namespace == "CryptoSmithX.WebApp.Admin.Data")
            .SelectMany(t => t.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Sql", StringComparison.Ordinal))
            .OrderBy(f => f.DeclaringType!.Name + "." + f.Name, StringComparer.Ordinal);

    private static IEnumerable<Type> Candidates() =>
        typeof(DashboardStore).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract);

    /// <summary>The constructor a positional record declares — the one with the most parameters.</summary>
    private static ConstructorInfo? Primary(Type t) =>
        t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length > 0)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
}
