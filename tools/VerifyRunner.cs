#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
#:package YamlDotNet
using System.Diagnostics;
using YamlDotNet.RepresentationModel;
const string ScriptFile = "SCRIPT_FILE";

const string ExpectedArguments = "one value\nsemi;colon\nliteral $(value)\nliteral $(value)\n";

const int FixtureFailureExitCode = 7;

var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();

var action = Path.Combine(root, ".github", "actions", "run-csharp");

var scratch = Path.Combine(Path.GetTempPath(), $"shared-runner-{Guid.NewGuid():N}");

_ = Directory.CreateDirectory(scratch);

try
{
    var launcher = Path.Combine(scratch, "runner.sh");
    File.WriteAllText(launcher, $"#!/usr/bin/env dotnet\n#:include {Path.Combine(action, "Run.cs")}\n");
    var fixture = Path.Combine(scratch, "argument-fixture.cs");
    File.WriteAllText(fixture, "#!/usr/bin/env dotnet\nif (args is [\"exit7\"]) return 7;\nforeach (var value in args) Console.WriteLine(value);\nreturn 0;\n");
    var envFile = Path.Combine(scratch, "github.env");
    var environment = Run(
    new() { [ScriptFile] = Path.Combine(root, ".github/actions/dotnet-environment/scripts/configure--net-cli-environment.cs"), ["GITHUB_ENV"] = envFile, });
    Check(
    environment.ExitStatus.ExitCode == 0 && File.ReadAllText(envFile).Contains("DOTNET_CLI_TELEMETRY_OPTOUT=1", StringComparison.Ordinal),
    "Run a file from a remote action folder");
    var arguments = Run(new()
    {
        [ScriptFile] = Path.Combine(root, ".github/actions/run-command/Run.cs"),
        ["COMMAND_FILE"] = "dotnet",
        ["COMMAND_ARGUMENTS"] = $"run\n--file\n{fixture}\n--\none value\nsemi;colon\n$PROBE_VALUE\n${{PROBE_VALUE}}",
        ["COMMAND_DIRECTORY"] = ".",
        ["PROBE_VALUE"] = "literal $(value)",
    });
    Check(
    arguments.ExitStatus.ExitCode == 0 && arguments.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).EndsWith(ExpectedArguments, StringComparison.Ordinal),
    "Keep spaces and shell characters in arguments");
    var failure = Run(new() { [ScriptFile] = fixture, ["SCRIPT_ARGUMENTS"] = "exit7" });
    Check(failure.ExitStatus.ExitCode == FixtureFailureExitCode, "Return the child exit code");
    var output = Path.Combine(scratch, "shared.output");
    var shared = Run(new() { ["SHARED_SCRIPT"] = "resolve-built-commit-sha-c775df1621.cs", ["GITHUB_OUTPUT"] = output, });
    Check(
    shared.ExitStatus.ExitCode == 0 && File.ReadAllText(output).StartsWith("sha=", StringComparison.Ordinal),
    "Resolve a shared script from the action folder");

    var yaml = new YamlStream();
    using var actionReader = File.OpenText(Path.Combine(action, "action.yml"));
    yaml.Load(actionReader);
    var contract = (YamlMappingNode)yaml.Documents[0].RootNode;
    var outputs = contract.Children.TryGetValue(new YamlScalarNode("outputs"), out var declared) ? (YamlMappingNode)declared : new YamlMappingNode();
    string[] names = ["sha", "version", "tag", "prerelease", "SemVer2", "SimpleVersion", "NuGetPackageVersion", "GitCommitId", "VersionHeight"];
    var steps = (YamlSequenceNode)((YamlMappingNode)contract.Children[new YamlScalarNode("runs")]).Children[new YamlScalarNode("steps")];
    var hasRunStep = false;
    foreach (var step in steps.Children)
    {
        if (step is not YamlMappingNode map || !map.Children.TryGetValue(new YamlScalarNode("id"), out var id) || id.ToString() != "run")
        {
            continue;
        }

        hasRunStep = true;
        break;
    }

    var exposesOutputs = true;
    foreach (var name in names)
    {
        exposesOutputs &= outputs.Children.TryGetValue(new YamlScalarNode(name), out var value)
            && ((YamlMappingNode)value).Children[new YamlScalarNode("value")].ToString() == $"${{{{ steps.run.outputs.{name} }}}}";
    }

    Check(hasRunStep && exposesOutputs, "Expose script outputs through the composite action");
    foreach (var version in new[] { "2.3.4", "2.3.4-rc.2+build.7" })
    {
        var versionOutput = Path.Combine(scratch, $"{version}.output");
        var result = Run(new()
        {
            [ScriptFile] = Path.Combine(root, ".github/actions/minver/scripts/compute-minver-version-and-export-minver-----github-output.cs"),
            ["VERSION_OVERRIDE"] = version,
            ["GITHUB_ENV"] = envFile,
            ["GITHUB_OUTPUT"] = versionOutput,
        });
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(versionOutput))
        {
            var separator = line.IndexOf('=');
            values.Add(line[..separator], line[(separator + 1)..]);
        }

        Check(
            result.ExitStatus.ExitCode == 0 && values["SemVer2"] == version && values["SimpleVersion"] == "2.3.4"
            && values["NuGetPackageVersion"] == version.Split('+')[0] && values.ContainsKey("GitCommitId") && values.ContainsKey("VersionHeight"),
            $"Preserve MinVer output values for {version}");
    }

    Console.WriteLine("7 runner checks passed.");
    return 0;

    ProcessTextOutput Run(Dictionary<string, string> variables)
    {
        var start = new ProcessStartInfo("dotnet", ["run", "--file", launcher]) { WorkingDirectory = scratch, RedirectStandardOutput = true, RedirectStandardError = true, };
        start.Environment["GITHUB_WORKSPACE"] = root;
        start.Environment["ACTION_PATH"] = action;
        start.Environment[ScriptFile] = string.Empty;
        start.Environment["SHARED_SCRIPT"] = string.Empty;
        start.Environment["SCRIPT_ARGUMENTS"] = string.Empty;
        foreach (var item in variables)
        {
            start.Environment[item.Key] = item.Value;
        }

        var result = Process.RunAndCaptureText(start);
        if (result.ExitStatus.ExitCode is not (0 or FixtureFailureExitCode))
        {
            Console.Error.WriteLine(result.StandardOutput + result.StandardError);
        }

        return result;
    }
}
finally
{
    Directory.Delete(scratch, recursive: true);
}

static void Check(bool success, string name)
{
    if (!success)
    {
        throw new InvalidOperationException(name);
    }

    Console.WriteLine($"Passed: {name}");
}
