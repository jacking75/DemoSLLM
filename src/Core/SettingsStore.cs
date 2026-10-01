using System.Text.Json;
namespace LocalMind.Core;

public record StudioSettings(double? ReferencePricePerMillion = null);
public sealed class SettingsStore
{
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalMindStudio/settings.json");
    public StudioSettings Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(path)) ?? new() : new();
    public void Save(StudioSettings settings)
    {
        if (settings.ReferencePricePerMillion is double p && (!double.IsFinite(p) || p < 0)) throw new InvalidDataException("단가가 유효하지 않다.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings));
        File.Move(path + ".tmp", path, true);
    }
}
