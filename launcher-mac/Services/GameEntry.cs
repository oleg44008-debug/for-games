using System.Text.Json.Serialization;

namespace DustoreLauncherV.Mac.Services;

public sealed record GameEntry(
    Guid Id,
    string Name,
    string SourcePath,
    DateTimeOffset AddedUtc,
    DateTimeOffset? LastPlayedUtc = null,
    string? PreparedMacAppPath = null,
    string? LastOutputPath = null,
    string? WindowMode = null,
    int? WindowWidth = null,
    int? WindowHeight = null,
    string? GraphicsMode = null,
    bool MetalFxUpscale = false,
    int? FpsLimit = null,
    bool ShowFps = false,
    string? CustomCoverPath = null,
    bool Ultra = false)
{
    [JsonIgnore]
    public string Kind => Directory.Exists(SourcePath)
        ? SourcePath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? "macOS .app" : "Папка игры"
        : Path.GetExtension(SourcePath).ToLowerInvariant() switch
        {
            ".exe" => "Windows .exe",
            ".zip" => "ZIP-пакет",
            ".pck" => "Godot .pck",
            ".love" => "LÖVE",
            _ => "Файл игры"
        };

    [JsonIgnore]
    public bool SourceExists => File.Exists(SourcePath) || Directory.Exists(SourcePath);
    [JsonIgnore]
    public bool CanLaunchOnMac => PreparedMacAppPath is { } prepared && Directory.Exists(prepared)
        || SourcePath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(SourcePath);
}
