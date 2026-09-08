namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>
/// Which signed-in account owns which bot instance, and what each instance runs on when it has no
/// override rows at all.
///
/// Both live in configuration rather than in the database because neither is ours to store: the
/// mapping is a fact about the bot's deployment, and the baselines are a MIRROR of the workers'
/// own appsettings. The mirror can drift — the bot could be redeployed with different numbers and
/// nothing here would know — which is why the screen says out loud when a figure comes from it
/// rather than from a row, instead of presenting it as something the owner set.
/// </summary>
public sealed class TradingBotOptions
{
    public const string SectionName = "TradingBot";

    /// <summary>Base address of the bot's HTTP API. Used by the health probe.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>username → bot_instance_id. An account absent from this map owns no bot.</summary>
    public Dictionary<string, string> Instances { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>bot_instance_id → the four numbers its deployed configuration carries.</summary>
    public Dictionary<string, BaselineProfile> Baselines { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The instance this account owns, or null if it owns none.</summary>
    public string? InstanceFor(string? username) =>
        username is not null && Instances.TryGetValue(username, out var instance) ? instance : null;

    /// <summary>
    /// The instance's deployed configuration. Absent from the map is not a zero — it means nobody
    /// wrote down what this worker starts with, and the screen has to say it does not know rather
    /// than show a made-up number beside three real ones.
    /// </summary>
    public BaselineProfile? BaselineFor(string botInstanceId) =>
        Baselines.TryGetValue(botInstanceId, out var baseline) ? baseline : null;

    public sealed class BaselineProfile
    {
        public decimal PositionMarginUsd { get; set; }
        public decimal Leverage { get; set; }
        public int MaxOpenPositions { get; set; }
        public int MaxOpenPositionsPerGroup { get; set; }
    }
}
