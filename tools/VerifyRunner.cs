#!/usr/bin/env dotnet
using System.Diagnostics;

var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();
var action = Path.Combine(root, ".github", "actions", "run-csharp");
var scratch = Path.Combine(Path.GetTempPath(), "shared-runner-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var launcher = Path.Combine(scratch, "runner.sh");
    File.WriteAllText(launcher, $"#!/usr/bin/env dotnet\n#:include {Path.Combine(action, "Run.cs")}\n");
    var fixture = Path.Combine(scratch, "argument-fixture.cs");
    File.WriteAllText(fixture, "#!/usr/bin/env dotnet\nif (args is [\"exit7\"]) return 7;\nforeach (var value in args) Console.WriteLine(value);\nreturn 0;\n");
    var envFile = Path.Combine(scratch, "github.env");
    var environment = Run(new()
    {
        ["SCRIPT_FILE"] = Path.Combine(root, ".github/actions/dotnet-environment/scripts/configure--net-cli-environment.cs"),
        ["GITHUB_ENV"] = envFile,
    });
    Check(environment.ExitStatus.ExitCode == 0 && File.ReadAllText(envFile).Contains("DOTNET_CLI_TELEMETRY_OPTOUT=1", StringComparison.Ordinal), "Run a file from a remote action folder");
    var arguments = Run(new()
    {
        ["SCRIPT_FILE"] = Path.Combine(root, ".github/actions/run-command/Run.cs"),
        ["COMMAND_FILE"] = "dotnet",
        ["COMMAND_ARGUMENTS"] = $"run\n--file\n{fixture}\n--\none value\nsemi;colon\n$PROBE_VALUE\n${{PROBE_VALUE}}",
        ["COMMAND_DIRECTORY"] = ".",
        ["PROBE_VALUE"] = "literal $(value)",
    });
    Check(arguments.ExitStatus.ExitCode == 0 && arguments.StandardOutput.Replace("\r", "", StringComparison.Ordinal).EndsWith("one value\nsemi;colon\nliteral $(value)\nliteral $(value)\n", StringComparison.Ordinal), "Keep spaces and shell characters in arguments");
    var failure = Run(new() { ["SCRIPT_FILE"] = fixture, ["SCRIPT_ARGUMENTS"] = "exit7" });
    Check(failure.ExitStatus.ExitCode == 7, "Return the child exit code");
    var output = Path.Combine(scratch, "shared.output");
    var shared = Run(new()
    {
        ["SHARED_SCRIPT"] = "resolve-built-commit-sha-c775df1621.cs",
        ["GITHUB_OUTPUT"] = output,
    });
    Check(shared.ExitStatus.ExitCode == 0 && File.ReadAllText(output).StartsWith("sha=", StringComparison.Ordinal), "Resolve a shared script from the action folder");
    Console.WriteLine("4 runner checks passed.");
    return 0;

    ProcessTextOutput Run(Dictionary<string, string> variables)
    {
        var start = new ProcessStartInfo("dotnet", ["run", "--file", launcher])
        {
            WorkingDirectory = scratch,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["GITHUB_WORKSPACE"] = root;
        start.Environment["ACTION_PATH"] = action;
        start.Environment["SCRIPT_FILE"] = "";
        start.Environment["SHARED_SCRIPT"] = "";
        start.Environment["SCRIPT_ARGUMENTS"] = "";
        foreach (var item in variables) start.Environment[item.Key] = item.Value;
        var result = Process.RunAndCaptureText(start);
        if (result.ExitStatus.ExitCode is not (0 or 7)) Console.Error.WriteLine(result.StandardOutput + result.StandardError);
        return result;
    }
}
finally
{
    Directory.Delete(scratch, recursive: true);
}

static void Check(bool success, string name)
{
    if (!success) throw new InvalidOperationException(name);
    Console.WriteLine("Passed: " + name);
}
