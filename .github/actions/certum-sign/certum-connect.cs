// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

#:package Pkcs11Interop

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Web;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using static System.Environment;

if (args is not [var launchSeconds])
{
    Console.WriteLine("::error::Expected the launch timeout argument.");
    return Program.UsageError;
}

if ((GetEnvironmentVariable("CERTUM_USER_ID"), GetEnvironmentVariable("CERTUM_OTP_URI")) is not ({ Length: > 0 } userId, { Length: > 0 } otpUri))
{
    Console.WriteLine("::error::Map the CERTUM_USER_ID and CERTUM_OTP_URI secrets into the job env.");
    return 1;
}

if (GetEnvironmentVariable("SS_DIST") is not { Length: > 0 } dist)
{
    Console.WriteLine("::error::The prebuilt certum-signer image must set SS_DIST.");
    return 1;
}

var paths = await Program.ResolvePathsAsync(dist, CancellationToken.None).ConfigureAwait(false);

if (paths.Module.Length is 0)
{
    Console.WriteLine($"::error::No SimplySign PKCS#11 module found under {dist}.");
    return 1;
}

if (!await Program.StartDisplayAsync(CancellationToken.None).ConfigureAwait(false))
{
    Console.WriteLine("::error::X display :99 or its window manager did not start.");
    return 1;
}

Console.WriteLine("X display :99 up");

var log = Program.LaunchSimplySign(dist, paths.Start.Length > 0 ? paths.Start : paths.Executable);

var launchTimeout = TimeSpan.FromSeconds(int.Parse(launchSeconds, CultureInfo.InvariantCulture));

if (!await Program.LogInAsync(userId, otpUri, launchTimeout, CancellationToken.None).ConfigureAwait(false))
{
    Console.WriteLine("::error::SimplySign login window did not appear");
    Console.WriteLine(await File.ReadAllTextAsync(log).ConfigureAwait(false));
    return 1;
}

return await Program.WaitForTokenAsync(paths.Module, CancellationToken.None).ConfigureAwait(false);

/// <summary>Starts SimplySign Desktop on a headless display, logs in to the Certum cloud token and exports $SS_PKCS11.</summary>
internal static partial class Program
{
    /// <summary>The exit code for bad arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The headless X display.</summary>
    private const string Display = ":99";

    /// <summary>The environment variable that selects the X display.</summary>
    private const string DisplayVariable = "DISPLAY";

    /// <summary>The environment variable naming the runner's temporary folder.</summary>
    private const string RunnerTemp = "RUNNER_TEMP";

    /// <summary>The SimplySign log's file name.</summary>
    private const string SimplySignLog = "simplysign.log";

    /// <summary>The X automation tool that finds, focuses and clicks SimplySign's windows.</summary>
    private const string XDoTool = "xdotool";

    /// <summary>Splits a <c>NAME=value</c> line into its two parts.</summary>
    private const int NameAndValue = 2;

    /// <summary>The smallest login window width; smaller SimplySign windows are splash or tray windows.</summary>
    private const int LoginWidth = 400;

    /// <summary>The smallest login window height.</summary>
    private const int LoginHeight = 300;

    /// <summary>The e-mail field, as a percentage of the login window height.</summary>
    private const int EmailFieldPercent = 45;

    /// <summary>The one-time code field, as a percentage of the login window height.</summary>
    private const int CodeFieldPercent = 55;

    /// <summary>The one-time code field, as a percentage of the login window width; the code's boxes sit at the left.</summary>
    private const int CodeFieldAcrossPercent = 20;

    /// <summary>The Sign In button, as a percentage of the login window height.</summary>
    private const int SignInButtonPercent = 72;

    /// <summary>The Sign In button, as a percentage of the login window width; it sits at the right.</summary>
    private const int SignInButtonAcrossPercent = 90;

    /// <summary>The horizontal centre, as a percentage of the window width.</summary>
    private const int CentrePercent = 50;

    /// <summary>The whole width or height, for percentages.</summary>
    private const int Percent = 100;

    /// <summary>The typing delay in milliseconds, which the login form needs to register every key.</summary>
    private const string TypingDelay = "50";

