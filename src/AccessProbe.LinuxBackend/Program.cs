using AccessProbe.Linux;
using AccessProbe.LinuxBackend;
try
{
    string? explicitRoot=null;
    for(var i=0;i<args.Length;i++)
        if(args[i]=="--data-root" && i+1<args.Length)explicitRoot=args[++i];
        else if(args[i]=="--help"){Console.WriteLine("StudyTaskHub.LinuxBackend --data-root PATH");return;}
        else throw new ArgumentException();
    var root=LinuxDataPaths.Resolve(explicitRoot,Environment.GetEnvironmentVariable("XDG_DATA_HOME"),Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    using var service=new LinuxAppService(root);
    await BackendHost.ServeAsync(Console.OpenStandardInput(),Console.OpenStandardOutput(),service,CancellationToken.None);
}
catch(AccessProbe.TaskValidationException error){Console.Error.WriteLine(error.Code);Environment.ExitCode=1;}
catch{Console.Error.WriteLine("IPC006");Environment.ExitCode=1;}
