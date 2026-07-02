// NativeAOT smoke used by ci.yml's aot-smoke job. Two things are asserted by running this binary in
// an environment WITHOUT native binaries:
//   1. the core package publishes and starts under NativeAOT (the IsAotCompatible promise), and
//   2. the missing-runtime-package diagnostic surfaces as designed — the very first native touch must
//      throw the enriched DllNotFoundException naming the LiteRtLmSharp.runtime.<rid> package.
// Exit codes: 42 = both hold (expected); 1 = something else happened.
// A full model-backed AOT run (load + tokenize + chat + streaming) was validated manually on win-x64;
// this check keeps the publish + startup + diagnostic path green without downloading a model in CI.
using LiteRtLmSharp;

try
{
    LiteRtEngine.SetMinLogLevel(3);
    Console.WriteLine("UNEXPECTED: the native library resolved — this check must run without natives.");
    return 1;
}
catch (DllNotFoundException ex) when (ex.Message.Contains("LiteRtLmSharp.runtime."))
{
    Console.WriteLine("OK: NativeAOT startup + enriched missing-runtime diagnostic:");
    Console.WriteLine(ex.Message);
    return 42;
}
catch (Exception ex)
{
    Console.WriteLine($"UNEXPECTED: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
