// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Text.RegularExpressions;

namespace ActionsCommon.Benchmarks;

/// <summary>Provides compiled patterns for workflow inputs.</summary>
internal static partial class BenchmarkPatterns
{
    /// <summary>Gets the compiled input pattern.</summary>
    [GeneratedRegex(@"\(([^()]*)\)\s*$")]
    internal static partial Regex MyRegex { get; }
}
