// The native LiteRT environment is process-global and not safe to initialize concurrently,
// so run all tests sequentially (no cross-collection parallelism).
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
