using System.Globalization;

namespace Oriel.Tests;

// The parity summaries are hand-kept totals of their checklist rows, and merges from several branches leave them stale.
public sealed class ParityCountTests
{
    private static readonly string s_root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "docs", "ELECTRON-PARITY.md")) && Directory.Exists(Path.Combine(dir.FullName, "src"))) return dir.FullName;
        throw new InvalidOperationException("No repository root above the test output.");
    }

    internal sealed record Tally(int Done, int Partial, int Missing, int NotApplicable, int Items)
    {
        public int Applicable => Items - NotApplicable;
        public static Tally operator +(Tally a, Tally b) => new(a.Done + b.Done, a.Partial + b.Partial, a.Missing + b.Missing, a.NotApplicable + b.NotApplicable, a.Items + b.Items);
    }

    internal static string[] Cells(string line) => [.. line.Trim().Trim('|').Split('|').Select(c => c.Trim())];

    internal static string Percent(double part, int whole)
        => (whole == 0 ? 0 : 100 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    internal static string SummaryRow(string name, Tally t)
        => $"| {name} | {t.Items} | {t.Done} | {t.Partial} | {t.Missing} | {t.NotApplicable} | {Percent(t.Done, t.Applicable)} | {Percent(t.Done + t.Partial / 2.0, t.Applicable)} |";

    // The checklist's sections in order, each counted from its rows' Status column.
    internal static List<(string Name, Tally Tally)> CountChecklist(string[] lines)
    {
        var start = Array.FindIndex(lines, l => l.StartsWith("## Checklist", StringComparison.Ordinal));
        var sections = new List<(string, Tally)>();
        var statusColumn = -1;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("## ", StringComparison.Ordinal)) break;
            if (line.StartsWith("### ", StringComparison.Ordinal)) { sections.Add((line, new Tally(0, 0, 0, 0, 0))); statusColumn = -1; continue; }
            if (sections.Count == 0 || !line.StartsWith('|')) continue;
            var cells = Cells(line);
            if (statusColumn < 0) { statusColumn = Array.IndexOf(cells, "Status"); continue; }
            if (cells.All(c => c.Trim(':').Length > 0 && c.Trim(':').All(ch => ch == '-'))) continue;
            var (name, t) = sections[^1];
            sections[^1] = (name, cells[statusColumn] switch
            {
                "done" => t with { Done = t.Done + 1, Items = t.Items + 1 },
                "partial" => t with { Partial = t.Partial + 1, Items = t.Items + 1 },
                "missing" => t with { Missing = t.Missing + 1, Items = t.Items + 1 },
                "n/a" => t with { NotApplicable = t.NotApplicable + 1, Items = t.Items + 1 },
                var other => throw new InvalidOperationException($"line {i + 1}: unknown status '{other}'"),
            });
        }
        return sections;
    }

    private static Tally CheckChecklist(string relative)
    {
        var lines = File.ReadAllLines(Path.Combine(s_root, relative));
        var sections = CountChecklist(lines);
        var summaryStart = Array.FindIndex(lines, l => l.StartsWith("## Summary", StringComparison.Ordinal));
        var checklistStart = Array.FindIndex(lines, l => l.StartsWith("## Checklist", StringComparison.Ordinal));
        var rows = lines[summaryStart..checklistStart].Where(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| Area", StringComparison.Ordinal)).ToList();
        Assert.Equal(sections.Count + 1, rows.Count);
        var total = new Tally(0, 0, 0, 0, 0);
        for (var k = 0; k < sections.Count; k++)
        {
            Assert.Equal(SummaryRow(Cells(rows[k])[0], sections[k].Tally), rows[k]);
            total += sections[k].Tally;
        }
        Assert.Equal(SummaryRow("**Total**", total), rows[^1]);
        return total;
    }

    [Fact]
    public void BrowserWindowSummaryMatchesItsRows() => CheckChecklist(Path.Combine("docs", "parity", "browser-window.md"));

    [Fact]
    public void WebContentsSummaryMatchesItsRows() => CheckChecklist(Path.Combine("docs", "parity", "web-contents.md"));

    [Fact]
    public void DownloadItemSummaryMatchesItsRows() => CheckChecklist(Path.Combine("docs", "parity", "download-item.md"));

    [Fact]
    public void AppSummaryMatchesItsRows() => CheckChecklist(Path.Combine("docs", "parity", "app.md"));

    [Fact]
    public void ElectronParityFiguresMatchTheirRows()
    {
        var browserWindow = CheckChecklist(Path.Combine("docs", "parity", "browser-window.md"));
        var webContents = CheckChecklist(Path.Combine("docs", "parity", "web-contents.md"));
        var downloadItem = CheckChecklist(Path.Combine("docs", "parity", "download-item.md"));
        var app = CheckChecklist(Path.Combine("docs", "parity", "app.md"));
        var lines = File.ReadAllLines(Path.Combine(s_root, "docs", "ELECTRON-PARITY.md"));
        int[] modules = [0, 0, 0], m2 = [0, 0, 0];
        var inModules = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal)) inModules = !line.StartsWith("## Progress", StringComparison.Ordinal);
            if (!inModules || !line.StartsWith("| ", StringComparison.Ordinal) || line.StartsWith("| Electron ", StringComparison.Ordinal)) continue;
            var cells = Cells(line);
            if (cells.Length < 5) continue;
            var status = cells[^1];
            var k = status.StartsWith("done", StringComparison.Ordinal) ? 0 : status.StartsWith("partial", StringComparison.Ordinal) ? 1 : 2;
            modules[k]++;
            // "M2 core, M6 rest" rows are not M2 rows.
            if (cells[3] == "M2") m2[k]++;
        }
        string Row(string name, Tally t) => $"| {name} | {t.Applicable} | {t.Done} | {t.Partial} | {t.Missing} | {Percent(t.Done, t.Applicable)} | {Percent(t.Done + t.Partial / 2.0, t.Applicable)} |";
        Assert.Contains($"| Module rows below | {modules[0]} | {modules[1]} | {modules[2]} | {modules.Sum()} |", lines);
        Assert.Contains($"| M2 rows | {m2[0]} | {m2[1]} | {m2[2]} | {m2.Sum()} |", lines);
        Assert.Contains(Row("[BrowserWindow](parity/browser-window.md) (with `BaseWindow`)", browserWindow), lines);
        Assert.Contains(Row("[webContents](parity/web-contents.md) (with `navigationHistory`, `WebFrameMain`)", webContents), lines);
        Assert.Contains(Row("[DownloadItem](parity/download-item.md) (with the session's download API)", downloadItem), lines);
        Assert.Contains(lines, l => l.StartsWith("| `DownloadItem` |", StringComparison.Ordinal)
            && l.Contains($"{downloadItem.Done} of {downloadItem.Applicable} applicable items done ({Percent(downloadItem.Done, downloadItem.Applicable)})", StringComparison.Ordinal));
        Assert.Contains(Row("[app](parity/app.md) (with `app.dock` as one item)", app), lines);
        Assert.Contains(lines, l => l.StartsWith("| `app` |", StringComparison.Ordinal)
            && l.Contains($"{app.Done} of {app.Applicable} applicable items done ({Percent(app.Done, app.Applicable)})", StringComparison.Ordinal));
    }

    [Fact]
    public void CountsAChecklistBySection()
    {
        string[] doc =
        [
            "## Checklist", "### A", "| Item | Status |", "|---|---|", "| `a` | done |", "| `b` | partial |", "### B", "| Item | Status |", "|---|---|", "| `c` | n/a |", "| `d` | missing |",
        ];
        var sections = CountChecklist(doc);
        Assert.Equal(new Tally(1, 1, 0, 0, 2), sections[0].Tally);
        Assert.Equal(new Tally(0, 0, 1, 1, 2), sections[1].Tally);
        Assert.Equal("| x | 2 | 1 | 1 | 0 | 0 | 50.0% | 75.0% |", SummaryRow("x", sections[0].Tally));
    }
}
