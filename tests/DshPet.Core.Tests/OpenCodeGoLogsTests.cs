using DshPet.Core.Parsing;
using Xunit;

namespace DshPet.Core.Tests;

/// <summary>
/// The request-log parser, checked against a real captured response
/// (Fixtures/request-logs-sample.json: 50 items, identifiers replaced with
/// TEST placeholders, every structural detail left alone).
///
/// The expected totals were computed independently with PowerShell's own
/// ConvertFrom-Json, so this test fails if the C# parser and a third-party
/// parser ever disagree about the same bytes.
/// </summary>
public class OpenCodeGoLogsTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static IReadOnlyList<Model.RequestLogEntry> LoadSample() =>
        OpenCodeGo.ParseLogs(File.ReadAllText(Fixture("request-logs-sample.json")));

    [Fact]
    public void Reads_every_item_out_of_the_capture()
    {
        Assert.Equal(50, LoadSample().Count);
    }

    [Fact]
    public void Every_item_keeps_its_id()
    {
        Assert.Equal(50, LoadSample().Count(e => !string.IsNullOrEmpty(e.Id)));
        Assert.Equal(50, LoadSample().Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Billable_items_and_their_total_match_the_independent_count()
    {
        var billable = LoadSample().Where(e => e.IsBillable).ToList();
        Assert.Equal(25, billable.Count);
        Assert.Equal(0.06377707, billable.Sum(e => e.CostUsd), 8);
    }

    [Fact]
    public void Rejected_calls_report_a_null_cost_and_are_never_billable()
    {
        var rejected = LoadSample().Where(e => e.Outcome == "rejected").ToList();
        Assert.Equal(20, rejected.Count);
        Assert.All(rejected, e => Assert.True(double.IsNaN(e.CostUsd)));
        Assert.All(rejected, e => Assert.False(e.IsBillable));
    }

    [Fact]
    public void Succeeded_calls_that_cost_nothing_are_not_billable_either()
    {
        var free = LoadSample().Where(e => e.Outcome == "succeeded" && e.CostUsd == 0).ToList();
        Assert.Equal(5, free.Count);
        Assert.All(free, e => Assert.False(e.IsBillable));
    }

    [Fact]
    public void Nested_objects_do_not_leak_into_the_fields()
    {
        // The old string-scanning parser existed because requestHeaders values can
        // contain braces and quoted commas. Structural parsing cannot be fooled,
        // so a regression here would mean the shape changed, not the scanner.
        var first = LoadSample()[0];
        Assert.Equal("6586bdb1-8af2-48c3-b146-4f9b38ed7a41", first.Id);
        Assert.Equal("rejected", first.Outcome);
        Assert.Equal("deepseek-v4.1-flash", first.Model);
        Assert.Equal(1790841627983, first.StartedAt);
        Assert.Equal(46, first.DurationMs);
    }

    [Fact]
    public void StartedAt_is_ordered_newest_first_like_the_api_sends_it()
    {
        var sample = LoadSample();
        // no index-from-end syntax on net48 (needs System.Index)
        Assert.True(sample[0].StartedAt >= sample[sample.Count - 1].StartedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"items":null}""")]
    [InlineData("""{"items":"not an array"}""")]
    public void Unusable_payloads_return_nothing_instead_of_throwing(string? body)
    {
        Assert.Empty(OpenCodeGo.ParseLogs(body));
    }

    [Fact]
    public void Items_without_an_id_are_dropped_because_they_cannot_be_de_duplicated()
    {
        const string noId = """
        {"items":[{"startedAt":1,"outcome":"succeeded","cost":0.5},
                  {"id":"keep","startedAt":2,"outcome":"succeeded","cost":0.5}]}
        """;
        var entries = OpenCodeGo.ParseLogs(noId);
        Assert.Single(entries);
        Assert.Equal("keep", entries[0].Id);
    }

    [Fact]
    public void A_real_succeeded_call_carries_its_exact_cost()
    {
        // This is the value the per-call cue prints above the character's head.
        var entry = LoadSample().Single(e => e.Id == "3ee2ff33-fa43-43a0-a66e-8a015e365c01");
        Assert.Equal("succeeded", entry.Outcome);
        Assert.Equal(0.00241094, entry.CostUsd, 8);
        Assert.True(entry.IsBillable);
    }
}
