using System.Reflection;
using CryptoSmithX.MarketData.Connectors;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Every dataset an adapter DECLARES must have a real body behind it.
///
/// <b>The bug this exists to prevent.</b> Most of <see cref="IExchangeMarketData"/>'s optional
/// members are DEFAULT INTERFACE METHODS returning empty. A default is matched by name and full
/// signature, so an adapter that writes the method with one extra parameter — say an
/// <c>intervalSeconds</c> that used to be there — compiles cleanly, reads correctly, is covered by
/// its own unit tests through the concrete type, and is NEVER CALLED. The interface default answers
/// instead, the collector stores nothing, and the venue's column stays empty while the capability
/// row says the data is being collected. Three adapters shipped in exactly that state.
///
/// Nothing about that is visible at a call site, so it is checked here structurally: for each
/// declared dataset, the interface map must point at the adapter's own method rather than back at
/// the interface.
/// </summary>
public sealed class DeclaredDatasetsAreImplementedTests
{
    /// <summary>
    /// Dataset code to the members that can serve it. ANY ONE of them being the adapter's own is
    /// enough — liquidations reach us by three different shapes depending on the transport, and a
    /// polled venue is no less implemented than a streaming one.
    ///
    /// Only members with a DEFAULT are listed. <c>discovery</c>, <c>snapshot</c>, <c>candles</c>,
    /// <c>funding</c> and <c>depth</c> sit on abstract members every adapter must write anyway, so
    /// there is no silent-default trap for them to fall into.
    ///
    /// <c>open_interest</c> is deliberately ABSENT from this map, and not by oversight: WEEX and
    /// Hyperliquid publish no OI series at all, only "OI right now" on the ticker, which the Hub
    /// buckets itself — so on those two the dataset is genuinely served with no history member
    /// written, and a structural rule would call a correct adapter broken. The phantom-signature
    /// trap it would otherwise have caught is caught for every member at once by
    /// <see cref="No_adapter_carries_a_near_miss_of_an_interface_method"/>.
    /// </summary>
    private static readonly Dictionary<string, string[]> Serves = new(StringComparer.Ordinal)
    {
        ["trades"] = ["DrainTrades", "ObserveTrades"],
        ["liquidations"] = ["DrainLiquidations", "ObserveLiquidations", "GetLiquidationVolumeAsync"],
        ["candles_mark"] = ["GetPriceCandles1mAsync"],
        ["candles_index"] = ["GetPriceCandles1mAsync"],
        ["reference_depth"] = ["GetReferenceDepthAsync"],
        ["vault_state"] = ["GetVaultStateAsync"],
        ["vault_pair_state"] = ["GetVaultPairStateAsync"],
    };

    public static TheoryData<string> Adapters()
    {
        var data = new TheoryData<string>();
        foreach (var a in All())
        {
            data.Add(a.SegmentCode);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void Every_dataset_this_adapter_claims_has_its_own_body_behind_it(string segmentCode)
    {
        var adapter = All().Single(a => a.SegmentCode == segmentCode);
        var map = adapter.GetType().GetInterfaceMap(typeof(IExchangeMarketData));

        foreach (var capability in adapter.Capabilities)
        {
            if (!Serves.TryGetValue(capability.DatasetCode, out var candidates))
            {
                continue;
            }

            var implemented = candidates.Any(name => IsOwn(map, name));

            Assert.True(
                implemented,
                $"{segmentCode} declares '{capability.DatasetCode}' but none of "
                + $"{string.Join(", ", candidates)} is implemented on the adapter — the interface's "
                + "own empty default is what the collector would call. The usual cause is a "
                + "signature that does not match the interface exactly, which still compiles.");
        }
    }

    /// <summary>
    /// The trap itself, checked without knowing anything about datasets: a public method NAMED like
    /// an interface member must actually occupy that member's slot.
    ///
    /// This is what a wrong signature looks like from the outside — <c>GetOpenInterestHistoryAsync</c>
    /// present, public, tested, and filling no slot, because one extra parameter made it a different
    /// method that happens to share a name. Nothing else in the build says a word about it: no
    /// warning, no unused-member hint, and the adapter's own tests call it directly and pass.
    /// </summary>
    [Theory]
    [MemberData(nameof(Adapters))]
    public void No_adapter_carries_a_near_miss_of_an_interface_method(string segmentCode)
    {
        var type = All().Single(a => a.SegmentCode == segmentCode).GetType();
        var map = type.GetInterfaceMap(typeof(IExchangeMarketData));

        var names = map.InterfaceMethods.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var slots = map.TargetMethods.ToHashSet();

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!names.Contains(method.Name) || slots.Contains(method))
            {
                continue;
            }

            Assert.Fail(
                $"{segmentCode}.{method.Name} shares a name with an {nameof(IExchangeMarketData)} "
                + "member but fills none of its slots, so the interface's default is what callers "
                + $"reach and this method is dead. Its signature is ({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))}) "
                + "— compare it against the interface, parameter for parameter.");
        }
    }

