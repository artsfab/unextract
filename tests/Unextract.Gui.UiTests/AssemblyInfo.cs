// UI tests drive one shared interactive desktop (mouse, keyboard, focus), so nothing may run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
