// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.IO.Compression;

if (args is not [var source, var output])
{
    Console.WriteLine("::error::Expected the source folder and output zip arguments.");
    return Program.UsageError;
}

if (!Directory.Exists(source))
{
    Console.WriteLine($"::error::source folder not found: {source}");
    return 1;
}

if (Path.GetDirectoryName(Path.GetFullPath(output)) is { Length: > 0 } folder)
{
    _ = Directory.CreateDirectory(folder);
}

File.Delete(output);

await ZipFile.CreateFromDirectoryAsync(source, output, CompressionLevel.SmallestSize, includeBaseDirectory: true).ConfigureAwait(false);

Console.WriteLine($"Wrote {output}.");

return 0;

/// <summary>Zips a folder, keeping the folder itself as the top-level entry.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;
}