    /// <summary>How often a condition is checked.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest wait for the X server and window manager.</summary>
    private static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait for the login to answer with the "Logon successful" dialog, and for a window to take focus.</summary>
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait for the token's slot once the dialog is closed.</summary>
    private static readonly TimeSpan TokenTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long the login form needs after its window maps before it takes input.</summary>
    private static readonly TimeSpan FormReadyDelay = TimeSpan.FromSeconds(12);

    /// <summary>How long the form needs to act on a click or key before the next one.</summary>
    private static readonly TimeSpan InputSettleDelay = TimeSpan.FromSeconds(1);

    /// <summary>How long the cloud login takes before the success dialog is drawn.</summary>
    private static readonly TimeSpan CloudLoginDelay = TimeSpan.FromSeconds(8);

    /// <summary>How often the screen is snapshotted while the cloud login runs.</summary>
    private static readonly TimeSpan SignInSnapshotInterval = TimeSpan.FromSeconds(3);

    /// <summary>How long SimplySign needs after the dialog closes to open the cloud token.</summary>
    private static readonly TimeSpan CloseSettleDelay = TimeSpan.FromSeconds(3);

    /// <summary>Where snapshots of the screen and windows go, or null when snapshots are off.</summary>
    private static readonly string? SnapshotFolder = GetEnvironmentVariable("CERTUM_SNAPSHOT_DIR") is { Length: > 0 } folder ? folder : null;

    /// <summary>Where Xvfb keeps its screen file while snapshots are on.</summary>
    private static readonly string FrameBufferFolder = Path.Combine(GetEnvironmentVariable(RunnerTemp) ?? Path.GetTempPath(), "xvfb-screen");

    /// <summary>The number of the next snapshot, so they sort in the order taken.</summary>
    private static int _snapshot;

    /// <summary>Copies the screen, the SimplySign windows and the SimplySign log into the snapshot folder, when snapshots are on.</summary>
    /// <param name="name">What the snapshot shows.</param>
    /// <param name="cancellationToken">Stops xdotool.</param>
    /// <returns>A task that completes when the snapshot is written.</returns>
    internal static async Task SnapshotAsync(string name, CancellationToken cancellationToken)
    {
        if (SnapshotFolder is null)
        {
            return;
        }

        _ = Directory.CreateDirectory(SnapshotFolder);
        var prefix = Path.Combine(SnapshotFolder, $"{_snapshot:D2}-{name}");
        _snapshot++;
        if (Path.Combine(FrameBufferFolder, "Xvfb_screen0") is var screen && File.Exists(screen))
        {
            File.Copy(screen, $"{prefix}.xwd", overwrite: true);
        }

        List<string> windows = [];
        foreach (var id in Lines(await CaptureAsync(cancellationToken, "search", "--name", string.Empty).ConfigureAwait(false)))
        {
            var title = (await CaptureAsync(cancellationToken, "getwindowname", id).ConfigureAwait(false)).StandardOutput.Trim();
            var size = Geometry(id, await CaptureAsync(cancellationToken, "getwindowgeometry", "--shell", id).ConfigureAwait(false));
            windows.Add($"{id}\t{size.X},{size.Y}\t{size.Width}x{size.Height}\t{title}");
        }

        var active = (await CaptureAsync(cancellationToken, "getactivewindow").ConfigureAwait(false)).StandardOutput.Trim();
        await File.WriteAllLinesAsync($"{prefix}.txt", [$"active {active}", .. windows], cancellationToken).ConfigureAwait(false);
        if (Path.Combine(GetEnvironmentVariable(RunnerTemp) ?? Path.GetTempPath(), SimplySignLog) is var log && File.Exists(log))
        {
            File.Copy(log, Path.Combine(SnapshotFolder, SimplySignLog), overwrite: true);
        }

        Console.WriteLine($"snapshot {Path.GetFileName(prefix)}");
    }

