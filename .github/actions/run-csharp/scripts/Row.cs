// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

namespace ActionsCommon.Benchmarks;

/// <summary>Stores a benchmark comparison result.</summary>
/// <param name="Project">The benchmark project name.</param>
/// <param name="Key">The benchmark name and characteristics.</param>
/// <param name="Base">The baseline measurement.</param>
/// <param name="Head">The candidate measurement.</param>
/// <param name="FirstRatio">The candidate to baseline ratio in the first order.</param>
/// <param name="SecondRatio">The candidate to baseline ratio in the second order.</param>
/// <param name="Result">The comparison verdict.</param>
internal sealed record Row(string Project, string Key, Measurement? Base, Measurement? Head, double? FirstRatio, double? SecondRatio, string Result);
