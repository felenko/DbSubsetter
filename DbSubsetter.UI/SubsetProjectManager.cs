using System.IO;
using System.Text.Json;

namespace DbSubsetter.UI;

public class SubsetProjectManager
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DbSubsetter");
    private static readonly string RecentFilePath = Path.Combine(AppDataDir, "recent.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static void Save(SubsetProject project, string path)
    {
        var json = JsonSerializer.Serialize(project, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static SubsetProject Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SubsetProject>(json, JsonOptions) ?? new SubsetProject();
    }

    public static List<string> LoadRecentProjects()
    {
        if (!File.Exists(RecentFilePath)) return new();
        try
        {
            var json = File.ReadAllText(RecentFilePath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new();
        }
        catch { return new(); }
    }

    public static void AddRecentProject(string path, List<string> recents)
    {
        recents.Remove(path);
        recents.Insert(0, path);
        while (recents.Count > 10)
            recents.RemoveAt(recents.Count - 1);
        SaveRecentProjects(recents);
    }

    public static void SaveRecentProjects(List<string> recents)
    {
        Directory.CreateDirectory(AppDataDir);
        File.WriteAllText(RecentFilePath, JsonSerializer.Serialize(recents, JsonOptions));
    }
}
