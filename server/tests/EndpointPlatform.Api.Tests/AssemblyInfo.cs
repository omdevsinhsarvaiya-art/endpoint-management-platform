// All test classes in this assembly boot the same Admin API entry point through
// WebApplicationFactory. Parallel factories race inside HostFactoryResolver's
// process-wide diagnostic listener ("The entry point exited without ever building
// an IHost"), so collections here run serially. The whole assembly finishes in
// well under a minute; determinism is worth more than the overlap.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
