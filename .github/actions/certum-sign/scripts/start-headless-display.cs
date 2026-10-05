#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using static System.Environment;
const int DisplayStartupSeconds = 3;

const int WindowManagerStartupSeconds = 2;

var temp = GetEnvironmentVariable("RUNNER_TEMP")!;

var home = GetFolderPath(SpecialFolder.UserProfile);

StartInBackground(new("Xvfb", [":99", "-screen", "0", "1280x1024x24"]), Path.Combine(temp, "xvfb.log"));

Thread.Sleep(TimeSpan.FromSeconds(DisplayStartupSeconds));

// Without a rootCommand fluxbox runs fbsetbg, whose missing-wallpaper xmessage steals focus from the login form.
_ = Directory.CreateDirectory(Path.Combine(home, ".fluxbox"));

File.WriteAllText(Path.Combine(home, ".fluxbox", "init"), "session.screen0.rootCommand: /bin/true\n");

var fluxbox = new ProcessStartInfo("fluxbox");

fluxbox.Environment["DISPLAY"] = ":99";

StartInBackground(fluxbox, Path.Combine(temp, "fluxbox.log"));

Thread.Sleep(TimeSpan.FromSeconds(WindowManagerStartupSeconds));

using var discard = File.OpenNullHandle();

var probe = new ProcessStartInfo("xdpyinfo") { StandardOutputHandle = discard };

probe.Environment["DISPLAY"] = ":99";

if (Process.Run(probe) is { ExitCode: not 0 })
{
    Console.WriteLine("::error::X display :99 did not start.");
    return 1;
}

Console.WriteLine("X display :99 up");

return 0;

// The X server and window manager outlive this step, writing to their own logs.
static void StartInBackground(ProcessStartInfo startInfo, string log)
{
    using var output = File.OpenHandle(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    startInfo.StartDetached = true;
    startInfo.StandardOutputHandle = output;
    startInfo.StandardErrorHandle = output;
    _ = Process.StartAndForget(startInfo);
}
