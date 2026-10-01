using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace readingFish.Models;

public enum SynthesisState
{
    NotStarted,
    InProgress,
    Completed,
    Failed
}

/// <summary>Часть текста проекта — одна порция, отправляемая на сервер.</summary>
public sealed class ProjectPart : INotifyPropertyChanged
{
    private bool _isPlaying;

    public int Index { get; set; }

    /// <summary>Текущий текст части. Можно редактировать.</summary>
    public string Text { get; set; } = "";

    /// <summary>Текст, который реально был синтезирован (для сравнения с текущим).</summary>
    public string? SynthesizedText { get; set; }

    /// <summary>Голос, которым синтезировали эту часть.</summary>
    public string? SynthesizedVoiceId { get; set; }

    public string? LastError { get; set; }

    [JsonIgnore]
    public string AudioFileName => $"part_{Index:D4}.wav";

    [JsonIgnore]
    public string AudioPath => Path.Combine(AppPaths.ProjectAudioDir(ProjectIdRef), AudioFileName);

    /// <summary>Заполняется хранилищем при загрузке — часть сама не знает свой проект.</summary>
    [JsonIgnore]
    public string ProjectIdRef { get; set; } = "";

    /// <summary>
    /// Аудио синтезировано и совпадает с текущим текстом и голосом проекта.
    /// Голос сравниваем только если он записан: у частей, синтезированных
    /// до появления этого поля, SynthesizedVoiceId пуст, и трогать их не нужно.
    /// </summary>
    [JsonIgnore]
    public bool IsCurrent =>
        SynthesizedText is not null &&
        string.Equals(SynthesizedText, Text, StringComparison.Ordinal) &&
        !HasDifferentVoice &&
        File.Exists(AudioPath);

    /// <summary>Часть синтезирована другим голосом, чем выбран сейчас.</summary>
    [JsonIgnore]
    public bool HasDifferentVoice =>
        SynthesizedVoiceId is { } used &&
        !string.IsNullOrWhiteSpace(ProjectVoiceId) &&
        !string.Equals(used, ProjectVoiceId, StringComparison.Ordinal);

    /// <summary>Голос проекта. Заполняется хранилищем вместе с ProjectIdRef.</summary>
    [JsonIgnore]
    public string ProjectVoiceId { get; set; } = "";

    /// <summary>Часть была синтезирована, но текст или голос изменились — нужно перегенерировать.</summary>
    [JsonIgnore]
    public bool IsStale =>
        SynthesizedText is not null &&
        (!string.Equals(SynthesizedText, Text, StringComparison.Ordinal) || HasDifferentVoice);

    [JsonIgnore]
    public bool HasError => !string.IsNullOrEmpty(LastError);

    [JsonIgnore]
    public string StatusText
    {
        get
        {
            if (IsCurrent) return "готово";
            if (HasError) return "ошибка";
            if (SynthesizedText is not null) return "требует перегенерации";
            return "ожидает";
        }
    }

    /// <summary>Сейчас проигрывается именно эта часть. Состояние интерфейса, в JSON не пишется.</summary>
    [JsonIgnore]
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value)
                return;

            _isPlaying = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Проект перевода. Основная сущность приложения.
/// Лежит в ~/fishApp/projects/&lt;id&gt;/
/// </summary>
public sealed class Project
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string VoiceId { get; set; } = "";

    /// <summary>Имя исходного файла, который пользователь выбрал при создании.</summary>
    public string SourceFileName { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public SynthesisState State { get; set; } = SynthesisState.NotStarted;
    public string? LastError { get; set; }

    /// <summary>Параметры синтеза этого проекта. У каждого проекта свои.</summary>
    public SynthesisOptions Synthesis { get; set; } = new();

    public List<ProjectPart> Parts { get; set; } = new();

    public const string SourceFileNameOnDisk = "source.txt";
    public const string ResultFileName = "result.wav";

    [JsonIgnore] public string Dir => AppPaths.ProjectDir(Id);
    [JsonIgnore] public string AudioDir => AppPaths.ProjectAudioDir(Id);
    [JsonIgnore] public string SourcePath => Path.Combine(Dir, SourceFileNameOnDisk);
    [JsonIgnore] public string ResultPath => Path.Combine(AudioDir, ResultFileName);

    public int TotalParts => Parts.Count;

    [JsonIgnore] public int DoneParts => Parts.Count(p => p.IsCurrent);

    [JsonIgnore] public int StaleParts => Parts.Count(p => p.IsStale);

    [JsonIgnore] public int FailedParts => Parts.Count(p => p.HasError && !p.IsCurrent);

    /// <summary>Результат актуален, когда все части готовы и файл существует.</summary>
    [JsonIgnore]
    public bool IsResultCurrent => TotalParts > 0 && DoneParts == TotalParts && File.Exists(ResultPath);

    [JsonIgnore]
    public bool HasResultFile => File.Exists(ResultPath);

    [JsonIgnore]
    public string ProgressText => $"{DoneParts} / {TotalParts}";

    [JsonIgnore]
    public double ProgressRatio => TotalParts == 0 ? 0 : (double)DoneParts / TotalParts;

    /// <summary>Есть ли что доделывать (новые, устаревшие или упавшие части).</summary>
    [JsonIgnore]
    public bool HasPendingWork => Parts.Any(p => !p.IsCurrent);
}
