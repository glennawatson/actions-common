#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
#:include Window.cs
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Web;
using ActionsCommon.Signing;
using static System.Environment;
const int MaximumWindowAttempts = 30;

const int MinimumWindowWidth = 400;

const int MinimumWindowHeight = 300;

const int WindowPollSeconds = 2;

const int EmailFieldPercent = 39;

const int LoginButtonPercent = 76;

const int LoginWaitSeconds = 8;

const int CloseButtonPercent = 94;

const int CloseDialogWaitSeconds = 3;

const int TokenTimeoutSeconds = 60;

const int TimeoutExitCode = 124;

const int WindowMidpointDivisor = 2;

const int GeometryParts = 2;

const int PercentScale = 100;

const int BitsPerByte = 8;

const int DecimalRadix = 10;

const int Base32BitsPerCharacter = 5;

const int InvalidArgumentsExitCode = 2;

args = [System.Environment.GetEnvironmentVariable("LAUNCH_SECONDS") ?? string.Empty];

if (args is not [var launchSeconds])
{
    Console.WriteLine("::error::Expected the launch delay argument.");
    return InvalidArgumentsExitCode;
}

if ((GetEnvironmentVariable("CERTUM_USER_ID"), GetEnvironmentVariable("CERTUM_OTP_URI")) is not ({ Length: > 0 } userId, { Length: > 0 } otpUri))
{
    Console.WriteLine("::error::Map the CERTUM_USER_ID and CERTUM_OTP_URI secrets into the job env.");
    return 1;
}

var dist = GetEnvironmentVariable("SS_DIST")!;

var temp = GetEnvironmentVariable("RUNNER_TEMP")!;

var home = GetFolderPath(SpecialFolder.UserProfile);

// Without its config in $HOME and the logon-dialog preference, SimplySign shows a "Critical error" on launch.
if (Path.Combine(dist, "SimplySignDesktop.xml") is var config && File.Exists(config))
{
    File.Copy(config, Path.Combine(home, "SimplySignDesktop.xml"), overwrite: true);
}

_ = Directory.CreateDirectory(Path.Combine(home, ".config"));

File.WriteAllLines(
    Path.Combine(home, ".config", "Unknown Organization.conf"),
    ["[General]", "CacheUserIdAtLogon=Yes", "ShowLogonDialogAfterApplicationStartup=Yes", "ShowLogonDialogWhenAnyAppRequestsAccess=Yes"]);

var log = Path.Combine(temp, "simplysign.log");

using (var output = File.OpenHandle(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
{
    // Launch via the *_start wrapper (sets LD_LIBRARY_PATH for the bundled Qt).
    var launch = new ProcessStartInfo(GetEnvironmentVariable("SS_START") is { Length: > 0 } start ? start : GetEnvironmentVariable("SS_EXE")!)
    {
        StartDetached = true,
        StandardOutputHandle = output,
        StandardErrorHandle = output,
    };

    // SimplySignDesktop segfaults before drawing the login window when $USER is unset, as it is in containers.
    launch.Environment["USER"] = GetEnvironmentVariable("USER") is { Length: > 0 } user ? user : "root";
    launch.Environment["LD_LIBRARY_PATH"] = $"{dist}:{GetEnvironmentVariable("LD_LIBRARY_PATH")}";
    _ = Process.StartAndForget(launch);
}

Thread.Sleep(TimeSpan.FromSeconds(int.Parse(launchSeconds, CultureInfo.InvariantCulture)));

var otp = Totp(otpUri);

Console.WriteLine($"::add-mask::{otp}");

Window? login = null;

for (var attempt = 1; attempt <= MaximumWindowAttempts; attempt++)
{
    if (LargestWindow() is { Width: >= MinimumWindowWidth, Height: >= MinimumWindowHeight } found)
    {
        login = found;
        Console.WriteLine($"login window {found.Id} {found.Width}x{found.Height} (attempt {attempt})");
        break;
    }

    Thread.Sleep(TimeSpan.FromSeconds(WindowPollSeconds));
}

if (login is not { } window)
{
    Console.WriteLine("::error::SimplySign login window did not appear");
    Console.WriteLine(File.ReadAllText(log));
    return 1;
}

Activate(window);

_ = XDoTool("windowraise", window.Id);

Thread.Sleep(TimeSpan.FromSeconds(1));

// Click E-MAIL field, type id; Tab; type OTP; click Login.
Click(window, EmailFieldPercent);

Thread.Sleep(TimeSpan.FromSeconds(1));

_ = XDoTool("type", "--clearmodifiers", "--delay", "50", userId);

_ = XDoTool("key", "Tab");

Thread.Sleep(TimeSpan.FromSeconds(1));

_ = XDoTool("type", "--clearmodifiers", "--delay", "50", otp);

Click(window, LoginButtonPercent);

Thread.Sleep(TimeSpan.FromSeconds(LoginWaitSeconds));

// The token only activates once the "Logon successful" dialog's Close button (bottom-centre) is clicked.
var dialog = LargestWindow() ?? window;

Activate(dialog);

Click(dialog, CloseButtonPercent);

Thread.Sleep(TimeSpan.FromSeconds(CloseDialogWaitSeconds));

// The slot only enumerates once the cloud session is up, so this fails fast on a bad login.
// It cannot show the certificate: this token lists none to pkcs11-tool even when signing works.
return Process.Run("pkcs11-tool", ["--module", GetEnvironmentVariable("SS_PKCS11")!, "-L"], timeout: TimeSpan.FromSeconds(TokenTimeoutSeconds)) switch
{
    { Canceled: true } => TimeoutExitCode,
    var slots => slots.ExitCode,
};

// Match on getwindowname (_NET_WM_NAME): `search --name PATTERN` only sees this app's empty WM_NAME.
static Window? LargestWindow()
{
    Window? largest = null;
    foreach (var id in Capture("search", "--name", string.Empty).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Capture("getwindowname", id).StandardOutput.Contains("implySign", StringComparison.Ordinal))
        {
            continue;
        }

        Dictionary<string, int> geometry = [with(StringComparer.Ordinal)];
        foreach (var line in Capture("getwindowgeometry", "--shell", id).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Split('=', GeometryParts) is [var name, var value] && int.TryParse(value, CultureInfo.InvariantCulture, out var number))
            {
                geometry[name] = number;
            }
        }

        var candidate = new Window(id, geometry.GetValueOrDefault("X"), geometry.GetValueOrDefault("Y"), geometry.GetValueOrDefault("WIDTH"), geometry.GetValueOrDefault("HEIGHT"));
        if (candidate.Area > (largest?.Area ?? 0))
        {
            largest = candidate;
        }
    }

    return largest;
}

