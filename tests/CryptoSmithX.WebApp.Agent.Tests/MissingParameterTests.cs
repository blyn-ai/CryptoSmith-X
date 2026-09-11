using System.Text.Json.Nodes;
using CryptoSmithX.WebApp.Agent.Data;

namespace CryptoSmithX.WebApp.Agent.Tests;

/// <summary>
/// A profile that does not carry a key is a state, not a crash.
///
/// It shipped as a crash: <c>Read</c> threw an <see cref="InvalidOperationException"/>, the
/// controller caught only <c>NpgsqlException</c>, and the owner got a 500 with an empty body —
/// a blank page on the one screen whose whole job is to state what the bot is set to. Measured on
/// the test host: the seeded LUKO profile has no <c>Exits.TrailingActivationRMultiple</c>, and
/// three requests in a row ended that way while the other nineteen parameters were readable.
/// </summary>
public sealed class MissingParameterTests
{
    private static JsonObject WithoutTrailing() =>
        JsonNode.Parse("""{ "Exits": { "StopAtrMult": 1.25 } }""")!.AsObject();

    [Fact]
    public void A_key_the_profile_does_not_carry_reads_as_nothing_rather_than_throwing()
    {
        Assert.Null(StrategyParameterCatalog.Read(StrategyParameterCatalog.Get("trail"), WithoutTrailing()));
    }

    [Fact]
    public void The_other_parameters_of_the_same_profile_still_read()
    {
        // The point of not throwing: one absent key must cost one field, never the screen.
        Assert.Equal(1.25m, StrategyParameterCatalog.Read(StrategyParameterCatalog.Get("stop"), WithoutTrailing()));
    }

    [Fact]
    public void Presence_is_asked_without_the_arithmetic()
    {
        var values = WithoutTrailing();

        Assert.False(StrategyParameterCatalog.IsPresent(StrategyParameterCatalog.Get("trail"), values));
        Assert.True(StrategyParameterCatalog.IsPresent(StrategyParameterCatalog.Get("stop"), values));
    }

    [Fact]
    public void A_key_the_profile_does_not_carry_cannot_be_written()
    {
        // Writing it would hand the worker a parameter it was not reading, on the authority of a
        // screen. The page edits what is in the profile, never what could be.
        var values = WithoutTrailing();

        Assert.Throws<InvalidOperationException>(
            () => StrategyParameterCatalog.Write(StrategyParameterCatalog.Get("trail"), values, 1.5m));
        Assert.False(values["Exits"]!.AsObject().ContainsKey("TrailingActivationRMultiple"));
    }
}
