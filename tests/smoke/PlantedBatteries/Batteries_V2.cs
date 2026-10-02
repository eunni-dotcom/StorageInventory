namespace SQLitePCL;

/// <summary>The decoy: signals that it was loaded and initialised.</summary>
public static class Batteries_V2
{
    public static void Init()
    {
        var marker = Environment.GetEnvironmentVariable("SI_PLANT_MARKER");
        if (!string.IsNullOrEmpty(marker)) File.WriteAllText(marker, "Batteries_V2.Init was called");
    }
}
