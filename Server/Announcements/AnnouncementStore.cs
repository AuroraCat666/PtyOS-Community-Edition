using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPGReServer;

public enum AnnouncementType
{
    Maintenance,
    Update,
    Other
}

public class AnnouncementRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "other";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("publishedAt")] public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AnnouncementStore
{
    private readonly string _path;
    private readonly List<AnnouncementRecord> _announcements = new();
    private readonly object _lock = new();

    public AnnouncementStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "announcements.json");
        Directory.CreateDirectory(dataDir);
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<AnnouncementRecord>>(File.ReadAllText(_path));
            if (list != null) _announcements.AddRange(list);
        }
        catch (Exception e)
        {
            Log.Error($"[Announcement] 无法读取公告文件: {e.Message}");
        }
    }

    private void Save()
    {
        string tempPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(_announcements, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, true);
    }

    public AnnouncementRecord Publish(string type, string title, string content)
    {
        lock (_lock)
        {
            var record = new AnnouncementRecord
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Type = type,
                Title = title,
                Content = content,
                PublishedAt = DateTimeOffset.UtcNow
            };
            _announcements.Insert(0, record);
            Save();
            return record;
        }
    }

    public List<AnnouncementRecord> GetAll() => _announcements;
}