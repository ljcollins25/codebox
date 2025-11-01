//using Microsoft.NET.HostModel.AppHost;

namespace Dotnet.Portable.Runner;

class Program
{
    static void Main(string[] args)
    {
        //HostWriter.CreateAppHost()
        //Console.WriteLine("Hello, World!");

        StackOverflowTest(default, int.Parse(args[0]));
    }


    private static void CurrentDomain_FirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        Console.WriteLine("Handling exception");
    }

    static void StackOverflowTest(Guid guid, int index)
    {
        if (index <= 0)
        {
            return;
        }

        StackOverflowTest(guid, index - 1);
    }
}
