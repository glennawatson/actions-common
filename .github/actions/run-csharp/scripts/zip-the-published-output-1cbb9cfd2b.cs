#!/usr/bin/env dotnet
using System.IO.Compression;
args = [System.Environment.GetEnvironmentVariable("GITHUB_WORKSPACE/publish/$RID") ?? string.Empty, System.Environment.GetEnvironmentVariable("GITHUB_WORKSPACE/ClaudeConverter-$VERSION-$RID.zip") ?? string.Empty];

if (args is not [var publishDirectory, var zipPath])
{
    Console.WriteLine("::error::Expected the publish directory and zip path arguments.");
    return 2;
}

ZipFile.CreateFromDirectory(publishDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
return 0;
