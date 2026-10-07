using System.Text.RegularExpressions;

namespace Oriel.Tests;

// docs/DRIFT.md is merged from several machines' branches; these keep its IDs unique and every reference to it live.
public sealed partial class DriftRegisterTests
{
    private static readonly string s_root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "docs", "DRIFT.md")) && Directory.Exists(Path.Combine(dir.FullName, "src"))) return dir.FullName;
        throw new InvalidOperationException("No repository root above the test output.");
    }

    [GeneratedRegex(@"^\| (?<id>[A-Z]{1,2}[0-9]+) \|", RegexOptions.Multiline)]
    private static partial Regex RowId();

    // "drift W5", "DRIFT C37", "(drift E30)"; a reference to a closed row says "was DRIFT X".
    [GeneratedRegex(@"(?<!was )\b(?:[Dd]rift|DRIFT) (?<id>[A-Z]{1,2}[0-9]+)\b")]
    private static partial Regex Reference();

    internal static IReadOnlyList<string> Ids(string register) => [.. RowId().Matches(register).Select(m => m.Groups["id"].Value)];

    internal static IEnumerable<string> References(string text) => Reference().Matches(text).Select(m => m.Groups["id"].Value);

    [Fact]
    public void EveryRowHasItsOwnId()
    {
        var duplicates = Ids(File.ReadAllText(Path.Combine(s_root, "docs", "DRIFT.md"))).GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryReferenceNamesALiveRow()
    {
        var ids = Ids(File.ReadAllText(Path.Combine(s_root, "docs", "DRIFT.md"))).ToHashSet();
        var dangling = new List<string>();
        foreach (var folder in new[] { "src", "samples", "docs" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(s_root, folder), "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs") || f.EndsWith(".md") || f.EndsWith(".js"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
                foreach (var id in References(File.ReadAllText(file)).Where(id => !ids.Contains(id)))
                    dangling.Add($"{Path.GetRelativePath(s_root, file)}: {id}");
        Assert.Empty(dangling);
    }

    [Fact]
    public void ParsesRowsAndReferences_SkippingClosedOnes()
    {
        Assert.Equal(["A1", "W12"], Ids("| A1 | x |\n| W12 | y |\nnot | B3 | row"));
        Assert.Equal(["C3", "E27", "W5"], References("see drift C3 and DRIFT E27 (drift W5); was DRIFT A4; adrift X1"));
    }
}
