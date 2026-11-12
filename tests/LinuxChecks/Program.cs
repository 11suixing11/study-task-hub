using LinuxChecks;

try
{
    var suite = args.Length == 2 && args[0] == "--suite" ? args[1] : args.Length == 0 ? "all" : throw new ArgumentException("Use --suite <name>");
    if (suite is "all" or "protection") await ProtectionChecks.RunAsync();
    if (suite is "all" or "tasks") await TaskServiceChecks.RunAsync();
    if (suite is "all" or "ipc") await ProtocolChecks.RunAsync();
    if (suite is not ("all" or "protection" or "tasks" or "ipc")) throw new ArgumentException("Unknown suite");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { outcome = "passed", suite, checks = Check.Count, officialNetworkAccessed = false, productionDataAccessed = false }));
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
