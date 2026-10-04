#!/usr/bin/env dotnet
#:package YamlDotNet@18.1.0
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using YamlDotNet.RepresentationModel;

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("Run verification on Linux.");
    return 2;
}

var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();
var scratch = Path.Combine(Path.GetTempPath(), "shared-actions-verify-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var references = 0;
foreach (var file in Directory.GetFiles(Path.Combine(root, ".github"), "*.yml", SearchOption.AllDirectories))
{
    var yaml = Read(file);
    foreach (var map in Maps(yaml))
    {
        var reference = Value(map, "uses");
        const string prefix = "glennawatson/actions-common/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal)) continue;
        var path = reference[prefix.Length..].Split('@')[0];
        var target = Path.Combine(root, path);
        if (!path.StartsWith(".github/workflows/", StringComparison.Ordinal)) target = Path.Combine(target, "action.yml");
        var contract = Read(target);
        if (path.StartsWith(".github/workflows/", StringComparison.Ordinal)) contract = Child(Child(contract, "on"), "workflow_call");
        var inputs = Child(contract, "inputs");
        var supplied = Child(map, "with");
        foreach (var key in supplied.Children.Keys)
            if (!inputs.Children.ContainsKey(key)) throw new InvalidOperationException($"Unknown input {key} in {file}.");
        foreach (var input in inputs.Children)
            if (Value((YamlMappingNode)input.Value, "required") == "true" && !supplied.Children.ContainsKey(input.Key))
                throw new InvalidOperationException($"Missing input {input.Key} in {file}.");
        references++;
    }
}

var scripts = Directory.GetFiles(Path.Combine(root, ".github"), "*.cs", SearchOption.AllDirectories);
for (var index = 0; index < scripts.Length; index++)
{
    var source = File.ReadAllText(scripts[index]);
    source = source.Insert(source.IndexOf('\n') + 1, "#:property TreatWarningsAsErrors=true\n");
    var target = Path.Combine(scratch, $"script-{index}.cs");
    File.WriteAllText(target, source);
    var build = Process.RunAndCaptureText("dotnet", ["build", target]);
    if (build.ExitStatus.ExitCode != 0)
    {
        Console.Error.WriteLine(build.StandardOutput + build.StandardError);
        return build.ExitStatus.ExitCode;
    }
}

const string version = "1.7.12";
const string checksum = "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8";
using var client = new HttpClient();
var archive = await client.GetByteArrayAsync($"https://github.com/rhysd/actionlint/releases/download/v{version}/actionlint_{version}_linux_amd64.tar.gz");
if (!string.Equals(Convert.ToHexString(SHA256.HashData(archive)), checksum, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The workflow checker checksum does not match.");
using var compressed = new MemoryStream(archive);
using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
using var tar = new TarReader(gzip);
var checker = Path.Combine(scratch, "actionlint");
while (tar.GetNextEntry() is { } entry)
    if (entry.Name == "actionlint") entry.ExtractToFile(checker, overwrite: false);
File.SetUnixFileMode(checker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var workflows = Directory.GetFiles(Path.Combine(root, ".github/workflows"), "*.yml");
var lint = Process.Run(checker, ["-shellcheck=", "-pyflakes=", .. workflows]);
if (lint.ExitCode != 0) return lint.ExitCode;
var runner = Process.Run("dotnet", ["run", "--file", Path.Combine(root, "tools", "VerifyRunner.cs")]);
if (runner.ExitCode != 0) return runner.ExitCode;
Console.WriteLine($"Checked {references} shared references. Compiled {scripts.Length} C# files without warnings. Checked {workflows.Length} workflows.");
return 0;

static YamlMappingNode Read(string file)
{
    var yaml = new YamlStream();
    yaml.Load(new StringReader(File.ReadAllText(file)));
    return (YamlMappingNode)yaml.Documents[0].RootNode;
}
static YamlMappingNode Child(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlMappingNode child ? child : new YamlMappingNode();
static string Value(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value.ToString() : "";
static IEnumerable<YamlMappingNode> Maps(YamlNode node)
{
    if (node is YamlMappingNode map)
    {
        yield return map;
        foreach (var child in map.Children.Values)
            foreach (var item in Maps(child)) yield return item;
    }
    if (node is YamlSequenceNode sequence)
        foreach (var child in sequence.Children)
            foreach (var item in Maps(child)) yield return item;
}
