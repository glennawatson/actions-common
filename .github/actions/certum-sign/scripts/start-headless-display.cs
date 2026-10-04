#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;

var temp = GetEnvironmentVariable("RUNNER_TEMP")!;
var home = GetFolderPath(SpecialFolder.UserProfile);

StartInBackground(new ProcessStartInfo("Xvfb", [":99", "-screen", "0", "1280x1024x24"]), Path.Combine(temp, "xvfb.log"));
Thread.Sleep(TimeSpan.FromSeconds(3));

// Without a rootCommand fluxbox runs fbsetbg, whose missing-wallpaper xmessage steals focus from the login form.
Directory.CreateDirectory(Path.Combine(home, ".fluxbox"));
File.WriteAllText(Path.Combine(home, ".fluxbox", "init"), "session.screen0.rootCommand: /bin/true\n");

var fluxbox = new ProcessStartInfo("fluxbox");
fluxbox.Environment["DISPLAY"] = ":99";
StartInBackground(fluxbox, Path.Combine(temp, "fluxbox.log"));
Thread.Sleep(TimeSpan.FromSeconds(2));

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
    Process.StartAndForget(startInfo);
}
