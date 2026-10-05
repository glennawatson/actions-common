#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using static System.Environment;

if (GetEnvironmentVariable("SS_DIST") is not { Length: > 0 } dist)
{
    Console.WriteLine("::error::The prebuilt certum-signer image must set SS_DIST.");
    return 1;
}

File.AppendAllLines(
    GetEnvironmentVariable("GITHUB_ENV")!,
    [
    $"SS_START={Find(dist, "SimplySignDesktop_start")}",
    $"SS_EXE={Find(dist, "SimplySignDesktop")}",
    $"SS_PKCS11={Find(dist, "SimplySignPKCS*.so")}"]);

return 0;

static string Find(string folder, string pattern)
{
    using var files = Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories).GetEnumerator();
    return files.MoveNext() ? files.Current : string.Empty;
}
