using System.Diagnostics;
using readingFish.Models;

namespace readingFish.Services;

public sealed class SynthesisProgressEventArgs : EventArgs
{
    public required int Index { get; init; }
    public required int Total { get; init; }
    public required string Text { get; init; }
    public bool IsPartFinished { get; init; }
    public bool IsProjectFinished { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Синтез проекта: поочерёдно отправляет части на сервер, сохраняет wav каждой части,
/// в конце склеивает их в result.wav.
/// Каждая готовая часть сразу фиксируется на диске, поэтому прерывание не теряет работу.
/// </summary>
public sealed class SynthesisService
{
    private readonly FishSpeechClient _client;
    private readonly ProjectStore _projects;

    public SynthesisService(FishSpeechClient client, ProjectStore projects)
    {
        _client = client;
        _projects = projects;
    }

    public event EventHandler<SynthesisProgressEventArgs>? Progress;

    /// <summary>True, пока идёт синтез — используется для блокировки кнопок.</summary>
    public bool IsRunning { get; private set; }

    public async Task RunAsync(
        Project project,
        bool regenerateAll,
        IProgress<int>? partCounter = null,
        CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("Синтез уже выполняется.");

        // Голос не обязателен: сервер умеет подобрать случайный тембр.
        // Но пустое значение стоит отметить в статусе проекта.

        _projects.EnsureFolders(project);
        IsRunning = true;
        project.State = SynthesisState.InProgress;
        project.LastError = null;

        if (regenerateAll)
        {
            foreach (var part in project.Parts)
            {
                part.SynthesizedText = null;
                part.SynthesizedVoiceId = null;
                part.LastError = null;
                var audio = part.AudioPath;
                if (File.Exists(audio))
                    TryDelete(audio);
            }

            TryDelete(project.ResultPath);
            _projects.Save(project);
        }

        var total = project.Parts.Count;

        try
        {
            for (int i = 0; i < project.Parts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var part = project.Parts[i];

                // Пропускаем то, что уже синтезировано из того же текста и тем же голосом.
                if (part.IsCurrent)
                {
                    partCounter?.Report(part.Index + 1);
                    continue;
                }

                part.LastError = null;

                try
                {
                    // Параметры берём у проекта: у каждого свои.
                    var wav = await _client
                        .SynthesizeAsync(part.Text, project.VoiceId, project.Synthesis, ct)
                        .ConfigureAwait(false);

                    await File.WriteAllBytesAsync(part.AudioPath, wav, ct).ConfigureAwait(false);

                    part.SynthesizedText = part.Text;
                    part.SynthesizedVoiceId = project.VoiceId;
                }
                catch (OperationCanceledException)
                {
                    project.State = SynthesisState.InProgress;
                    _projects.Save(project);
                    throw;
                }
                catch (Exception ex)
                {
                    part.LastError = ex.Message;
                    project.State = SynthesisState.Failed;
                    project.LastError = ex.Message;
                    _projects.Save(project);

                    Progress?.Invoke(this, new SynthesisProgressEventArgs
                    {
                        Index = part.Index,
                        Total = total,
                        Text = part.Text,
                        Error = ex.Message
                    });

                    throw;
                }

                // Фиксируем после каждой части — состояние переживёт аварийное завершение.
                _projects.Save(project);
                partCounter?.Report(part.Index + 1);

                Progress?.Invoke(this, new SynthesisProgressEventArgs
                {
                    Index = part.Index,
                    Total = total,
                    Text = part.Text,
                    IsPartFinished = true
                });
            }

            var missing = project.Parts.Where(p => !p.IsCurrent).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Не удалось синтезировать {missing.Count} из {project.Parts.Count} частей.");

            Combine(project);
            project.State = SynthesisState.Completed;
            project.LastError = null;
            _projects.Save(project);

            Progress?.Invoke(this, new SynthesisProgressEventArgs
            {
                Index = total,
                Total = total,
                Text = string.Empty,
                IsProjectFinished = true
            });
        }
        finally
        {
            IsRunning = false;
            _projects.Save(project);
        }
    }

    /// <summary>
    /// Переозвучивает одну часть: уходит на сервер, wav заменяется, затем
    /// пересобирается result.wav. Нужно после правки текста части — иначе
    /// старая запись осталась бы лежать рядом с новым текстом.
    ///
    /// Возвращает true, если после пересборки результат полный.
    /// </summary>
    public async Task<bool> RegeneratePartAsync(
        Project project,
        ProjectPart part,
        CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("Синтез уже выполняется.");

        if (!project.Parts.Contains(part))
            throw new InvalidOperationException("Часть не принадлежит этому проекту.");

        _projects.EnsureFolders(project);
        IsRunning = true;
        project.LastError = null;
        part.LastError = null;

        try
        {
            var wav = await _client
                .SynthesizeAsync(part.Text, project.VoiceId, project.Synthesis, ct)
                .ConfigureAwait(false);

            // Старое аудио заменяем целиком. При отмене записи SynthesizedText
            // не трогаем — тогда часть останется устаревшей, и это честно.
            await File.WriteAllBytesAsync(part.AudioPath, wav, ct).ConfigureAwait(false);

            part.SynthesizedText = part.Text;
            part.SynthesizedVoiceId = project.VoiceId;
            part.LastError = null;
            _projects.Save(project);

            Progress?.Invoke(this, new SynthesisProgressEventArgs
            {
                Index = part.Index,
                Total = project.Parts.Count,
                Text = part.Text,
                IsPartFinished = true
            });

            var ready = project.Parts.Count(p => p.IsCurrent);
            if (ready == 0)
                return false;

            // Если соседние части ещё не синтезированы, результат будет неполным.
            // Собираем его всё равно — так же, как при обычном синтезе, — но
            // вызывающий код сообщит, что частей не хватает.
            Combine(project);
            project.State = ready == project.Parts.Count
                ? SynthesisState.Completed
                : SynthesisState.InProgress;

            _projects.Save(project);

            Progress?.Invoke(this, new SynthesisProgressEventArgs
            {
                Index = part.Index,
                Total = project.Parts.Count,
                Text = string.Empty,
                IsProjectFinished = true
            });

            return ready == project.Parts.Count;
        }
        catch (OperationCanceledException)
        {
            _projects.Save(project);
            throw;
        }
        catch (Exception ex)
        {
            part.LastError = ex.Message;
            project.State = SynthesisState.Failed;
            project.LastError = ex.Message;
            _projects.Save(project);

            Progress?.Invoke(this, new SynthesisProgressEventArgs
            {
                Index = part.Index,
                Total = project.Parts.Count,
                Text = part.Text,
                Error = ex.Message
            });

            throw;
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Склеивает wav-файлы всех готовых частей в один result.wav.</summary>
    public void Combine(Project project)    {
        _projects.EnsureFolders(project);

        var files = new List<byte[]>();
        foreach (var part in project.Parts)
        {
            if (!part.IsCurrent)
                continue;

            files.Add(File.ReadAllBytes(part.AudioPath));
        }

        if (files.Count == 0)
            throw new InvalidOperationException("Нет готовых частей для склейки.");

        var combined = WavTools.Combine(files);
        File.WriteAllBytes(project.ResultPath, combined);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Не смогли удалить старый файл — перезапишем при следующей генерации.
        }
    }
}
