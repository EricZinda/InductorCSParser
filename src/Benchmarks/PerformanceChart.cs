using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Reports;

namespace InductorParser.Benchmarks;

// Post-benchmark hook that regenerates src/Benchmarks/performance-chart.html
// from the Summary that BenchmarkSwitcher returns. Wired into Program.Main
// so every benchmark run produces a fresh chart alongside the BenchmarkDotNet
// artifacts.
//
// The HTML structure (notes, styling, scale choice) lives in HtmlTemplate
// below. Only the parser-label list and the four per-shape data arrays are
// substituted at run time.
//
// Skipped quietly if the run didn't cover all four shapes (e.g. the user
// filtered to --filter *Big*), so partial runs don't clobber a full chart.
public static class PerformanceChart
{
    private static readonly string[] Shapes = ["Big", "Deep", "Long", "Wide"];

    public static void TryUpdate(Summary summary)
    {
        if (summary.BenchmarksCases.Length == 0) return;
        if (summary.BenchmarksCases[0].Descriptor.Type != typeof(Json.JsonBench)) return;

        var shapeMeans = Shapes.ToDictionary(s => s, _ => new Dictionary<string, double>());
        foreach (var report in summary.Reports)
        {
            if (report.ResultStatistics == null) continue;
            var methodName = report.BenchmarkCase.Descriptor.WorkloadMethod.Name;
            var splitAt = methodName.IndexOf("Json_", StringComparison.Ordinal);
            if (splitAt < 0) continue;
            var shape = methodName[..splitAt];
            var parser = methodName[(splitAt + "Json_".Length)..];
            if (!shapeMeans.ContainsKey(shape)) continue;
            shapeMeans[shape][parser] = report.ResultStatistics.Mean / 1000.0;
        }

        foreach (var shape in Shapes)
        {
            if (shapeMeans[shape].Count == 0)
            {
                Console.WriteLine($"Chart not regenerated: no {shape} results in this run (partial run skipped).");
                return;
            }
        }

        // Parsers are ordered by their Big-shape time (fastest to slowest).
        // Other shapes use the same order so a visual dip or spike on one
        // shape relative to another tells you the parser is shape-sensitive.
        var parserOrder = shapeMeans["Big"]
            .OrderBy(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        string benchDirectory;
        try
        {
            benchDirectory = GetBenchDirectory();
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Chart not regenerated: {ex.Message}");
            return;
        }

        var runDate = DateTime.Now;
        var htmlPath = Path.Combine(benchDirectory, "performance-chart.html");
        var jpgPath = Path.Combine(benchDirectory, "performance-chart.jpg");

        File.WriteAllText(htmlPath, BuildHtml(parserOrder, shapeMeans, runDate));
        Console.WriteLine($"Performance chart regenerated: {htmlPath}");

        try
        {
            SaveJpeg(parserOrder, shapeMeans, runDate, jpgPath);
            Console.WriteLine($"Performance chart regenerated: {jpgPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"JPG chart not regenerated: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string GetBenchDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (directory.GetFiles("Benchmarks.csproj").Length > 0)
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            "Benchmarks.csproj not found walking up from " + AppContext.BaseDirectory);
    }

    // The four per-shape line colors here are kept in sync with the same
    // four hex codes in HtmlTemplate (Chart.js dataset borderColor) so the
    // JPG and HTML render with identical series colors.
    private static readonly (string Shape, ScottPlot.Color Color)[] ShapeColors =
    [
        ("Big",  ScottPlot.Color.FromHex("#1f77b4")),
        ("Deep", ScottPlot.Color.FromHex("#ff7f0e")),
        ("Long", ScottPlot.Color.FromHex("#2ca02c")),
        ("Wide", ScottPlot.Color.FromHex("#d62728")),
    ];

    private static void SaveJpeg(
        List<string> parserOrder,
        Dictionary<string, Dictionary<string, double>> shapeMeans,
        DateTime runDate,
        string jpgPath)
    {
        var plot = new ScottPlot.Plot();

        var xs = Enumerable.Range(0, parserOrder.Count).Select(i => (double)i).ToArray();

        foreach (var (shape, color) in ShapeColors)
        {
            var ys = parserOrder
                .Select(parser => shapeMeans[shape].TryGetValue(parser, out var mean) ? mean : double.NaN)
                .ToArray();
            var line = plot.Add.ScatterLine(xs, ys);
            line.LegendText = shape;
            line.LineColor = color;
            line.MarkerStyle.FillColor = color;
            line.MarkerStyle.LineColor = color;
            line.MarkerSize = 6;
            line.LineWidth = 2;
        }

        var ticks = parserOrder
            .Select((parser, index) => new ScottPlot.Tick(index, parser))
            .ToArray();
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
        // -45° rotation reads diagonally below the axis: the standard
        // "tilted axis label" look. Alignment.MiddleRight anchors the
        // right end of the text at the tick, so the label hangs to
        // the lower-left, far enough below the next tick to avoid
        // overlap on the long parser names.
        plot.Axes.Bottom.TickLabelStyle.Rotation = -45;
        plot.Axes.Bottom.TickLabelStyle.Alignment = ScottPlot.Alignment.MiddleRight;
        plot.Axes.Bottom.TickLabelStyle.OffsetX = -3;
        plot.Axes.Bottom.TickLabelStyle.OffsetY = 3;
        // Reserve enough vertical space for the longest vertical label
        // ("InductorParserToken" / "InductorParserTyped") plus the
        // x-axis title.
        plot.Axes.Bottom.MinimumSize = 170;
        // Reserve enough horizontal space so the y-axis tick labels fit.
        plot.Axes.Left.MinimumSize = 70;

        var runDateText = runDate.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        plot.Title($"JSON parser performance — run {runDateText}");
        plot.XLabel("Parser (ordered by Big-shape time, fastest to slowest)");
        plot.YLabel("Mean parse time (μs)");
        plot.ShowLegend(ScottPlot.Edge.Top);

        // A little extra margin around the data so markers near the edges
        // aren't clipped by the axis frame.
        plot.Axes.Margins(left: 0.04, right: 0.04, bottom: 0.05, top: 0.10);
        plot.SaveJpeg(jpgPath, 1300, 700, 90);
    }

    private static string BuildHtml(
        List<string> parserOrder,
        Dictionary<string, Dictionary<string, double>> shapeMeans,
        DateTime runDate)
    {
        string DataArray(string shape) =>
            "[" + string.Join(", ", parserOrder.Select(p =>
                shapeMeans[shape].TryGetValue(p, out var mean)
                    ? mean.ToString("F2", CultureInfo.InvariantCulture)
                    : "null")) + "]";

        var labels = "[\n  " + string.Join(",\n  ",
            parserOrder.Select(p => "\"" + p + "\"")) + "\n]";

        var runDateText = runDate.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        return HtmlTemplate
            .Replace("__LABELS__", labels)
            .Replace("__BIG__", DataArray("Big"))
            .Replace("__DEEP__", DataArray("Deep"))
            .Replace("__LONG__", DataArray("Long"))
            .Replace("__WIDE__", DataArray("Wide"))
            .Replace("__RUN_DATE__", runDateText);
    }

    private const string HtmlTemplate = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<title>JSON parser performance by shape</title>
<style>
  body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, sans-serif; max-width: 960px; margin: 2em auto; padding: 0 1em; color: #222; }
  h1 { font-size: 1.3em; margin-bottom: 0.2em; }
  .subtitle { color: #666; margin-bottom: 1.5em; }
  .chart-wrapper { position: relative; height: 520px; }
  .note { font-size: 0.88em; color: #444; margin-top: 1.5em; }
  .note ul { padding-left: 1.4em; margin: 0.5em 0; }
  .note li { margin-bottom: 0.3em; }
  code { background: #f4f4f4; padding: 0.1em 0.3em; border-radius: 3px; font-size: 0.9em; }
</style>
</head>
<body>

<h1>JSON parser performance by shape</h1>
<div class="subtitle">Mean parse time in microseconds. Lower is better. BenchmarkDotNet ShortRun, .NET 8.0.25 on Arm64. Four lines, one per input shape (Big, Deep, Long, Wide). Regenerated automatically on every benchmark run. <strong>Run: __RUN_DATE__</strong></div>

<div class="chart-wrapper">
  <canvas id="chart"></canvas>
</div>

<div class="note">
  <ul>
    <li>Parsers are ordered left to right by their time on the Big shape (fastest to slowest). The same parser order is used for all four lines, so a dip or spike tells you that parser is unusually fast or slow on that shape.</li>
    <li><strong>Shape definitions:</strong> Big = balanced (4 root elements, depth 4, width 3). Long = 256 top-level elements. Deep = 256 levels of nesting. Wide = one object with 256 members. See <a href="README.md">README.md</a> for full methodology.</li>
    <li>Superpower crashes with an uncatchable <code>StackOverflowException</code> on Deep (256 levels overflow its combinator pipeline). Shown as a gap in the Deep line.</li>
    <li>Pegasus appears twice: <strong>PegasusOptimized</strong> uses idiomatic patterns (<code>[^"\\]+</code> bulk runs, <code>&lt;min,max,sep&gt;</code> delimited repetition); <strong>PegasusWiki</strong> is the per-character style shown in the Pegasus wiki JSON example.</li>
    <li>Pidgin uses the bulk-run fast path via <code>Token(pred).AtLeastOnceString()</code>. Sprache and Superpower use per-character ordered choice (bulk-run measured slower on those libraries, see README).</li>
    <li><strong>Deep is the odd shape out:</strong> Parlot and ParlotCompiled are both <em>faster</em> than STJ there. Deep has far fewer characters than the other shapes (~2,600 vs 5,000-8,000), so per-level overhead dominates and STJ's depth-validation cost tips it into last place among the fast parsers.</li>
  </ul>
</div>

<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.0/dist/chart.umd.min.js"></script>
<script>
const labels = __LABELS__;

const data = {
  labels: labels,
  datasets: [
    {
      label: "Big",
      data: __BIG__,
      borderColor: "#1f77b4",
      backgroundColor: "#1f77b4",
      borderWidth: 2,
      tension: 0
    },
    {
      label: "Deep",
      data: __DEEP__,
      borderColor: "#ff7f0e",
      backgroundColor: "#ff7f0e",
      borderWidth: 2,
      tension: 0,
      spanGaps: false
    },
    {
      label: "Long",
      data: __LONG__,
      borderColor: "#2ca02c",
      backgroundColor: "#2ca02c",
      borderWidth: 2,
      tension: 0
    },
    {
      label: "Wide",
      data: __WIDE__,
      borderColor: "#d62728",
      backgroundColor: "#d62728",
      borderWidth: 2,
      tension: 0
    }
  ]
};

new Chart(document.getElementById("chart"), {
  type: "line",
  data: data,
  options: {
    responsive: true,
    maintainAspectRatio: false,
    interaction: { mode: "index", intersect: false },
    plugins: {
      title: { display: true, text: "JSON parser performance — run __RUN_DATE__", font: { size: 14 }, padding: { top: 4, bottom: 12 } },
      legend: { position: "top", labels: { boxWidth: 14, padding: 14 } },
      tooltip: {
        callbacks: {
          label: ctx => ctx.dataset.label + ": " + (ctx.parsed.y === null ? "CRASH (stack overflow)" : ctx.parsed.y.toFixed(2) + " μs")
        }
      }
    },
    scales: {
      y: {
        type: "linear",
        beginAtZero: true,
        title: { display: true, text: "Mean parse time (μs)", font: { size: 13 } }
      },
      x: {
        title: { display: true, text: "Parser (ordered by Big-shape time, fastest to slowest)", font: { size: 13 } },
        ticks: { autoSkip: false, maxRotation: 40, minRotation: 40 }
      }
    }
  }
});
</script>

</body>
</html>
""";
}
