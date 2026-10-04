namespace DustoreLauncherV.Mac.Services;

/// <summary>Free or Prime, fixed at build time (-p:DustoreEdition=Prime).</summary>
public static class Edition
{
#if PRIME
    public static readonly bool IsPrimeBuild = true;
#else
    public static readonly bool IsPrimeBuild = false;
#endif
    /// <summary>A Prime build whose purchase the Dustore store confirmed on this Mac.</summary>
    public static bool IsPrime => IsPrimeBuild && PrimeLicense.IsActive;
    public static string Name => IsPrime ? "Prime" : "Free";

    /// <summary>Free converts at a capped pace; Prime has no limit.</summary>
    public const long FreeExBytesPerSecond = 2L * 1024 * 1024;
    /// <summary>Free waits this long before each eX transfer; Prime starts at once.</summary>
    public const int FreeQueueSeconds = 15;
}
