using System.Text;
using Xunit.Abstractions;

namespace Nexis.Azure.Utilities.Tests;

public class TestOutputWriter : StringWriter
{
    public override Encoding Encoding => Encoding.UTF8;

    public ITestOutputHelper Output { get; }

    public TestOutputWriter(ITestOutputHelper output)
    {
        Output = output;
    }

    public override void Write(string? value)
    {
        if (value?.EndsWith('\n') == true)
        {
            if (value.EndsWith('\r'))
            {
                WriteLine(value.Substring(0, value.Length - 1));
            }
            else
            {
                WriteLine(value.Substring(0, value.Length - 2));
            }
        }
        else
        {
            WriteLine(value);
        }
    }

    public override void WriteLine(string value)
    {
        Output.WriteLine(value);
    }

    public override void WriteLine()
    {
        Flush();
    }

    public override void Flush()
    {
        var sb = base.GetStringBuilder();
        Output.WriteLine(sb.ToString());
        sb.Clear();
    }
}
