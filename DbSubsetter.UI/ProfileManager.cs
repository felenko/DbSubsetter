using System.IO;
using System.Text.Json;
using DbSubsetter.Core;

namespace DbSubsetter.UI;

public class ProfileManager
{
    private static readonly string ProfileDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DbSubsetter");

    private static readonly string ProfilePath = Path.Combine(ProfileDir, "profiles.json");

    public List<ConnectionProfile> Load()
    {
        if (!File.Exists(ProfilePath))
            return new List<ConnectionProfile>();

        var json = File.ReadAllText(ProfilePath);
        return JsonSerializer.Deserialize<List<ConnectionProfile>>(json) ?? new List<ConnectionProfile>();
    }

    public void Save(List<ConnectionProfile> profiles)
    {
        Directory.CreateDirectory(ProfileDir);
        var json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ProfilePath, json);
    }
}
