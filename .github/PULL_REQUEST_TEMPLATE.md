<!-- Thanks for contributing! For anything beyond a small fix, please open an issue first
     so we can agree on the approach — see CONTRIBUTING.md. -->

## Summary

<!-- What does this PR change, and why? -->

## Related issue

<!-- e.g. "Fixes #123". For non-trivial changes there should be a prior issue. -->

## How was it tested?

<!-- Platforms/RIDs and backend (cpu/gpu) you tested on. If you ran the model tests,
     mention which model (LITERTLM_TEST_MODEL / LITERTLM_TEST_TOOLS=1). -->

## Checklist

- [ ] `dotnet build LiteRtLmSharp.slnx` and `dotnet test` pass locally
- [ ] No native binaries or model files committed
- [ ] Interop stays AOT/trim-friendly (`[LibraryImport]` / `[UnmanagedCallersOnly]`, no reflection-based marshalling)
- [ ] README/docs updated if user-visible behavior changed
- [ ] No package version bump (versions are handled at release time)