    /// <summary>Starts Xvfb, then fluxbox once the display answers, then waits for fluxbox to manage it.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>True when the display and window manager are up.</returns>
    internal static async Task<bool> StartDisplayAsync(CancellationToken cancellationToken)
    {
        var temp = GetEnvironmentVariable(RunnerTemp)!;
        var home = GetFolderPath(SpecialFolder.UserProfile);

        // With snapshots on, Xvfb keeps the screen in an XWD file that a snapshot copies.
        string[] xvfb = [Display, "-screen", "0", "1280x1024x24"];
        if (SnapshotFolder is not null)
        {
            _ = Directory.CreateDirectory(FrameBufferFolder);
            xvfb = [.. xvfb, "-fbdir", FrameBufferFolder];
        }

        StartInBackground(new("Xvfb", xvfb), Path.Combine(temp, "xvfb.log"));
        if (await PollAsync(static token => Succeeded(OnDisplay("xdpyinfo"), token), DisplayTimeout, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        // Without a rootCommand fluxbox runs fbsetbg, whose missing-wallpaper xmessage steals focus from the login form.
        _ = Directory.CreateDirectory(Path.Combine(home, ".fluxbox"));
        await File.WriteAllTextAsync(Path.Combine(home, ".fluxbox", "init"), "session.screen0.rootCommand: /bin/true\n", cancellationToken).ConfigureAwait(false);
        StartInBackground(OnDisplay("fluxbox"), Path.Combine(temp, "fluxbox.log"));

        // get_desktop reads _NET_CURRENT_DESKTOP, which only a running EWMH window manager sets.
        return await PollAsync(static token => Succeeded(OnDisplay(XDoTool, "get_desktop"), token), DisplayTimeout, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Finds the SimplySign launcher, executable and PKCS#11 module, and exports them to later steps.</summary>
    /// <param name="dist">The prebuilt SimplySign folder.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>The paths; a path is empty when no file matched.</returns>
    internal static async Task<SimplySignPaths> ResolvePathsAsync(string dist, CancellationToken cancellationToken)
    {
        var paths = new SimplySignPaths(Find(dist, "SimplySignDesktop_start"), Find(dist, "SimplySignDesktop"), Find(dist, "SimplySignPKCS*.so"));

        // Later steps, such as jsign's keystore and the RPM and checksum signers, load the module from $SS_PKCS11.
        if (GetEnvironmentVariable("GITHUB_ENV") is { Length: > 0 } environment)
        {
            await File.AppendAllLinesAsync(
                environment,
                [$"SS_START={paths.Start}", $"SS_EXE={paths.Executable}", $"SS_PKCS11={paths.Module}"],
                cancellationToken).ConfigureAwait(false);
        }

        return paths;
    }

    /// <summary>Configures and launches SimplySign Desktop, detached, logging to a file.</summary>
    /// <param name="dist">The prebuilt SimplySign folder.</param>
    /// <param name="program">The *_start wrapper, or the executable when there is no wrapper.</param>
    /// <returns>The log path.</returns>
    internal static string LaunchSimplySign(string dist, string program)
    {
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

        // Launch via the *_start wrapper (sets LD_LIBRARY_PATH for the bundled Qt).
        var launch = OnDisplay(program);

        // SimplySignDesktop segfaults before drawing the login window when $USER is unset, as it is in containers.
        launch.Environment["USER"] = GetEnvironmentVariable("USER") is { Length: > 0 } user ? user : "root";
        launch.Environment["LD_LIBRARY_PATH"] = $"{dist}:{GetEnvironmentVariable("LD_LIBRARY_PATH")}";
        var log = Path.Combine(GetEnvironmentVariable(RunnerTemp)!, SimplySignLog);
        StartInBackground(launch, log);
        return log;
    }

    /// <summary>Waits for the login window, types the user id and a fresh one-time password, and closes the success dialog.</summary>
    /// <param name="userId">The Certum user id.</param>
    /// <param name="otpUri">The otpauth URI holding the TOTP secret.</param>
    /// <param name="launchTimeout">The longest wait for the login window.</param>
    /// <param name="cancellationToken">Stops the waits.</param>
    /// <returns>False when the login window never appeared.</returns>
    internal static async Task<bool> LogInAsync(string userId, string otpUri, TimeSpan launchTimeout, CancellationToken cancellationToken)
    {
        if (await PollAsync(LoginWindowAsync, launchTimeout, cancellationToken).ConfigureAwait(false) is not { } window)
        {
            await SnapshotAsync("no-login-window", cancellationToken).ConfigureAwait(false);
            return false;
        }

        Console.WriteLine($"login window {window.Id} {window.Width}x{window.Height}");
        await SnapshotAsync("window-mapped", cancellationToken).ConfigureAwait(false);

        // The code is made once the window is up, so it is fresh when typed.
        var otp = Totp(otpUri, TimeProvider.System);
        Console.WriteLine($"::add-mask::{otp}");

        // SimplySign maps its window before the form takes input, and exposes no signal for when it does.
        await Task.Delay(FormReadyDelay, cancellationToken).ConfigureAwait(false);
        await SnapshotAsync("form-ready", cancellationToken).ConfigureAwait(false);
        await ActivateAsync(window, cancellationToken).ConfigureAwait(false);
        _ = await XDoToolAsync(cancellationToken, "windowraise", window.Id).ConfigureAwait(false);
        await Task.Delay(InputSettleDelay, cancellationToken).ConfigureAwait(false);

        // Click the e-mail field and type the id, click the one-time code field and type the code, then click Sign In.
        await ClickAsync(window, CentrePercent, EmailFieldPercent, cancellationToken).ConfigureAwait(false);
        await WaitForFocusAsync(window, cancellationToken).ConfigureAwait(false);
        await Task.Delay(InputSettleDelay, cancellationToken).ConfigureAwait(false);
        await SnapshotAsync("email-clicked", cancellationToken).ConfigureAwait(false);
        _ = await XDoToolAsync(cancellationToken, "type", "--clearmodifiers", "--delay", TypingDelay, userId).ConfigureAwait(false);
        await Task.Delay(InputSettleDelay, cancellationToken).ConfigureAwait(false);
        await ClickAsync(window, CodeFieldAcrossPercent, CodeFieldPercent, cancellationToken).ConfigureAwait(false);
        await Task.Delay(InputSettleDelay, cancellationToken).ConfigureAwait(false);
        await SnapshotAsync("code-clicked", cancellationToken).ConfigureAwait(false);
        _ = await XDoToolAsync(cancellationToken, "type", "--clearmodifiers", "--delay", TypingDelay, otp).ConfigureAwait(false);
        await Task.Delay(InputSettleDelay, cancellationToken).ConfigureAwait(false);
        await SnapshotAsync("code-typed", cancellationToken).ConfigureAwait(false);
        await ClickAsync(window, SignInButtonAcrossPercent, SignInButtonPercent, cancellationToken).ConfigureAwait(false);

        // The cloud login runs before the success dialog is drawn; these show how it looks and when it appears.
        for (var waited = TimeSpan.Zero; waited < CloudLoginDelay; waited += SignInSnapshotInterval)
        {
            await Task.Delay(SignInSnapshotInterval, cancellationToken).ConfigureAwait(false);
            await SnapshotAsync("after-sign-in", cancellationToken).ConfigureAwait(false);
        }

        // Return presses the success dialog's default button, wherever the new layout puts it.
        var dialog = await LargestWindowAsync(cancellationToken).ConfigureAwait(false) ?? window;
        await ActivateAsync(dialog, cancellationToken).ConfigureAwait(false);
        _ = await XDoToolAsync(cancellationToken, "key", "Return").ConfigureAwait(false);
        await Task.Delay(CloseSettleDelay, cancellationToken).ConfigureAwait(false);
        await SnapshotAsync("closed", cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Waits for the PKCS#11 module to list the token, which fails fast on a bad login.</summary>
    /// <param name="module">The PKCS#11 module.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>Zero when a slot holds the token.</returns>
    /// <remarks>The slot only enumerates once the cloud session is up. It cannot show the certificate: this token lists none even when signing works.</remarks>
    internal static async Task<int> WaitForTokenAsync(string module, CancellationToken cancellationToken)
    {
        // Pkcs11Interop imports dlopen from "libdl"; glibc 2.34 and later only ship that name in its development package.
        NativeLibrary.SetDllImportResolver(
            typeof(Pkcs11InteropFactories).Assembly,
            static (name, _, _) => string.Equals(name, "libdl", StringComparison.Ordinal) ? NativeLibrary.Load("libdl.so.2") : 0);

        // Each attempt initialises the module afresh, as a separate tool would; a module initialised before the cloud
        // session is up may keep reporting no token.
        var slots = await PollAsync(_ => Task.FromResult(ListTokens(module)), TokenTimeout, cancellationToken).ConfigureAwait(false);
        if (slots is null)
        {
            await SnapshotAsync("no-token", cancellationToken).ConfigureAwait(false);
            Console.WriteLine("::error::the PKCS#11 module lists no token; the login did not complete");
            return 1;
        }

        foreach (var slot in slots)
        {
            Console.WriteLine(slot);
        }

        return 0;
    }

    /// <summary>Computes the RFC 6238 TOTP for an otpauth URI, honouring its algorithm, digits and period.</summary>
    /// <param name="uri">The otpauth URI.</param>
    /// <param name="clock">The clock.</param>
    /// <returns>The one-time password.</returns>
    /// <exception cref="NotSupportedException">The URI names an algorithm TOTP does not define.</exception>
    internal static string Totp(string uri, TimeProvider clock)
    {
        const int MaskLow31Bits = 0x7FFFFFFF;
        const int LastNibble = 0xF;
        const int Decimal = 10;
        var query = HttpUtility.ParseQueryString(new Uri(uri).Query);
        var key = Base32(query["secret"]!);
        var digits = int.Parse(query["digits"] ?? "6", CultureInfo.InvariantCulture);
        var period = long.Parse(query["period"] ?? "30", CultureInfo.InvariantCulture);
        var algorithm = (query["algorithm"] ?? "SHA1").ToUpperInvariant() switch
        {
            "SHA1" => HashAlgorithmName.SHA1,
            "SHA256" => HashAlgorithmName.SHA256,
            "SHA512" => HashAlgorithmName.SHA512,
            var other => throw new NotSupportedException($"Unsupported TOTP algorithm {other}."),
        };

        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, clock.GetUtcNow().ToUnixTimeSeconds() / period);
        Span<byte> hash = stackalloc byte[SHA512.HashSizeInBytes];
        var length = CryptographicOperations.HmacData(algorithm, key, counter, hash);
        var offset = hash[length - 1] & LastNibble;
        var modulus = 1;
        for (var i = 0; i < digits; i++)
        {
            modulus *= Decimal;
        }

        var code = (BinaryPrimitives.ReadInt32BigEndian(hash[offset..]) & MaskLow31Bits) % modulus;
        return code.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>Decodes RFC 4648 base32, ignoring padding and case.</summary>
    /// <param name="text">The base32 text.</param>
    /// <returns>The bytes.</returns>
    internal static byte[] Base32(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        const int BitsPerCharacter = 5;
        const int BitsPerByte = 8;
        const int BufferMask = 0xFFFF;
        var trimmed = text.AsSpan().TrimEnd('=');
        var bytes = new byte[trimmed.Length * BitsPerCharacter / BitsPerByte];
        var buffer = 0;
        var bits = 0;
        var written = 0;
        foreach (var character in trimmed)
        {
            buffer = ((buffer << BitsPerCharacter) | Alphabet.IndexOf(char.ToUpperInvariant(character), StringComparison.Ordinal)) & BufferMask;
            bits += BitsPerCharacter;
            if (bits < BitsPerByte)
            {
                continue;
            }

            bits -= BitsPerByte;
            bytes[written] = (byte)(buffer >> bits);
            written++;
        }

        return bytes;
    }

    /// <summary>Checks a condition until it yields a value, the timeout passes or the caller cancels.</summary>
    /// <typeparam name="T">The value the condition yields.</typeparam>
    /// <param name="probe">Checks the condition once; null means not yet.</param>
    /// <param name="timeout">The longest wait.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>The value, or null when the timeout passed first.</returns>
    private static async Task<T?> PollAsync<T>(Func<CancellationToken, Task<T?>> probe, TimeSpan timeout, CancellationToken cancellationToken)
        where T : class
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                if (await probe(limit.Token).ConfigureAwait(false) is { } value)
                {
                    return value;
                }
            }
            while (await timer.WaitForNextTickAsync(limit.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout passed; the caller decides what that means.
        }

        return null;
    }

    /// <summary>Runs a process and reports whether it exited with zero.</summary>
    /// <param name="startInfo">The process.</param>
    /// <param name="cancellationToken">Stops the process.</param>
    /// <returns>The exit status on success, otherwise null.</returns>
    private static async Task<ProcessExitStatus?> Succeeded(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var discard = File.OpenNullHandle();
        startInfo.StandardOutputHandle = discard;
        startInfo.StandardErrorHandle = discard;
        var status = await Process.RunAsync(startInfo, cancellationToken).ConfigureAwait(false);
        return status is { ExitCode: 0 } ? status : null;
    }

    /// <summary>Finds the login window once SimplySign has drawn it.</summary>
    /// <param name="cancellationToken">Stops the search.</param>
    /// <returns>The login window, or null when it is not up yet.</returns>
    private static async Task<Window?> LoginWindowAsync(CancellationToken cancellationToken) =>
        await LargestWindowAsync(cancellationToken).ConfigureAwait(false) is { Width: >= LoginWidth, Height: >= LoginHeight } found ? found : null;

    /// <summary>Loads the PKCS#11 module and describes each slot that holds a token.</summary>
    /// <param name="module">The PKCS#11 module.</param>
    /// <returns>One line per token, or null when there is none.</returns>
    private static List<string>? ListTokens(string module)
    {
        var factories = new Pkcs11InteropFactories();
        using var library = factories.Pkcs11LibraryFactory.LoadPkcs11Library(factories, module, AppType.MultiThreaded);
        var slots = library.GetSlotList(SlotsType.WithTokenPresent);
        if (slots.Count == 0)
        {
            return null;
        }

        var tokens = new List<string>(slots.Count);
        foreach (var slot in slots)
        {
            tokens.Add($"slot {slot.SlotId}: {slot.GetSlotInfo().SlotDescription.Trim()}, token {slot.GetTokenInfo().Label.Trim()}");
        }

        return tokens;
    }

    /// <summary>Finds the largest SimplySign window.</summary>
    /// <param name="cancellationToken">Stops the search.</param>
    /// <returns>The window, or null when none is mapped.</returns>
    /// <remarks>Matches on getwindowname (_NET_WM_NAME): <c>search --name PATTERN</c> only sees this app's empty WM_NAME.</remarks>
    private static async Task<Window?> LargestWindowAsync(CancellationToken cancellationToken)
    {
        Window? largest = null;
        foreach (var id in Lines(await CaptureAsync(cancellationToken, "search", "--name", string.Empty).ConfigureAwait(false)))
        {
            if (!(await CaptureAsync(cancellationToken, "getwindowname", id).ConfigureAwait(false)).StandardOutput.Contains("implySign", StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = Geometry(id, await CaptureAsync(cancellationToken, "getwindowgeometry", "--shell", id).ConfigureAwait(false));
            if (candidate.Area > (largest?.Area ?? 0))
            {
                largest = candidate;
            }
        }

        return largest;
    }

    /// <summary>Parses a window's position and size from <c>getwindowgeometry --shell</c>.</summary>
    /// <param name="id">The window id.</param>
    /// <param name="output">The xdotool output.</param>
    /// <returns>The window.</returns>
    private static Window Geometry(string id, ProcessTextOutput output)
    {
        Dictionary<string, int> geometry = [with(StringComparer.Ordinal)];
        foreach (var line in Lines(output))
        {
            if (line.Split('=', NameAndValue) is [var name, var value] && int.TryParse(value, CultureInfo.InvariantCulture, out var number))
            {
                geometry[name] = number;
            }
        }

        return new(id, geometry.GetValueOrDefault("X"), geometry.GetValueOrDefault("Y"), geometry.GetValueOrDefault("WIDTH"), geometry.GetValueOrDefault("HEIGHT"));
    }

    /// <summary>Focuses a window, waiting for the window manager when it can.</summary>
    /// <param name="window">The window.</param>
    /// <param name="cancellationToken">Stops xdotool.</param>
    /// <returns>A task that completes when the window has focus or activation was tried twice.</returns>
    private static async Task ActivateAsync(Window window, CancellationToken cancellationToken)
    {
        if (await XDoToolAsync(cancellationToken, "windowactivate", "--sync", window.Id).ConfigureAwait(false) is not 0)
        {
            _ = await XDoToolAsync(cancellationToken, "windowactivate", window.Id).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until a window holds the keyboard focus, so typed keys reach it.</summary>
    /// <param name="window">The window.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>A task that completes when the window has focus or the wait times out.</returns>
    private static async Task WaitForFocusAsync(Window window, CancellationToken cancellationToken) =>
        _ = await PollAsync(token => FocusedAsync(window, token), DialogTimeout, cancellationToken).ConfigureAwait(false);

    /// <summary>Checks whether a window holds the keyboard focus.</summary>
    /// <param name="window">The window.</param>
    /// <param name="cancellationToken">Stops xdotool.</param>
    /// <returns>The window id when it has focus, otherwise null.</returns>
    private static async Task<string?> FocusedAsync(Window window, CancellationToken cancellationToken)
    {
        var active = (await CaptureAsync(cancellationToken, "getactivewindow").ConfigureAwait(false)).StandardOutput.Trim();
        return string.Equals(active, window.Id, StringComparison.Ordinal) ? active : null;
    }

    /// <summary>Clicks a window at a position given as percentages of its width and height.</summary>
    /// <param name="window">The window.</param>
    /// <param name="percentAcross">How far across the window to click.</param>
    /// <param name="percentDown">How far down the window to click.</param>
    /// <param name="cancellationToken">Stops xdotool.</param>
    /// <returns>A task that completes when the click is sent.</returns>
    private static async Task ClickAsync(Window window, int percentAcross, int percentDown, CancellationToken cancellationToken) =>
        _ = await XDoToolAsync(
            cancellationToken,
            "mousemove",
            $"{window.X + (window.Width * percentAcross / Percent)}",
            $"{window.Y + (window.Height * percentDown / Percent)}",
            "click",
            "1").ConfigureAwait(false);

    /// <summary>Runs xdotool on the headless display.</summary>
    /// <param name="cancellationToken">Stops xdotool.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> XDoToolAsync(CancellationToken cancellationToken, params string[] arguments) =>
        (await Process.RunAsync(OnDisplay(XDoTool, arguments), cancellationToken).ConfigureAwait(false)).ExitCode;

    /// <summary>Runs xdotool on the headless display and captures its output.</summary>
    /// <param name="cancellationToken">Stops xdotool.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The output.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Task<ProcessTextOutput> CaptureAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        // RunAndCaptureTextAsync with a ProcessStartInfo throws unless both streams are redirected.
        var start = OnDisplay(XDoTool, arguments);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        return Process.RunAndCaptureTextAsync(start, cancellationToken);
    }

    /// <summary>Makes the start info for a program on the headless display.</summary>
    /// <param name="fileName">The program.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The start info.</returns>
    private static ProcessStartInfo OnDisplay(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName, arguments);
        start.Environment[DisplayVariable] = Display;
        return start;
    }

    /// <summary>Splits captured output into trimmed, non-empty lines.</summary>
    /// <param name="output">The output.</param>
    /// <returns>The lines.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string[] Lines(ProcessTextOutput output) => output.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Starts a process that outlives this script, writing to its own log.</summary>
    /// <param name="startInfo">The process.</param>
    /// <param name="log">The log path.</param>
    private static void StartInBackground(ProcessStartInfo startInfo, string log)
    {
        using var output = File.OpenHandle(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        startInfo.StartDetached = true;
        startInfo.StandardOutputHandle = output;
        startInfo.StandardErrorHandle = output;
        _ = Process.StartAndForget(startInfo);
    }

    /// <summary>Finds the first file below a folder whose name matches a pattern, in ordinal path order.</summary>
    /// <param name="folder">The folder to search.</param>
    /// <param name="pattern">The file name pattern.</param>
    /// <returns>The path, or empty when nothing matched.</returns>
    private static string Find(string folder, string pattern)
    {
        var files = Directory.GetFiles(folder, pattern, new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 });
        Array.Sort(files, StringComparer.Ordinal);
        return files is [var first, ..] ? first : string.Empty;
    }

    /// <summary>The SimplySign files the prebuilt image holds.</summary>
    /// <param name="Start">The *_start wrapper, which sets the library path for the bundled Qt.</param>
    /// <param name="Executable">The SimplySign Desktop executable.</param>
    /// <param name="Module">The PKCS#11 module.</param>
    internal sealed record SimplySignPaths(string Start, string Executable, string Module);

    /// <summary>A mapped X window.</summary>
    /// <param name="Id">The window id.</param>
    /// <param name="X">The left edge.</param>
    /// <param name="Y">The top edge.</param>
    /// <param name="Width">The width.</param>
    /// <param name="Height">The height.</param>
    private sealed record Window(string Id, int X, int Y, int Width, int Height)
    {
        /// <summary>Gets the window area.</summary>
        public int Area => Width * Height;
    }
}
