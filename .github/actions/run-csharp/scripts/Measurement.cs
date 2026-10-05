// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

namespace ActionsCommon.Benchmarks;

/// <summary>Stores a benchmark's timing interval and allocated bytes.</summary>
/// <param name="Mean">The mean duration in nanoseconds.</param>
/// <param name="Lower">The lower confidence bound in nanoseconds.</param>
/// <param name="Upper">The upper confidence bound in nanoseconds.</param>
/// <param name="Allocated">The allocated bytes per operation.</param>
internal readonly record struct Measurement(double Mean, double Lower, double Upper, long Allocated);
