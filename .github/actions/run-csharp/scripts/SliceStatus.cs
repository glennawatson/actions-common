// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

namespace ActionsCommon.Benchmarks;

/// <summary>Stores the exported benchmark and pass counts for a slice.</summary>
/// <param name="Slice">The slice identifier.</param>
/// <param name="Project">The benchmark project name.</param>
/// <param name="Benchmarks">The planned benchmark count.</param>
/// <param name="Passes">The exported pass count.</param>
internal readonly record struct SliceStatus(string Slice, string Project, int Benchmarks, int Passes);
