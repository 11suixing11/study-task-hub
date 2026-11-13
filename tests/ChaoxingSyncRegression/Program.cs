try
{
    await ChaoxingIndependentSyncChecks.RunAsync(args.FirstOrDefault()??"all");
}
catch(Exception error)
{
    Console.Error.WriteLine(error.Message);Environment.ExitCode=1;
}
