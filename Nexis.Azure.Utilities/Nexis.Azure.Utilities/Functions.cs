using System;
using System.Linq;
using System.Text.RegularExpressions;

public class Functions
{
    public static string GetEnvironmentVars(string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        return string.Join(",", Environment.GetEnvironmentVariables().Keys.OfType<string>().Where(name => regex.IsMatch(name)));
    }

    public static void ClearEnvironmentVars(string envVars)
    {
        foreach (var envVar in envVars.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }
}