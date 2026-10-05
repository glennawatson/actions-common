#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using static System.Environment;

if (GetEnvironmentVariable("SONAR_TOKEN") is not { Length: > 0 })
{
    Console.WriteLine("::error::SONAR_TOKEN is not set in the job/step env. Map the SONAR_TOKEN secret into env on the calling job.");
    return 1;
}

return 0;
