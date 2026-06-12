# Security policy

## Supported versions

Only the latest published version on [nuget.org](https://www.nuget.org/packages/LiteRtLmSharp)
receives fixes.

## Reporting a vulnerability

Please **do not** open a public issue. Use GitHub's private vulnerability reporting:
[Security → Report a vulnerability](https://github.com/OrihuelaConde/LiteRtLmSharp/security/advisories/new).

Note that the native binaries are built unmodified (apart from adding a shared-library build
target) from [google-ai-edge/LiteRT-LM](https://github.com/google-ai-edge/LiteRT-LM) source at
pinned release tags — vulnerabilities in the engine itself should follow
[Google's reporting process](https://github.com/google-ai-edge/LiteRT-LM/security) as well.