    [Fact]
    public void The_check_can_tell_an_implemented_member_from_an_inherited_default()
    {
        // Without this, a mapping bug that answered "own" for everything would make the theories
        // above pass on every adapter forever while proving nothing.
        var weex = All().Single(a => a.SegmentCode == "weex-futures");
        var map = weex.GetType().GetInterfaceMap(typeof(IExchangeMarketData));

        // WEEX publishes no open-interest SERIES — only "OI right now" on the ticker — so this
        // member really is the interface's default here, and the check must say so.
        Assert.False(IsOwn(map, "GetOpenInterestHistoryAsync"));

        // And one it does write itself, so the check is not simply answering false to everything.
        Assert.True(IsOwn(map, "GetInstrumentsAsync"));
    }

    /// <summary>True when the interface slot lands on the adapter's own method rather than on the
    /// default declared by the interface itself.</summary>
    private static bool IsOwn(InterfaceMapping map, string interfaceMethodName)
    {
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i].Name != interfaceMethodName)
            {
                continue;
            }

            if (map.TargetMethods[i].DeclaringType != typeof(IExchangeMarketData))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every adapter in the assembly, built with a base URL that is never called: this test reads
    /// declarations and metadata only, and constructors here do no I/O.
    ///
    /// Discovered by reflection rather than listed, so a venue added tomorrow is checked without
    /// anyone remembering to add it — which is the whole point, since the trap only shows up on
    /// adapters written after the interface had defaults.
    /// </summary>
    private static IReadOnlyList<IExchangeMarketData> All()
    {
        var types = typeof(IExchangeMarketData).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && typeof(IExchangeMarketData).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        var built = new List<IExchangeMarketData>();
        foreach (var t in types)
        {
            if (Make(t) is IExchangeMarketData adapter)
            {
                built.Add(adapter);
            }
        }

        // A floor, not a count: adding a venue must never quietly turn this suite into a no-op, and
        // pinning the exact number would only mean editing it beside every new adapter.
        Assert.True(built.Count >= 10, $"only {built.Count} adapters could be constructed");
        return built;
    }

    private static object? Make(Type t)
    {
        // The SHORTEST constructor, deliberately. The longest is usually the test seam — clients
        // take (HttpClient, baseUrl) so a stub handler can be injected — and HttpClient itself only
        // takes an abstract handler, so resolving downward from the widest overload dead-ends.
        var ctor = t.GetConstructors().OrderBy(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null)
        {
            return null;
        }

        var args = new object?[ctor.GetParameters().Length];
        var parameters = ctor.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];

            if (p.ParameterType == typeof(string))
            {
                args[i] = "https://stub.invalid";
            }
            else if (p.ParameterType.IsInterface)
            {
                // A live feed or an open-interest feed. Nothing is read through it here, and a
                // proxy keeps this helper from needing a hand-written stub per venue.
                args[i] = Proxy.For(p.ParameterType);
            }
            else if (p.ParameterType.IsClass && Make(p.ParameterType) is { } nested)
            {
                args[i] = nested;
            }
            else if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
            }
            else
            {
                return null;
            }
        }

        try
        {
            return ctor.Invoke(args);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    /// <summary>
    /// An interface implementation whose methods are never called — only its existence matters, so
    /// that a constructor demanding a feed can be satisfied.
    ///
    /// Not sealed: the generator derives the proxy type FROM this one at runtime, and refuses a
    /// sealed base.
    /// </summary>
    private class Proxy : DispatchProxy
    {
        public static object? For(Type interfaceType) =>
            // Two overloads of Create share the name, so it is picked by shape — two type
            // parameters and none of its own — rather than by name alone.
            typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == nameof(Create)
                             && m.GetGenericArguments().Length == 2
                             && m.GetParameters().Length == 0)
                .MakeGenericMethod(interfaceType, typeof(Proxy))
                .Invoke(null, null);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(
                $"{targetMethod?.Name} was called on a construction-only stub");
    }
}
