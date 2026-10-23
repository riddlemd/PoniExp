using System.Reflection;

namespace PoniExp.Client;

/// <summary>This build's version: <c>&lt;version&gt;+&lt;commit&gt;</c> (the SDK appends the git commit), so two builds differ by commit.</summary>
public static class ClientVersion
{
    public static string Current { get; } =
        typeof(ClientVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
