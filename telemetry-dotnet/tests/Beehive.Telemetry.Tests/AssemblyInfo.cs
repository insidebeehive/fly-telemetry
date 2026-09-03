// Environment variables, Console.Out and the memoised service name are process-global;
// the suite exercises all three, so it runs serially by construction.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
