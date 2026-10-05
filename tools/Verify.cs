#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
#:package YamlDotNet
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using YamlDotNet.RepresentationModel;
const int InvalidArgumentsExitCode = 2;

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("Run verification on Linux.");
    return InvalidArgumentsExitCode;
}

var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();

var scratch = Path.Combine(Path.GetTempPath(), $"shared-actions-verify-{Guid.NewGuid():N}");

_ = Directory.CreateDirectory(scratch);

var references = 0;

foreach (var file in Directory.GetFiles(Path.Combine(root, ".github"), "*.yml", SearchOption.AllDirectories))
{
    var yaml = Read(file);
    foreach (var map in Maps(yaml))
    {
        var reference = Value(map, "uses");
        const string prefix = "glennawatson/actions-common/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            continue;
        }

        var path = reference[prefix.Length..].Split('@')[0];
        var target = Path.Combine(root, path);
        if (!path.StartsWith(".github/workflows/", StringComparison.Ordinal))
        {
            target = Path.Combine(target, "action.yml");
        }

        var contract = Read(target);
        if (path.StartsWith(".github/workflows/", StringComparison.Ordinal))
        {
            contract = Child(Child(contract, "on"), "workflow_call");
        }

        var inputs = Child(contract, "inputs");
        var supplied = Child(map, "with");
        foreach (var key in supplied.Children.Keys)
            if (!inputs.Children.ContainsKey(key))
            {
                throw new InvalidOperationException($"Unknown input {key} in {file}.");
            }

        foreach (var input in inputs.Children)
            if (Value((YamlMappingNode)input.Value, "required") == "true" && !supplied.Children.ContainsKey(input.Key))
            {
                throw new InvalidOperationException($"Missing input {input.Key} in {file}.");
            }

        references++;
    }
}

foreach (var file in new[] { "Directory.Build.props", "Directory.Packages.props", ".editorconfig", ".github/actions/.editorconfig", "tools/.editorconfig" })
{
    var target = Path.Combine(scratch, file);
    _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(Path.Combine(root, file), target);
}

List<string> scripts = [];

List<string> sourceFiles = [];

var includes = new HashSet<string>(StringComparer.Ordinal);

foreach (var folder in (string[])[".github", "tools"])
{
    foreach (var file in Directory.GetFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
    {
        sourceFiles.Add(file);
        foreach (var line in File.ReadLines(file))
        {
            const string includeDirective = "#:include ";
            if (line.StartsWith(includeDirective, StringComparison.Ordinal))
            {
                _ = includes.Add(Path.GetFullPath(line[includeDirective.Length..].Trim(), Path.GetDirectoryName(file)!));
            }
        }
    }
}

foreach (var file in sourceFiles)
{
    var target = Path.Combine(scratch, Path.GetRelativePath(root, file));
    _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(file, target);
    if (!includes.Contains(file))
    {
        scripts.Add(target);
    }
}

foreach (var target in scripts)
{
    var build = Process.RunAndCaptureText("dotnet", ["build", target]);
    if (build.ExitStatus.ExitCode == 0)
    {
        continue;
    }

    Console.Error.WriteLine(build.StandardOutput + build.StandardError);
    return build.ExitStatus.ExitCode;
}

const string version = "1.7.12";

const string checksum = "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8";

using var client = new HttpClient();

var archive = await client.GetByteArrayAsync(new Uri($"https://github.com/rhysd/actionlint/releases/download/v{version}/actionlint_{version}_linux_amd64.tar.gz"));

if (!string.Equals(Convert.ToHexString(SHA256.HashData(archive)), checksum, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("The workflow checker checksum does not match.");
}

using var compressed = new MemoryStream(archive);

using var gzip = new GZipStream(compressed, CompressionMode.Decompress);

using var tar = new TarReader(gzip);

var checker = Path.Combine(scratch, "actionlint");

while (tar.GetNextEntry() is { } entry)
    if (entry.Name == "actionlint")
    {
        entry.ExtractToFile(checker, overwrite: false);
    }

File.SetUnixFileMode(checker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

var workflows = Directory.GetFiles(Path.Combine(root, ".github/workflows"), "*.yml");

var lint = Process.Run(checker, ["-shellcheck=", "-pyflakes=", .. workflows]);

if (lint.ExitCode != 0)
{
    return lint.ExitCode;
}

var runner = Process.Run("dotnet", ["run", "--file", Path.Combine(root, "tools", "VerifyRunner.cs")]);

if (runner.ExitCode != 0)
{
    return runner.ExitCode;
}

Console.WriteLine($"Checked {references} shared references. Compiled {scripts.Count} C# files without warnings. Checked {workflows.Length} workflows.");

return 0;

static YamlMappingNode Read(string file)
{
    var yaml = new YamlStream();
    yaml.Load(new StringReader(File.ReadAllText(file)));
    return (YamlMappingNode)yaml.Documents[0].RootNode;
}

static YamlMappingNode Child(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlMappingNode child ? child : new YamlMappingNode();

static string Value(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value.ToString() : string.Empty;

static IEnumerable<YamlMappingNode> Maps(YamlNode node)
{
    if (node is YamlMappingNode map)
    {
        yield return map;
        foreach (var child in map.Children.Values)
        {
            foreach (var item in Maps(child))
            {
                yield return item;
            }
        }
    }

    if (node is YamlSequenceNode sequence)
    {
        foreach (var child in sequence.Children)
        {
            foreach (var item in Maps(child))
            {
                yield return item;
            }
        }
    }
}
