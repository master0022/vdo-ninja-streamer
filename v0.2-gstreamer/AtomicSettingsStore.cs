using System.Text.Json;

namespace StreamerV2;

public sealed class AtomicSettingsStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AtomicSettingsStore(string path) => _path = Path.GetFullPath(path);

    public string PathForDisplay => _path;

    public StreamSettings LoadOrDefault(StreamSettings fallback)
    {
        try
        {
            if (!File.Exists(_path)) return fallback;
            return JsonSerializer.Deserialize<StreamSettings>(File.ReadAllText(_path), _json) ?? fallback;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return fallback;
        }
    }

    public void Save(StreamSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        var json = JsonSerializer.Serialize(settings, _json);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(true);
        }
        File.Move(temporary, _path, true);
    }
}
