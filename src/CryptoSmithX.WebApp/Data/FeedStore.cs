using System.Data.Common;
using CryptoSmithX.WebApp.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Data;

/// <summary>
/// Data feeds (0014 phase 2): the per-dataset rows on the exchange page and the Edit feed
/// dialog. Capability is read-only here — only <c>ExchangeWorker</c> declares it; this store only
/// ever writes <c>segment_dataset</c> (policy).
/// </summary>
public static class FeedStore
{
    // capability_key.key -> the label HANDOFF.md specifies for the dialog's left column.
    private static readonly IReadOnlyDictionary<string, string> CapabilityLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["venue_supports"] = "Venue serves it",
        ["we_implement"] = "We implement it",
        ["transports_venue"] = "Transports · venue",
        ["transports_us"] = "Transports · us",
        ["api_versions_venue"] = "API versions · venue",
        ["api_versions_us"] = "API versions · us",
        ["history_depth"] = "History depth",
        ["auth"] = "Auth",
        ["rate_limit"] = "Rate limit",
    };

    public static async Task<IReadOnlyList<FeedRow>> ListAsync(DbConnection conn, string segmentCode, CancellationToken ct) =>
        (await conn.QueryAsync<RawRow>(new CommandDefinition(
            """
            select c.code                                                          as "DatasetCode",
                   c.name                                                          as "DatasetName",
                   c.kind                                                          as "Kind",
                   c.sort_order                                                    as "SortOrder",
                   vs.value                                                        as "VenueSupportsRaw",
                   wi.value                                                        as "WeImplementRaw",
                   hd.value                                                        as "HistoryDepth",
                   hd.source                                                       as "HistorySource",
                   ec.mode                                                         as "Mode",
                   ec.transport                                                    as "Transport",
                   coalesce(ec.interval_s, c.default_interval_s)                   as "EffectiveIntervalS",
                   coalesce(ec.retention_days, c.default_retention_days)           as "EffectiveRetentionDays",
                   ec.note                                                         as "Note",
                   extract(epoch from now() - s.last_success_at)::double precision as "LastSuccessAgeSeconds",
                   coalesce(s.consecutive_failures, 0)                             as "ConsecutiveFailures",
                   s.last_duration_ms                                              as "LastDurationMs",
                   s.avg_duration_ms                                               as "AvgDurationMs"
              from dataset c
              join segment_dataset ec on ec.segment_code = @segmentCode and ec.dataset_code = c.code
              left join segment_dataset_capability vs
                on vs.segment_code = @segmentCode and vs.dataset_code = c.code and vs.capability_key = 'venue_supports'
              left join segment_dataset_capability wi
                on wi.segment_code = @segmentCode and wi.dataset_code = c.code and wi.capability_key = 'we_implement'
              left join segment_dataset_capability hd
                on hd.segment_code = @segmentCode and hd.dataset_code = c.code and hd.capability_key = 'history_depth'
              left join collector_status s on s.segment_code = @segmentCode and s.collector = c.code
             order by c.sort_order
            """,
            new { segmentCode },
            cancellationToken: ct)))
            .Select(r => RawRow.ToFeedRow(r))
            .ToList();

    /// <summary>Every dataset's Edit-feed data for one segment, pre-rendered as hidden dialogs
    /// (see Details.cshtml) — cheap: 9 datasets × 9 capability keys.</summary>
    public static async Task<IReadOnlyList<FeedDetails>> DialogsAsync(DbConnection conn, string segmentCode, CancellationToken ct)
    {
        var datasets = (await conn.QueryAsync<(string Code, string Name, string Description, string Kind)>(
            new CommandDefinition(
                "select code as \"Code\", name as \"Name\", description as \"Description\", kind as \"Kind\" from dataset order by sort_order",
                cancellationToken: ct))).ToList();

        var policies = (await conn.QueryAsync<(string Dataset, string Mode, int? IntervalS, int? RetentionDays, string? Transport, string? Note, string? UpdatedBy, DateTime? UpdatedAt)>(
            new CommandDefinition(
                """
                select dataset_code as "Dataset", mode as "Mode", interval_s as "IntervalS",
                       retention_days as "RetentionDays", transport as "Transport", note as "Note",
                       updated_by as "UpdatedBy", updated_at as "UpdatedAt"
                  from segment_dataset where segment_code = @segmentCode
                """,
                new { segmentCode }, cancellationToken: ct)))
            .ToDictionary(r => r.Dataset, StringComparer.Ordinal);

        var defaults = (await conn.QueryAsync<(string Code, int? DefaultIntervalS, int? DefaultRetentionDays)>(
            new CommandDefinition(
                "select code as \"Code\", default_interval_s as \"DefaultIntervalS\", default_retention_days as \"DefaultRetentionDays\" from dataset",
                cancellationToken: ct)))
            .ToDictionary(r => r.Code, StringComparer.Ordinal);

        var capRows = (await conn.QueryAsync<(string Dataset, string Key, string KeyKind, bool LossRelevant, string? Value, string? Source, string? FilledBy, DateTime? FilledAt, short KeySort)>(
            new CommandDefinition(
                """
                select cc.dataset_code as "Dataset", cc.capability_key as "Key", k.kind as "KeyKind",
                       k.loss_relevant as "LossRelevant", cc.value as "Value", cc.source as "Source",
                       cc.filled_by as "FilledBy", cc.filled_at as "FilledAt", k.sort_order as "KeySort"
                  from segment_dataset_capability cc
                  join capability_key k on k.key = cc.capability_key
                 where cc.segment_code = @segmentCode
                 order by cc.dataset_code, k.sort_order
                """,
                new { segmentCode }, cancellationToken: ct))).ToList();

        var latestLogs = (await conn.QueryAsync<(string Dataset, string Line)>(new CommandDefinition(
            """
            select distinct on (dataset_code) dataset_code as "Dataset",
                   to_char(changed_at, 'DD.MM HH24:MI') || ' · ' || capability_key || ' ' ||
                   coalesce(old_value, '—') || ' → ' || coalesce(new_value, '—') ||
                   ' · ' || changed_by || coalesce(' — ' || note, '')                as "Line"
              from capability_log
             where segment_code = @segmentCode
             order by dataset_code, changed_at desc
            """,
            new { segmentCode }, cancellationToken: ct)))
            .ToDictionary(r => r.Dataset, r => r.Line, StringComparer.Ordinal);

        var result = new List<FeedDetails>(datasets.Count);
        foreach (var c in datasets)
        {
            var caps = capRows.Where(r => r.Dataset == c.Code)
                .Select(r => new FeedCapabilityRow(
                    r.Key, CapabilityLabels.GetValueOrDefault(r.Key, r.Key), r.KeyKind, r.LossRelevant,
                    r.Value, r.Source, r.FilledBy, r.FilledAt))
                .ToList();

            var venueTransports = Split(caps.FirstOrDefault(cap => cap.Key == "transports_venue")?.Value);
            var usTransports = Split(caps.FirstOrDefault(cap => cap.Key == "transports_us")?.Value);
            var weImplement = caps.FirstOrDefault(cap => cap.Key == "we_implement")?.Value == "true";

            policies.TryGetValue(c.Code, out var p);
            defaults.TryGetValue(c.Code, out var d);

            result.Add(new FeedDetails(
                DatasetCode: c.Code,
                DatasetName: c.Name,
                DatasetDescription: c.Description,
                Kind: c.Kind,
                Capabilities: caps,
                Mode: p.Mode ?? "disabled",
                WeImplement: weImplement,
                OwnIntervalS: p.IntervalS,
                DatasetDefaultIntervalS: d.DefaultIntervalS,
                OwnRetentionDays: p.RetentionDays,
                DatasetDefaultRetentionDays: d.DefaultRetentionDays,
                Transport: p.Transport,
                TransportOptions: c.Kind == "derived" ? [] : venueTransports.Intersect(usTransports, StringComparer.Ordinal).ToList(),
                Note: p.Note,
                UpdatedBy: p.UpdatedBy,
                UpdatedAt: p.UpdatedAt,
                LatestCapabilityLogLine: latestLogs.GetValueOrDefault(c.Code)));
        }

        return result;
    }

    /// <summary>Writes the policy of one feed. The loss guard (typing the dataset code) is
    /// checked here too, exactly like <c>ExchangesController.Status</c> does with the exchange code —
    /// never trust the client alone for an irreversible action.</summary>
    public static async Task<string?> SaveAsync(DbConnection conn, FeedSaveInput input, CancellationToken ct)
    {
        var current = await conn.QuerySingleOrDefaultAsync<(string Mode, string? HistoryDepth)?>(new CommandDefinition(
            """
            select ec.mode as "Mode", hd.value as "HistoryDepth"
              from segment_dataset ec
              left join segment_dataset_capability hd
                on hd.segment_code = ec.segment_code and hd.dataset_code = ec.dataset_code and hd.capability_key = 'history_depth'
             where ec.segment_code = @SegmentCode and ec.dataset_code = @DatasetCode
            """,
            input, cancellationToken: ct));

        if (current is null)
        {
            return "Unknown feed.";
        }

        var wasCollecting = current.Value.Mode == "collect";
        var stopsCollecting = input.Mode != "collect";
        var historyless = current.Value.HistoryDepth is null or "none";
        if (wasCollecting && stopsCollecting && historyless
            && !string.Equals(input.ConfirmCode?.Trim(), input.DatasetCode, StringComparison.OrdinalIgnoreCase))
        {
            return "Not saved — the typed dataset code did not match. Nothing was changed.";
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            update segment_dataset
               set mode = @Mode, interval_s = @IntervalS, retention_days = @RetentionDays,
                   transport = @Transport, note = nullif(@Note, ''), updated_by = @UpdatedBy, updated_at = now()
             where segment_code = @SegmentCode and dataset_code = @DatasetCode
            """,
            input, cancellationToken: ct));
        return null;
    }

    private static string[] Split(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Raw Dapper projection (capability values arrive as "true"/"false" text) mapped to
    /// the bool? the view wants.</summary>
    private sealed record RawRow
    {
        public string DatasetCode { get; init; } = "";
        public string DatasetName { get; init; } = "";
        public string Kind { get; init; } = "";
        public short SortOrder { get; init; }
        public string? VenueSupportsRaw { get; init; }
        public string? WeImplementRaw { get; init; }
        public string? HistoryDepth { get; init; }
        public string? HistorySource { get; init; }
        public string Mode { get; init; } = "";
        public string? Transport { get; init; }
        public int? EffectiveIntervalS { get; init; }
        public int? EffectiveRetentionDays { get; init; }
        public string? Note { get; init; }
        public double? LastSuccessAgeSeconds { get; init; }
        public int ConsecutiveFailures { get; init; }
        public int? LastDurationMs { get; init; }
        public double? AvgDurationMs { get; init; }

        public static FeedRow ToFeedRow(RawRow r) => new(
            r.DatasetCode, r.DatasetName, r.Kind, r.SortOrder,
            ParseBool(r.VenueSupportsRaw), ParseBool(r.WeImplementRaw),
            r.HistoryDepth, r.HistorySource,
            r.Mode, r.Transport, r.EffectiveIntervalS, r.EffectiveRetentionDays, r.Note,
            r.LastSuccessAgeSeconds, r.ConsecutiveFailures, r.LastDurationMs, r.AvgDurationMs);

        private static bool? ParseBool(string? raw) => raw switch { "true" => true, "false" => false, _ => null };
    }
}
