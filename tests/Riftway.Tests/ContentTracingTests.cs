using System.Text.Json.Nodes;

namespace Riftway.Tests;

public sealed class TracePlanTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("record-until-full", false)]
    [InlineData("enable-sampling", false)]                          // Chromium no longer knows it
    [InlineData("record-continuously,record-until-full", false)]    // the last mode wins
    [InlineData("record-continuously", true)]
    [InlineData("record-until-full,record-as-much-as-possible", true)]
    [InlineData(" trace-to-console ", true)]                        // tokens are trimmed
    [InlineData("enable-argument-filter", true)]
    [InlineData("enable-systrace=sched", true)]
    public void TraceOptionsBeyondTheDefaultsNeedDevTools(string options, bool devTools)
    {
        var plan = TracePlan.For("blink,-v8", options);
        Assert.Equal(devTools, plan.DevTools);
        Assert.Equal("blink,-v8", plan.Categories);
        Assert.Equal(devTools ? options : null, plan.Options);
        Assert.Null(plan.TraceConfigJson);
    }

    [Fact]
    public void CategoriesOnlyGoToCefAsOneFilter()
    {
        var plan = TracePlan.For(new TraceConfig { IncludedCategories = ["a", "b*"], ExcludedCategories = ["c"], RecordingMode = "record-until-full" });
        Assert.Equal(new TracePlan(false, "a,b*,-c", null, null), plan);
        Assert.Equal(new TracePlan(false, "", null, null), TracePlan.For(new TraceConfig { RecordingMode = "bogus", TraceBufferSizeInKb = 0 }));
    }

    [Fact]
    public void ModeAndArgumentFilterGoAsTraceOptions()
    {
        Assert.Equal(new TracePlan(true, "a", "record-continuously", null), TracePlan.For(new TraceConfig { IncludedCategories = ["a"], RecordingMode = "record-continuously" }));
        Assert.Equal(new TracePlan(true, "-c", "record-until-full,enable-argument-filter", null), TracePlan.For(new TraceConfig { ExcludedCategories = ["c"], EnableArgumentFilter = true }));
        Assert.Equal(new TracePlan(true, "", "trace-to-console", null), TracePlan.For(new TraceConfig { RecordingMode = "trace-to-console" }));
    }

    [Fact]
    public void BufferSizeAndMemoryDumpsGoAsADevToolsTraceConfig()
    {
        var plan = TracePlan.For(new TraceConfig
        {
            IncludedCategories = ["disabled-by-default-memory-infra"],
            ExcludedCategories = ["*"],
            RecordingMode = "record-as-much-as-possible",
            TraceBufferSizeInKb = 4096,
            EnableArgumentFilter = true,
            MemoryDumpConfig = new JsonObject { ["triggers"] = new JsonArray(new JsonObject { ["mode"] = "detailed", ["periodic_interval_ms"] = 1000 }) },
        });
        Assert.True(plan.DevTools);
        Assert.Null(plan.Categories);
        Assert.Null(plan.Options);
        Assert.Equal("""{"recordMode":"recordAsMuchAsPossible","traceBufferSizeInKb":4096,"enableArgumentFilter":true,"includedCategories":["disabled-by-default-memory-infra"],"excludedCategories":["*"],"memoryDumpConfig":{"triggers":[{"mode":"detailed","periodic_interval_ms":1000}]}}""", plan.TraceConfigJson);
        Assert.Equal("""{"recordMode":"recordContinuously","traceBufferSizeInKb":1}""", TracePlan.For(new TraceConfig { RecordingMode = "record-continuously", TraceBufferSizeInKb = 1 }).TraceConfigJson);
        Assert.Equal("""{"recordMode":"recordUntilFull","memoryDumpConfig":{}}""", TracePlan.For(new TraceConfig { MemoryDumpConfig = [] }).TraceConfigJson);
    }

    [Theory]
    [InlineData(new[] { "-a" }, new string[0])]       // a leading '-' would exclude in the string form
    [InlineData(new[] { "a,b" }, new string[0])]      // commas split it
    [InlineData(new[] { " a" }, new string[0])]       // the string form trims
    [InlineData(new string[0], new[] { "b " })]
    [InlineData(new string[0], new[] { "x,y" })]
    public void NamesTheStringFormWouldChangeGoAsATraceConfig(string[] included, string[] excluded)
    {
        var plan = TracePlan.For(new TraceConfig { IncludedCategories = included, ExcludedCategories = excluded });
        Assert.True(plan.DevTools);
        Assert.NotNull(plan.TraceConfigJson);
    }

    [Fact]
    public void TraceToConsoleCannotTravelInATraceConfig()
        => Assert.Throws<NotSupportedException>(() => TracePlan.For(new TraceConfig { RecordingMode = "trace-to-console", TraceBufferSizeInKb = 8 }));

    [Fact]
    public void OptionsNoRouteCarriesThrow()
    {
        Assert.Contains("included_process_ids", Assert.Throws<NotSupportedException>(() => TracePlan.For(new TraceConfig { IncludedProcessIds = [1] })).Message);
        Assert.Contains("histogram_names", Assert.Throws<NotSupportedException>(() => TracePlan.For(new TraceConfig { HistogramNames = ["h"] })).Message);
        Assert.Contains("trace_buffer_size_in_events", Assert.Throws<NotSupportedException>(() => TracePlan.For(new TraceConfig { TraceBufferSizeInEvents = 1 })).Message);
        Assert.False(TracePlan.For(new TraceConfig { TraceBufferSizeInEvents = 0 }).DevTools);
    }
}
