// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Globalization;
using static System.Environment;

if (args is not [var registry, var username, var image, var context])
{
    Console.WriteLine("::error::Expected the registry, username, image and context arguments.");
    return Program.UsageError;
}

if (GetEnvironmentVariable("REGISTRY_TOKEN") is not { Length: > 0 } token)
{
    Console.WriteLine("::error::REGISTRY_TOKEN is not set.");
    return 1;
}

if (GetEnvironmentVariable("GITHUB_SHA") is not { Length: >= Program.ShortShaLength } sha)
{
    Console.WriteLine("::error::GITHUB_SHA is not set.");
    return 1;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

var login = await Program.LoginAsync(registry, username, token).ConfigureAwait(false);

if (login != 0)
{
    return login;
}

string[] references = [$"{image}:latest", $"{image}:{DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{sha[..Program.ShortShaLength]}"];

List<string> build = ["build"];

foreach (var reference in references)
{
    build.Add("-t");
    build.Add(reference);
}

build.Add(context);

if (Process.Run(Program.Docker, build) is { ExitCode: not 0 } built)
{
    return built.ExitCode;
}

foreach (var reference in references)
{
    if (Process.Run(Program.Docker, ["push", reference]) is { ExitCode: not 0 } push)
    {
        return push.ExitCode;
    }
}

Console.WriteLine($"Pushed {string.Join(" and ", references)}");

return 0;

/// <summary>Builds a container image and pushes it tagged latest and with the date and short commit.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The number of commit SHA characters in the dated tag.</summary>
    internal const int ShortShaLength = 7;

    /// <summary>The docker command.</summary>
    internal const string Docker = "docker";

    /// <summary>Logs docker in to the registry, passing the token on standard input.</summary>
    /// <param name="registry">The registry host.</param>
    /// <param name="username">The registry user.</param>
    /// <param name="token">The registry token.</param>
    /// <returns>The docker login exit code.</returns>
    internal static async Task<int> LoginAsync(string registry, string username, string token)
    {
        var start = new ProcessStartInfo(Docker) { RedirectStandardInput = true, UseShellExecute = false };
        start.ArgumentList.Add("login");
        start.ArgumentList.Add(registry);
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(username);
        start.ArgumentList.Add("--password-stdin");

        using var docker = Process.Start(start);
        if (docker is null)
        {
            Console.WriteLine("::error::Could not start docker.");
            return 1;
        }

        await docker.StandardInput.WriteLineAsync(token.AsMemory()).ConfigureAwait(false);
        docker.StandardInput.Close();
        var status = await docker.WaitForExitStatusAsync().ConfigureAwait(false);
        return status.ExitCode;
    }
}
