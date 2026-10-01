namespace readingFish.Models;

/// <summary>
/// Пути всех данных приложения. Всё лежит в ~/fishApp.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "fishApp");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LockFile => Path.Combine(Root, "app.lock");
    public static string VoicesDir => Path.Combine(Root, "voices");
    public static string ProjectsDir => Path.Combine(Root, "projects");
    public static string LogsDir => Path.Combine(Root, "logs");

    public static string VoiceDir(string id) => Path.Combine(VoicesDir, id);
    public static string ProjectDir(string id) => Path.Combine(ProjectsDir, id);
    public static string ProjectAudioDir(string id) => Path.Combine(ProjectDir(id), "audio");
    public static string ProjectFile(string id) => Path.Combine(ProjectDir(id), "project.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(VoicesDir);
        Directory.CreateDirectory(ProjectsDir);
        Directory.CreateDirectory(LogsDir);
    }

    /// <summary>Приводит строку к безопасному имени папки: буквы, цифры, дефис, подчёркивание.</summary>
    public static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unnamed";

        var chars = value.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray();

        var result = new string(chars).Trim('_');
        if (result.Length == 0)
            result = "unnamed";

        return result.Length > 64 ? result[..64] : result;
    }

    /// <summary>Возвращает уникальный id: base + "-2", base + "-3" и т.д., если папка уже есть.</summary>
    public static string UniqueId(string baseName, Func<string, bool> exists)
    {
        var root = Sanitize(baseName);
        if (!exists(root))
            return root;

        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{root}-{i}";
            if (!exists(candidate))
                return candidate;
        }

        return $"{root}-{Guid.NewGuid().ToString("N")[..6]}";
    }
}
