// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Globalization;
using System.Text;

namespace ActionsCommon.Benchmarks;

/// <summary>Formats benchmark comparisons for workflow summaries.</summary>
internal static class ReportRenderer
{
    /// <summary>The benchmark comparison table header.</summary>
    private const string TableHeader = "| Benchmark | Base | Head | Head/Base (1st) | Head/Base (2nd) | Allocated base | Allocated head | Result |\n|---|---:|---:|---:|---:|---:|---:|---|";

    /// <summary>Formats a comparison section.</summary>
    /// <param name = "title">The section title.</param>
    /// <param name = "rows">The comparison rows.</param>
    /// <returns>The comparison data or formatted value.</returns>
    internal static string Section(string title, IEnumerable<Row> rows)
    {
        var section = new StringBuilder().AppendLine(CultureInfo.InvariantCulture, $"## {title}").AppendLine();
        var any = false;
        foreach (var row in rows)
        {
            if (!any)
            {
                _ = section.AppendLine(TableHeader);
                any = true;
            }

            _ = section.AppendLine(RowLine(row));
        }

        return section.AppendLine(any ? string.Empty : "None.").AppendLine().ToString();
    }

    /// <summary>Formats a benchmark row.</summary>
    /// <param name = "row">The comparison row.</param>
    /// <returns>The comparison data or formatted value.</returns>
    internal static string RowLine(Row row)
    {
        var key = row.Key.Replace("|", "\\|", StringComparison.Ordinal);
        return $"| {key} | {Format(row.Base)} | {Format(row.Head)} | {Ratio(row.FirstRatio)} | {Ratio(row.SecondRatio)} | {Bytes(row.Base)} | {Bytes(row.Head)} | {row.Result} |";
    }

    /// <summary>Formats a timing ratio.</summary>
    /// <param name = "ratio">The candidate to baseline ratio.</param>
    /// <returns>The comparison data or formatted value.</returns>
    internal static string Ratio(double? ratio) => ratio is { } value ? value.ToString("0.000", CultureInfo.InvariantCulture) : "-";

    /// <summary>Formats a duration and confidence interval.</summary>
    /// <param name = "measurement">The timing and allocation measurement.</param>
    /// <returns>The comparison data or formatted value.</returns>
    internal static string Format(Measurement? measurement)
    {
        const double NanosecondsPerSecond = 1_000_000_000;
        const double NanosecondsPerMillisecond = 1_000_000;
        const double NanosecondsPerMicrosecond = 1_000;
        if (measurement is not { } value)
        {
            return "-";
        }

        var (scale, unit) = value.Mean switch
        {
            >= NanosecondsPerSecond => (NanosecondsPerSecond, "s"),
            >= NanosecondsPerMillisecond => (NanosecondsPerMillisecond, "ms"),
            >= NanosecondsPerMicrosecond => (NanosecondsPerMicrosecond, "us"),
            _ => (1D, "ns"),
        };
        return string.Create(CultureInfo.InvariantCulture, $"{value.Mean / scale:0.00} ± {((value.Upper - value.Mean) / scale):0.00} {unit}");
    }

    /// <summary>Formats allocated bytes.</summary>
    /// <param name = "measurement">The timing and allocation measurement.</param>
    /// <returns>The comparison data or formatted value.</returns>
    internal static string Bytes(Measurement? measurement) => measurement is { Allocated: >= 0 } value ? $"{value.Allocated.ToString("N0", CultureInfo.InvariantCulture)} B" : "-";
}
