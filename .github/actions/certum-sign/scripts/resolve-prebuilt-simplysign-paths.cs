#!/usr/bin/env dotnet
using static System.Environment;

if (GetEnvironmentVariable("SS_DIST") is not { Length: > 0 } dist)
{
    Console.WriteLine("::error::The prebuilt certum-signer image must set SS_DIST.");
    return 1;
}

File.AppendAllLines(
    GetEnvironmentVariable("GITHUB_ENV")!,
    [$"SS_START={Find(dist, "SimplySignDesktop_start")}", $"SS_EXE={Find(dist, "SimplySignDesktop")}", $"SS_PKCS11={Find(dist, "SimplySignPKCS*.so")}"]);

return 0;

static string Find(string folder, string pattern) => Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
