using System.Runtime.InteropServices;

// Modes (first argument):
//   exit N          exit with code N
//   args            print every argument on its own line, wrapped in <>
//   cwd             print the working directory
//   echo            copy stdin to stdout, then print "err-line" on stderr
//   env NAME        print the value of an environment variable
//   basedir         print AppContext.BaseDirectory
//   entry           print the entry assembly's name
//   env-exit N      call Environment.Exit(N)
//   async N         await something, then return N (an async Main)
//   throw           throw an unhandled exception
//   load NAME       print the version of the named assembly and which context loaded it
//   wait-signal     print "ready", wait for SIGTERM / SIGINT, then exit 42 / 43
if (args.Length == 0) return 99;
switch (args[0])
{
    case "exit": return int.Parse(args[1]);
    case "args":
        foreach (var a in args.Skip(1)) Console.Out.Write("<" + a + ">\n");
        return 0;
    case "cwd": Console.Out.Write(Directory.GetCurrentDirectory() + "\n"); return 0;
    case "echo":
        Console.Out.Write(Console.In.ReadToEnd());
        Console.Error.Write("err-line\n");
        return 0;
    case "env": Console.Out.Write((Environment.GetEnvironmentVariable(args[1]) ?? "<unset>") + "\n"); return 0;
    case "basedir": Console.Out.Write(AppContext.BaseDirectory + "\n"); return 0;
    case "entry": Console.Out.Write(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name + "\n"); return 0;
    case "env-exit": Environment.Exit(int.Parse(args[1])); return 0;
    case "async": await Task.Delay(20); return int.Parse(args[1]);
    case "throw": throw new InvalidOperationException("boom from the tool");
    case "load":
    {
        var asm = System.Reflection.Assembly.Load(new System.Reflection.AssemblyName(args[1]));
        Console.Out.Write(asm.GetName().Version + " " + (System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm)?.Name ?? "?") + "\n");
        return 0;
    }
    case "wait-signal":
    {
        using var done = new ManualResetEventSlim();
        int code = 0;
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; code = 42; done.Set(); });
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; code = 43; done.Set(); });
        Console.Out.Write("ready\n");
        Console.Out.Flush();
        done.Wait(TimeSpan.FromSeconds(60));
        return code;
    }
}
return 98;
