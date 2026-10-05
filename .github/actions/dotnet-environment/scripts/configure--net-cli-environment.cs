#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using static System.Environment;

File.AppendAllLines(GetEnvironmentVariable("GITHUB_ENV")!, ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1", "DOTNET_NOLOGO=1", "DOTNET_CLI_TELEMETRY_OPTOUT=1"]);
