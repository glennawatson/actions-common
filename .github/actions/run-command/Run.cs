#!/usr/bin/env dotnet
using System.Diagnostics;
using System.Text;
using static System.Environment;

Directory.SetCurrentDirectory(Path.GetFullPath(GetEnvironmentVariable("COMMAND_DIRECTORY")!, GetEnvironmentVariable("GITHUB_WORKSPACE")!));
var command = Expand(GetEnvironmentVariable("COMMAND_FILE")!);
var arguments = (GetEnvironmentVariable("COMMAND_ARGUMENTS") ?? string.Empty)
    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
    .Select(Expand).ToArray();
return Process.Run(command, arguments).ExitCode;

static string Expand(string value)
{
    var result = new StringBuilder();
    for (var index = 0; index < value.Length; index++)
    {
        if (value[index] != '$')
        {
            result.Append(value[index]);
            continue;
        }

        var start = index + 1;
        var braced = start < value.Length && value[start] == '{';
        if (braced) start++;
        var end = start;
        while (end < value.Length && (char.IsAsciiLetterOrDigit(value[end]) || value[end] == '_')) end++;
        if (end == start || (braced && (end >= value.Length || value[end] != '}')))
        {
            result.Append('$');
            continue;
        }

        var name = value[start..end];
        var replacement = GetEnvironmentVariable(name);
        if (replacement is null) throw new InvalidOperationException($"Missing environment variable: {name}.");
        result.Append(replacement);
        index = braced ? end : end - 1;
    }

    return result.ToString();
}
