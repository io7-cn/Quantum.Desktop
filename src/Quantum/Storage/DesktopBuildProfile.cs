using System.Reflection;

namespace Quantum.Storage;

/// <summary>The immutable build profile, independent of Debug/Release and runtime environment variables.</summary>
internal sealed class DesktopBuildProfile
{
    private DesktopBuildProfile(string name) => Name = name;

    public static DesktopBuildProfile Preview { get; } = new("Preview");
    public static DesktopBuildProfile Stable { get; } = new("Stable");
    public static DesktopBuildProfile Current { get; } = Parse(typeof(DesktopBuildProfile).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "Quantum.BuildProfile").Value);

    public string Name { get; }
    public bool IsPreview => Name == "Preview";
    public string ApplicationId => IsPreview ? "io.quantum.desktop.preview" : "io.quantum.desktop";
    public string ApplicationTitle => IsPreview ? "Quantum Preview" : "Quantum";
    public string Publisher => "io7-cn";

    internal static DesktopBuildProfile Parse(string? name) => name switch
    {
        "Preview" => Preview,
        "Stable" => Stable,
        _ => throw new InvalidOperationException("Missing or invalid Quantum build profile; startup stopped before opening user data.")
    };
}
