#!/usr/bin/env dotnet
using static System.Environment;

if (GetEnvironmentVariable("SONAR_TOKEN") is not { Length: > 0 })
{
    Console.WriteLine("::error::SONAR_TOKEN is not set in the job/step env. Map the SONAR_TOKEN secret into env on the calling job.");
    return 1;
}

return 0;
