#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.IO.Compression;
const int InvalidArgumentsExitCode = 2;

args = [
    System.Environment.GetEnvironmentVariable("GITHUB_WORKSPACE/publish/$RID") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("GITHUB_WORKSPACE/ClaudeConverter-$VERSION-$RID.zip") ?? string.Empty];

if (args is not [var publishDirectory, var zipPath])
{
    Console.WriteLine("::error::Expected the publish directory and zip path arguments.");
    return InvalidArgumentsExitCode;
}

ZipFile.CreateFromDirectory(publishDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

return 0;
