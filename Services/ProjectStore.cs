using System.Text.Json;
using readingFish.Models;

namespace readingFish.Services;

/// <summary>Создание, чтение и сохранение проектов на диске.</summary>
public sealed class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<Project> LoadAll()
    {
        AppPaths.EnsureCreated();
        var list = new List<Project>();

        foreach (var dir in Directory.EnumerateDirectories(AppPaths.ProjectsDir))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(id))
                continue;

            try
            {
                var project = Load(id);
                if (project is not null)
                    list.Add(project);
            }
            catch (Exception ex)
            {
                // Повреждённый проект не должен мешать открыть остальные.
                Console.Error.WriteLine($"Не удалось прочитать проект {id}: {ex.Message}");
            }
        }

        return list
            .OrderByDescending(p => p.UpdatedAt)
            .ToList();
    }

    public Project? Load(string id)
    {
        var file = AppPaths.ProjectFile(id);
        if (!File.Exists(file))
            return null;

        var json = File.ReadAllText(file);
        var project = JsonSerializer.Deserialize<Project>(json, JsonOptions)
                      ?? throw new InvalidDataException("Пустой project.json");

        project.Id = id;
        foreach (var part in project.Parts)
        {
            part.ProjectIdRef = id;

            // Часть сама не знает голос проекта: без него нельзя понять,
            // устарела ли она после смены голоса.
            part.ProjectVoiceId = project.VoiceId;
        }

        // Старые project.json без блока синтеза: подставляем умолчания
        // и заодно подтягиваем к допустимым границам.
        project.Synthesis ??= new SynthesisOptions();
        project.Synthesis = project.Synthesis.Clamped();

        return project;
    }

    public void Save(Project project)
    {
        Directory.CreateDirectory(project.Dir);
        Directory.CreateDirectory(project.AudioDir);

        foreach (var part in project.Parts)
        {
            part.ProjectIdRef = project.Id;
            part.ProjectVoiceId = project.VoiceId;
        }

        project.UpdatedAt = DateTimeOffset.Now;
        var json = JsonSerializer.Serialize(project, JsonOptions);
        File.WriteAllText(AppPaths.ProjectFile(project.Id), json);
    }

    /// <summary>Создаёт проект: копирует исходный файл, режет текст на части, раскладывает по папкам.</summary>
    public Project Create(string sourceTextFilePath, string projectName, string voiceId, int targetChars)
    {
        AppPaths.EnsureCreated();

        if (!File.Exists(sourceTextFilePath))
            throw new FileNotFoundException("Исходный файл не найден.", sourceTextFilePath);

        var name = string.IsNullOrWhiteSpace(projectName)
            ? Path.GetFileNameWithoutExtension(sourceTextFilePath)
            : projectName.Trim();

        var id = AppPaths.UniqueId(name, p => Directory.Exists(AppPaths.ProjectDir(p)));

        var dir = AppPaths.ProjectDir(id);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(AppPaths.ProjectAudioDir(id));

        // Копируем исходный файл в папку проекта.
        var sourceTarget = Path.Combine(dir, Project.SourceFileNameOnDisk);
        File.Copy(sourceTextFilePath, sourceTarget, overwrite: true);

        var text = File.ReadAllText(sourceTarget);
        var parts = TextPartitioner.Split(text, targetChars);

        if (parts.Count == 0)
            throw new InvalidOperationException(
                "В файле нет текста для синтеза (возможно, он пустой или содержит только пробелы).");

        var project = new Project
        {
            Id = id,
            Name = name,
            VoiceId = voiceId,
            SourceFileName = Path.GetFileName(sourceTextFilePath),
            State = SynthesisState.NotStarted,

            // Новый проект начинает с серверных умолчаний; общие настройки
            // приложения на синтез больше не влияют.
            Synthesis = new SynthesisOptions()
        };

        for (int i = 0; i < parts.Count; i++)
        {
            project.Parts.Add(new ProjectPart
            {
                Index = i,
                Text = parts[i],
                ProjectIdRef = id
            });
        }

        Save(project);
        return project;
    }

    public void Delete(Project project)
    {
        var dir = project.Dir;
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>Открывает папку проекта в файловом менеджере.</summary>
    public void EnsureFolders(Project project)
    {
        Directory.CreateDirectory(project.Dir);
        Directory.CreateDirectory(project.AudioDir);
    }
}
