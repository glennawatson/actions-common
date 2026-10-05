#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;

Directory.SetCurrentDirectory(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

var app = Environment.GetEnvironmentVariable("SIGNING_APP") ?? throw new InvalidOperationException("SIGNING_APP is required.");

var kind = Environment.GetEnvironmentVariable("SIGNING_KIND") ?? throw new InvalidOperationException("SIGNING_KIND is required.");

var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };

start.ArgumentList.Add(Path.GetFullPath(app));

start.ArgumentList.Add("sign");

start.ArgumentList.Add(kind);

if (kind == "detached")
{
    start.ArgumentList.Add(Environment.GetEnvironmentVariable("CERTIFICATE_SOURCE") ?? throw new InvalidOperationException("CERTIFICATE_SOURCE is required."));
}

// Native providers read this environment in the child process, alongside the connected desktop session.
var module = Environment.GetEnvironmentVariable("SS_PKCS11");

if (!string.IsNullOrEmpty(module))
{
    start.Environment["PKCS11_MODULE_PATH"] = module;
}

Console.WriteLine($"[command]dotnet {string.Join(' ', start.ArgumentList)}");

using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the signing application.");

var status = await process.WaitForExitStatusAsync().ConfigureAwait(false);

return status.ExitCode;
