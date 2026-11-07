using System.IO;
using System.Text.Json;

namespace Beam.Core;

public sealed class Settings
{
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Beam");
    static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public bool Arabic { get; set; } = true;
    public List<string> Recent { get; set; } = [];
    public List<string>? OpenTabs { get; set; }
    public string? SelectedTab { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
        }
        return new Settings();
    }

    public void Remember(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > 8)
            Recent.RemoveRange(8, Recent.Count - 8);
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
