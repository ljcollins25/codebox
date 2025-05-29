using System.Threading.Tasks.Dataflow;

namespace Nexis.Azure.Utilities;

#nullable disable

public readonly struct Url(Uri uri, string uriString)
{
    public bool IsNull => uri == null && uriString == null;

    public Uri Uri => uri ?? uriString?
        .FluidSelect(static s => new Uri(s).FluidSelect(u => u.IsAbsoluteUri ? u : new Uri("file://" + s)));
    public string UriString => uriString ?? uri?.ToString();

    public UriBuilder UriBuilder => Uri.FluidSelect(static u => new UriBuilder(u)
    {
        Port = u.IsDefaultPort ? -1 : u.Port
    });

    public static implicit operator Uri(Url u) => u.Uri;
    public static implicit operator UriBuilder(Url u) => new(u.Uri);
    public static implicit operator string(Url u) => u.UriString;
    public static implicit operator Url(Uri u) => new(u, null);
    public static implicit operator Url(UriBuilder u) => new(u.Uri, null);
    public static implicit operator Url(string u) => new(null, u);

    public Url Combine(string relativeUri)
    {
        UriBuilder builder = this;
        builder.Path = UriCombine(builder.Path, relativeUri);
        return new Uri(builder.ToString());
    }

    public override string ToString()
    {
        return UriString;
    }
}

