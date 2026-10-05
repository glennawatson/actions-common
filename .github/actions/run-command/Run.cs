#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using System.Text;
using static System.Environment;

Directory.SetCurrentDirectory(Path.GetFullPath(GetEnvironmentVariable("COMMAND_DIRECTORY")!, GetEnvironmentVariable("GITHUB_WORKSPACE")!));

var command = Expand(GetEnvironmentVariable("COMMAND_FILE")!);

var arguments = (GetEnvironmentVariable("COMMAND_ARGUMENTS") ?? string.Empty)
    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

// Log variable names before expansion to keep credentials out of the command trace.
Console.WriteLine($"[command]{GetEnvironmentVariable("COMMAND_FILE")} {string.Join(' ', arguments)}");

for (var index = 0; index < arguments.Length; index++)
{
    arguments[index] = Expand(arguments[index]);
}

return Process.Run(command, arguments).ExitCode;

static string Expand(string value)
{
    var result = new StringBuilder();
    for (var index = 0; index < value.Length; index++)
    {
        if (value[index] != '$')
        {
            _ = result.Append(value[index]);
            continue;
        }

        var start = index + 1;
        var braced = IsBracedVariable(value, start);
        if (braced)
        {
            start++;
        }

        var end = start;
        while (end < value.Length && IsVariableCharacter(value[end]))
        {
            end++;
        }

        if (end == start || !HasClosingBrace(value, braced, end))
        {
            _ = result.Append('$');
            continue;
        }

        var name = value[start..end];
        var replacement = GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing environment variable: {name}.");
        _ = result.Append(replacement);
        index = braced ? end : end - 1;
    }

    return result.ToString();
}

static bool HasClosingBrace(string value, bool braced, int end) => !braced || (end < value.Length && value[end] == '}');

static bool IsVariableCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value == '_';

static bool IsBracedVariable(string value, int start) => start < value.Length && value[start] == '{';
