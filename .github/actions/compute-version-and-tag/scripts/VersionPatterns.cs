// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Text.RegularExpressions;

namespace ActionsCommon.Versioning;

/// <summary>Provides compiled patterns for workflow inputs.</summary>
internal static partial class VersionPatterns
{
    /// <summary>Gets the release version pattern without a tag prefix.</summary>
    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)$")]
    internal static partial Regex Core { get; }

    /// <summary>Gets the compiled input pattern.</summary>
    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    internal static partial Regex MyRegex { get; }
}
