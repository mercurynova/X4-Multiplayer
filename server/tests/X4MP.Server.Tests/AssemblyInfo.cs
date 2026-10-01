// WebApplicationFactory<Program> resolves the host through a process-wide diagnostic listener, so starting several
// factories concurrently fails intermittently with "The entry point exited without ever building an IHost".
[assembly: CollectionBehavior(DisableTestParallelization = true)]
