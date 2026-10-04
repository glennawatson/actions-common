# Repository rules

This repository holds shared .NET workflows and composite actions.

- Callers use `@main`. Changes affect their next workflow run.
- Keep repository-specific settings in callers.
- Preserve action inputs and outputs when changing implementations.
- Pass workflow inputs through environment variables before using them in commands.
- Keep script logic in C#.
- Run workflow syntax checks before publishing.
- Compile inline C# scripts outside this repository before publishing.
- Keep signing secrets in the calling job's environment.
- Keep docs and migration reports local.
- Do not add attribution footers or commit trailers.
