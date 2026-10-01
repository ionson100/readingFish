using System.Text.Json;
using readingFish.Models;

namespace readingFish.Services;

/// <summary>
/// Хранилище примеров голоса. Локальная копия в ~/fishApp/voices плюс
/// синхронизация с папкой references на сервере.
/// </summary>
public sealed class VoiceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] AudioExtensions =
        { ".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aac", ".opus", ".wma" };

    public List<VoiceSample> LoadAll()
    {
        AppPaths.EnsureCreated();
        var list = new List<VoiceSample>();

        foreach (var dir in Directory.EnumerateDirectories(AppPaths.VoicesDir))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var labPath = Path.Combine(dir, VoiceSample.LabFileName);
            var text = File.Exists(labPath)
                ? File.ReadAllText(labPath).Trim()
                : "";

            list.Add(new VoiceSample
            {
                Id = id,
                Text = text,
                AddedAt = new DirectoryInfo(dir).CreationTimeUtc
            });
        }

        return list.OrderBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Копирует аудиофайл в ~/fishApp/voices/&lt;id&gt; и создаёт .lab.</summary>
    public VoiceSample Add(string sourceAudioPath, string text, string? preferredId = null)
    {
        AppPaths.EnsureCreated();

        if (!File.Exists(sourceAudioPath))
            throw new FileNotFoundException("Выбранный аудиофайл не найден.", sourceAudioPath);

        var ext = Path.GetExtension(sourceAudioPath).ToLowerInvariant();
        if (!AudioExtensions.Contains(ext))
        {
            throw new InvalidOperationException(
                $"Неподдерживаемый формат «{ext}». Поддерживаются: {string.Join(", ", AudioExtensions)}");
        }

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Укажите текст, произнесённый в записи.");

        var baseId = preferredId ?? Path.GetFileNameWithoutExtension(sourceAudioPath);
        var id = AppPaths.UniqueId(baseId, p => Directory.Exists(AppPaths.VoiceDir(p)));

        var dir = AppPaths.VoiceDir(id);
        Directory.CreateDirectory(dir);

        // Приводим расширение к .wav, как ожидает сервер.
        var target = Path.Combine(dir, VoiceSample.AudioFileName);
        File.Copy(sourceAudioPath, target, overwrite: true);

        File.WriteAllText(Path.Combine(dir, VoiceSample.LabFileName), text.Trim());

        return new VoiceSample { Id = id, Text = text.Trim() };
    }

    public void Delete(string id)
    {
        var dir = AppPaths.VoiceDir(id);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Голос «{id}» не найден.");

        Directory.Delete(dir, recursive: true);
    }

    /// <summary>Голоса, которые знает сервер, но которых нет локально.</summary>
    public List<string> MissingLocal(List<string> serverIds)
    {
        return serverIds.Where(id => !Directory.Exists(AppPaths.VoiceDir(id))).ToList();
    }

    /// <summary>
    /// Соединяет локальные голоса с серверными. Голос, который знает только
    /// сервер, попадает в список без папки: синтез с ним работает, а удалить
    /// его можно лишь на сервере.
    /// </summary>
    public List<VoiceSample> Merge(List<VoiceSample> local, List<string> serverIds)
    {
        var result = new List<VoiceSample>();

        foreach (var voice in local)
        {
            voice.IsOnServer = serverIds.Contains(voice.Id, StringComparer.OrdinalIgnoreCase);
            result.Add(voice);
        }

        foreach (var id in serverIds)
        {
            if (result.Any(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(new VoiceSample
            {
                Id = id,
                Text = "",
                IsOnServer = true,
                AddedAt = DateTimeOffset.MinValue
            });
        }

        return result
            .OrderByDescending(v => v.IsOnServer)
            .ThenBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