static void Activate(Window window)
{
    if (XDoTool("windowactivate", "--sync", window.Id) is not 0)
    {
        _ = XDoTool("windowactivate", window.Id);
    }
}

static void Click(Window window, int percentDown) =>
    XDoTool("mousemove", $"{window.X + (window.Width / WindowMidpointDivisor)}", $"{window.Y + (window.Height * percentDown / PercentScale)}", "click", "1");

static int XDoTool(params IEnumerable<string> arguments) => Process.Run("xdotool", arguments).ExitCode;

static ProcessTextOutput Capture(params IEnumerable<string> arguments) => Process.RunAndCaptureText("xdotool", arguments);

// RFC 6238 TOTP from the otpauth URI, honouring its algorithm, digits and period.
[SuppressMessage("Security", "CA5350", Justification = "RFC 6238 requires HMAC-SHA1 for SHA1 otpauth tokens.")]
static string Totp(string uri)
{
    var query = HttpUtility.ParseQueryString(new Uri(uri).Query);
    var key = Base32(query["secret"]!);
    var digits = int.Parse(query["digits"] ?? "6", CultureInfo.InvariantCulture);
    var period = long.Parse(query["period"] ?? "30", CultureInfo.InvariantCulture);

    var counter = new byte[sizeof(long)];
    BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / period);

    var hash = (query["algorithm"] ?? "SHA1").ToUpperInvariant() switch
    {
        "SHA1" => HMACSHA1.HashData(key, counter),
        "SHA256" => HMACSHA256.HashData(key, counter),
        "SHA512" => HMACSHA512.HashData(key, counter),
        var algorithm => throw new NotSupportedException($"Unsupported TOTP algorithm {algorithm}."),
    };

    var offset = hash[^1] & 0xF;
    var code = (BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF) % (int)Math.Pow(DecimalRadix, digits);
    return code.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
}

static byte[] Base32(string text)
{
    const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    List<byte> bytes = [with(capacity: text.Length * Base32BitsPerCharacter / BitsPerByte)];
    var buffer = 0;

    var bits = 0;

    foreach (var character in text.TrimEnd('=').ToUpperInvariant())
    {
        buffer = ((buffer << Base32BitsPerCharacter) | alphabet.IndexOf(character)) & 0xFFFF;
        bits += Base32BitsPerCharacter;
        if (bits < BitsPerByte)
        {
            continue;
        }

        bits -= BitsPerByte;
        bytes.Add((byte)(buffer >> bits));
    }

    return [.. bytes];
}
