namespace Tool2App;

/// <summary>An error the user can act on (bad input, missing package); the CLI prints it without a stack trace.</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
    public ToolException(string message, Exception inner) : base(message, inner) { }
}
