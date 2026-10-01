using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readingFish.Models;
using readingFish.Services;

namespace readingFish.ViewModels;

/// <summary>
/// Страница проекта: список частей текста, прогресс, запуск синтеза,
/// перегенерация, прослушивание результата, редактирование частей.
/// </summary>
public partial class ProjectViewModel : ViewModelBase, IDisposable
{
    private readonly FishSpeechClient _client;
    private readonly ProjectStore _store;
    private readonly SynthesisService _synthesis;
    private readonly AudioPlayer _audio;
    private CancellationTokenSource? _cts;

    private bool _disposed;
    private bool _loadingLayout;
    private string? _requestedPath;
    private bool _requestedIsResult;

    public Project Project { get; private set; }

    public ObservableCollection<ProjectPart> Parts { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Готов к работе";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private ProjectPart? _selectedPart;
    [ObservableProperty] private string _resultInfo = "";
    [ObservableProperty] private bool _canPlayResult;
    [ObservableProperty] private bool _isResultStale;
    [ObservableProperty] private string _voiceId = "";

    // ---------- воспроизведение ----------

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _canStopPlayback;
    [ObservableProperty] private bool _isPlayingResultFile;
    [ObservableProperty] private string _playbackStatus = "";

    // Встроенный редактор исходного текста внизу страницы.
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private int _editorTargetChars = 200;
    [ObservableProperty] private int _estimatedParts;
    [ObservableProperty] private bool _hasUnsavedText;

    // Оформление редактора и положение разделителя — запоминаются в settings.json.
    [ObservableProperty] private double _editorFontSize = 13;
    [ObservableProperty] private bool _editorFontBold;
    [ObservableProperty] private double _editorRatio = 0.5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private string _progressLabel = "0 / 0";

    /// <summary>Процент выполнения 0..100 — к нему привязана полоса.</summary>
    public string ProgressText => $"{Math.Round(Progress)}%";

    /// <summary>Есть что показать в строке состояния воспроизведения.</summary>
    public bool HasPlaybackStatus => !string.IsNullOrEmpty(PlaybackStatus);

    /// <summary>
    /// Кнопка «Слушать» скрывается только когда result.wav уже играет
    /// и его можно остановить. Во всех остальных случаях она полезна.
    /// </summary>
    public bool ShowPlayResult => !IsPlayingResultFile || !CanStopPlayback;

    partial void OnPlaybackStatusChanged(string value) => OnPropertyChanged(nameof(HasPlaybackStatus));

    partial void OnIsPlayingResultFileChanged(bool value) => OnPropertyChanged(nameof(ShowPlayResult));

    partial void OnCanStopPlaybackChanged(bool value) => OnPropertyChanged(nameof(ShowPlayResult));

    public event EventHandler? BackRequested;

    public ProjectViewModel(Project project)
    {
        Project = project;
        _client = AppServices.Instance.Client;
        _store = AppServices.Instance.Projects;
        _synthesis = AppServices.Instance.Synthesis;
        _audio = AppServices.Instance.Audio;
        VoiceId = project.VoiceId;
        EditorTargetChars = AppServices.Instance.Settings.TargetPartChars;

        _audio.PlaybackChanged += OnPlaybackChanged;

        LoadEditorLayout();
        LoadSourceIntoEditor();
        RefreshState();
    }

    /// <summary>Читает source.txt в редактор. При отсутствии файла — склеивает текущие части.</summary>
    private void LoadSourceIntoEditor()
    {
        try
        {
            SourceText = File.Exists(Project.SourcePath)
                ? File.ReadAllText(Project.SourcePath)
                : string.Join(" ", Project.Parts.Select(p => p.Text));

            HasUnsavedText = false;
            RecalculateEstimatedParts();
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось прочитать исходный текст", ex);
            SourceText = string.Join(" ", Project.Parts.Select(p => p.Text));
        }
    }

    partial void OnSourceTextChanged(string value)
    {
        HasUnsavedText = true;
        RecalculateEstimatedParts();
    }

    partial void OnEditorTargetCharsChanged(int value) => RecalculateEstimatedParts();

    private void RecalculateEstimatedParts()
    {
        try
        {
            EstimatedParts = string.IsNullOrWhiteSpace(SourceText)
                ? 0
                : TextPartitioner.Split(SourceText, Math.Clamp(EditorTargetChars, 20, 5000)).Count;
        }
        catch
        {
            EstimatedParts = 0;
        }
    }

    // ---------- параметры синтеза ----------

    /// <summary>Краткая сводка параметров проекта для кнопки на странице.</summary>
    public string SynthesisSummary
    {
        get
        {
            var s = Project.Synthesis;
            var parts = new List<string>
            {
                $"часть {s.ChunkLength} симв.",
                $"T={s.Temperature:0.00}",
                $"top_p={s.TopP:0.00}"
            };

            if (Math.Abs(s.RepetitionPenalty - 1.1) > 0.001)
                parts.Add($"штраф {s.RepetitionPenalty:0.00}");

            if (s.Seed is { } seed)
                parts.Add($"зерно {seed}");

            if (s.MemoryCache == MemoryCacheMode.On)
                parts.Add("кэш референсов");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Открывает диалог параметров и записывает результат в проект.</summary>
    [RelayCommand]
    private async Task EditSynthesisOptionsAsync()
    {
        if (IsBusy)
            return;

        // Правим копию: пока пользователь не нажал «Сохранить» в диалоге,
        // параметры проекта не меняются.
        var vm = new SynthesisOptionsViewModel(Project.Synthesis, Project.VoiceId);
        await vm.LoadVoicesAsync(Project.VoiceId);

        var accepted = await AppServices.Instance.Dialogs.ShowSynthesisOptionsDialogAsync(vm);
        if (!accepted)
            return;

        Project.Synthesis = vm.ToOptions();
        StatusText = "Параметры синтеза сохранены";

        // Голос — тоже настройка проекта, меняем его здесь же.
        if (vm.VoiceChanged && vm.SelectedVoiceId is { } voiceId)
        {
            Project.VoiceId = voiceId;
            StatusText = "Параметры и голос сохранены";
            await EnsureVoiceOnServerAsync(vm.SelectedVoice);
        }

        _store.Save(Project);

        OnPropertyChanged(nameof(SynthesisSummary));
        OnPropertyChanged(nameof(HasCustomSynthesis));
        OnPropertyChanged(nameof(VoiceDisplay));
        RefreshState();
    }

    /// <summary>
    /// Отправляет голос на сервер, если его там нет. Голос, известный только
    /// локально, без сервера синтез не примет.
    /// </summary>
    private async Task EnsureVoiceOnServerAsync(VoiceSample? voice)
    {
        if (voice is null || !voice.LocalExists || voice.IsOnServer)
            return;

        try
        {
            await _client.AddVoiceAsync(voice.Id, voice.AudioPath, voice.Text);
            voice.IsOnServer = true;
            StatusText = $"Голос «{voice.Id}» отправлен на сервер";
        }
        catch (Exception ex)
        {
            // Синтез покажет понятную ошибку; молча ругаться здесь не нужно.
            AppLog.Info($"Не удалось отправить голос {voice.Id} на сервер: {ex.Message.Split('\n')[0]}");
            await AppServices.Instance.Dialogs.ShowErrorAsync(
                "Голос не отправлен на сервер",
                $"Проект сохранён, но сервер не принял голос «{voice.Id}»:\n\n{ex.Message}");
        }
    }

    /// <summary>Есть ли отличия от серверных умолчаний.</summary>
    public bool HasCustomSynthesis => Project.Synthesis.IsCustom;

    /// <summary>Сбросить правки и заново прочитать файл.</summary>
    [RelayCommand]
    private void ReloadSource()
    {
        LoadSourceIntoEditor();
        OnPropertyChanged(nameof(SourceText));
        StatusText = "Исходный текст перечитан из файла";
    }

    // ---------- оформление редактора и положение разделителя ----------

    /// <summary>
    /// Доля высоты под редактором. Задаётся разделителем и сохраняется, поэтому
    /// при изменении размера окна поле занимает ту же часть экрана.
    /// </summary>
    public void SetEditorRatio(double ratio)
    {
        var clamped = Math.Clamp(ratio, 0.1, 0.9);
        if (Math.Abs(clamped - EditorRatio) < 0.0001)
            return;

        EditorRatio = clamped;
    }

    /// <summary>Запоминает шрифт, жирность и положение разделителя в settings.json.</summary>
    [RelayCommand]
    public void SaveEditorLayout()
    {
        if (_disposed || _loadingLayout)
            return;

        var settings = AppServices.Instance.Settings;

        // Переписываем файл, только когда что-то действительно изменилось:
        // NumericUpDown шлёт значение на каждый шаг.
        if (Math.Abs(settings.EditorFontSize - EditorFontSize) < 0.01 &&
            settings.EditorFontBold == EditorFontBold &&
            Math.Abs(settings.EditorRatio - EditorRatio) < 0.0001)
        {
            return;
        }

        settings.EditorFontSize = EditorFontSize;
        settings.EditorFontBold = EditorFontBold;
        settings.EditorRatio = EditorRatio;
        AppServices.Instance.SaveSettings(settings);
    }

    private void LoadEditorLayout()
    {
        var settings = AppServices.Instance.Settings;

        // Флаг снимает автосохранение на время чтения: иначе конструктор
        // трижды переписывал бы settings.json без нужды.
        _loadingLayout = true;
        try
        {
            EditorFontSize = Math.Clamp(settings.EditorFontSize, 9, 32);
            EditorFontBold = settings.EditorFontBold;
            EditorRatio = Math.Clamp(settings.EditorRatio, 0.1, 0.9);
        }
        finally
        {
            _loadingLayout = false;
        }
    }

    partial void OnEditorFontSizeChanged(double value)
    {
        if (!_loadingLayout)
            SaveEditorLayout();
    }

    partial void OnEditorFontBoldChanged(bool value)
    {
        if (!_loadingLayout)
            SaveEditorLayout();
    }

    public string Name => Project.Name;
    public string SourceFileName => Project.SourceFileName;
    public string VoiceDisplay => string.IsNullOrWhiteSpace(Project.VoiceId) ? "не выбран" : Project.VoiceId;
    public bool CanEditParts => !IsBusy;

    public void Refresh()
    {
        var reloaded = _store.Load(Project.Id);
        if (reloaded is not null)
        {
            Project = reloaded;
            VoiceId = reloaded.VoiceId;
        }

        RefreshState();
    }

    private void RefreshState()
    {
        Parts.Clear();
        foreach (var p in Project.Parts)
            Parts.Add(p);

        // Полоса и подпись берут значения из проекта — они обновляются здесь,
        // а Progress во время синтеза пишется напрямую.
        Progress = Project.ProgressRatio * 100;
        ProgressLabel = $"{Project.DoneParts} / {Project.TotalParts}";

        CanPlayResult = Project.HasResultFile;
        IsResultStale = Project.HasResultFile && !Project.IsResultCurrent;

        OnPropertyChanged(nameof(CanEditParts));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(VoiceDisplay));
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(SynthesisSummary));
        OnPropertyChanged(nameof(HasCustomSynthesis));

        if (Project.HasResultFile)
        {
            var duration = WavTools.DurationSeconds(Project.ResultPath);
            var size = new FileInfo(Project.ResultPath).Length / 1024.0;
            ResultInfo = $"result.wav · {WavTools.FormatDuration(duration)} · {size:F0} КБ";
        }
        else
        {
            ResultInfo = "Результат ещё не собран";
        }

        OnPropertyChanged(nameof(ResultInfo));
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Синтез непроделанных частей. Уже готовые пропускаются.</summary>
    [RelayCommand]
    private async Task SynthesizeAsync()
    {
        await RunAsync(regenerateAll: false);
    }

    /// <summary>Полная перегенерация — все части заново.</summary>
    [RelayCommand]
    private async Task RegenerateAllAsync()
    {
        var ok = await AppServices.Instance.Dialogs.ConfirmAsync(
            "Перегенерировать проект",
            $"Будут заново синтезированы все {Project.TotalParts} частей.\n\n" +
            "Существующие аудиофайлы будут удалены. Продолжить?",
            "Перегенерировать");

        if (!ok)
            return;

        await RunAsync(regenerateAll: true);
    }

    private async Task RunAsync(bool regenerateAll)
    {
        if (IsBusy)
            return;

        var pending = Project.Parts.Count(p => !p.IsCurrent);

        // «Перегенерировать всё» — это и есть работа, даже когда все части готовы.
        // Ранний выход здесь означал бы, что кнопка молча ничего не делает.
        if (pending == 0 && !regenerateAll)
        {
            // Это не ошибка: все части уже готовы. Показываем статус, а не модальное окно.
            StatusText = Project.IsResultCurrent
                ? "Все части готовы, результат собран"
                : "Все части готовы. Нажмите «Пересобрать» для обновления result.wav";
            return;
        }

        // Сколько частей в этой задаче: при полной перегенерации — все,
        // иначе — только те, что ещё не готовы.
        var planned = regenerateAll ? Project.TotalParts : pending;
        if (planned == 0)
            return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = $"Синтез: 0 / {planned}";
        Progress = 0;
        ProgressLabel = regenerateAll
            ? $"0 / {Project.TotalParts}"
            : $"{Project.DoneParts} / {Project.TotalParts}";
        OnPropertyChanged(nameof(CanEditParts));

        try
        {
            // Параметры синтеза хранятся в проекте, а не в общих настройках.
            await _synthesis.RunAsync(
                Project,
                regenerateAll,
                new Progress<int>(done =>
                {
                    var total = Project.TotalParts;
                    if (total <= 0)
                        return;

                    // done — номер текущей части (1-based), считаем по проекту,
                    // потому что готовые части тоже учитываются.
                    Progress = (double)done / total * 100;
                    ProgressLabel = $"{done} / {total}";
                    StatusText = $"Синтезировано частей: {done} / {total}";
                }),
                _cts.Token);

            StatusText = "Готово";
            RefreshState();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Остановлено. Продолжение с места остановки.";
            RefreshState();
        }
        catch (Exception ex)
        {
            StatusText = "Ошибка синтеза";
            RefreshState();
            await AppServices.Instance.Dialogs.ShowErrorAsync("Ошибка синтеза", ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(CanEditParts));
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>Продолжить с места, где оборвалось (то же, что Synthesize, но с явным названием).</summary>
    [RelayCommand]
    private async Task ResumeAsync()
    {
        await RunAsync(regenerateAll: false);
    }

    [RelayCommand]
    private async Task PlayResultAsync()
    {
        if (!Project.HasResultFile)
        {
            await AppServices.Instance.Dialogs.ShowErrorAsync(
                "Файл не найден", "Результат ещё не собран.");
            return;
        }

        if (IsPlayingResultFile)
        {
            StopPlayback();
            return;
        }

        await PlayAsync(Project.ResultPath, isResultFile: true);
    }

    /// <summary>Прослушивание отдельной части. Повторное нажатие останавливает её.</summary>
    [RelayCommand]
    private async Task PlayPartAsync(ProjectPart? part)
    {
        if (part is null || !part.IsCurrent)
            return;

        if (IsPlayingPart(part))
        {
            StopPlayback();
            return;
        }

        await PlayAsync(part.AudioPath, isResultFile: false);
    }

    /// <summary>Кнопка остановки в строке части.</summary>
    [RelayCommand]
    private void StopPart(ProjectPart? part)
    {
        if (part is not null && IsPlayingPart(part))
            StopPlayback();
    }

    /// <summary>Останавливает звук.</summary>
    [RelayCommand]
    private void StopPlayback() => _audio.Stop();

    private bool IsPlayingPart(ProjectPart part) =>
        IsPlaying && !IsPlayingResultFile && part.AudioPath == _requestedPath;

    private async Task PlayAsync(string path, bool isResultFile)
    {
        // Запоминаем, что именно играет эта страница: события плеера приходят
        // для всех файлов, и чужие нас не касаются.
        _requestedPath = path;
        _requestedIsResult = isResultFile;

        var result = await _audio.PlayAsync(path);

        switch (result)
        {
            case AudioPlaybackResult.Failed:
                await AppServices.Instance.Dialogs.ShowErrorAsync(
                    "Не удалось воспроизвести",
                    $"Не удалось открыть аудиофайл:\n{path}");
                break;

            case AudioPlaybackResult.StartedExternally:
                StatusText = "Файл открыт в системном плеере";
                break;

            case AudioPlaybackResult.Started:
                StatusText = isResultFile ? "Играет результирующий файл" : "Играет выбранная часть";
                break;
        }
    }

    /// <summary>Событие плеера приходит из его потока — переносим на UI.</summary>
    private void OnPlaybackChanged(object? sender, AudioPlaybackChangedEventArgs e)
    {
        if (_disposed || e.Path is null)
            return;

        var path = e.Path;
        var state = e.State;
        var canStop = e.CanStop;
        var isExternal = e.IsExternal;

        DialogService.OnUiThread(() =>
        {
            if (_disposed || !string.Equals(path, _requestedPath, StringComparison.Ordinal))
                return;     // пока переносили в UI, могли переключить на другой файл

            switch (state)
            {
                case AudioPlaybackState.Playing:
                    IsPlaying = true;
                    CanStopPlayback = canStop;
                    IsPlayingResultFile = _requestedIsResult;
                    PlaybackStatus = isExternal
                        ? "Открыто в системном плеере"
                        : _requestedIsResult
                            ? "Играет result.wav"
                            : $"Играет часть №{PlayingPartNumber()}";

                    // Внешний плеер нам неподконтролен: кнопку остановки не показываем.
                    if (!isExternal && !_requestedIsResult)
                        MarkPartPlaying(path, true);
                    break;

                case AudioPlaybackState.Finished:
                    ClearPlaybackState();
                    PlaybackStatus = "Воспроизведение завершено";
                    break;

                case AudioPlaybackState.Stopped:
                    ClearPlaybackState();
                    PlaybackStatus = "Воспроизведение остановлено";
                    break;

                case AudioPlaybackState.Failed:
                    ClearPlaybackState();
                    PlaybackStatus = string.Empty;
                    break;
            }
        });
    }

    private int PlayingPartNumber()
    {
        if (_requestedPath is null)
            return 0;

        foreach (var part in Project.Parts)
        {
            if (part.AudioPath == _requestedPath)
                return part.Index + 1;
        }

        return 0;
    }

    private void MarkPartPlaying(string path, bool playing)
    {
        foreach (var part in Project.Parts)
        {
            if (part.AudioPath == path)
                part.IsPlaying = playing;
        }
    }

    private void ClearPlaybackState()
    {
        IsPlaying = false;
        CanStopPlayback = false;
        IsPlayingResultFile = false;
        _requestedPath = null;
        _requestedIsResult = false;

        foreach (var part in Project.Parts)
            part.IsPlaying = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _audio.PlaybackChanged -= OnPlaybackChanged;

        // Звук не должен продолжаться после ухода со страницы проекта.
        if (_audio.CurrentPath is not null &&
            string.Equals(_audio.CurrentPath, _requestedPath, StringComparison.Ordinal))
        {
            _audio.Stop();
        }

        foreach (var part in Project.Parts)
            part.IsPlaying = false;

        // Источник отмены освободит сам RunAsync в своём finally — освобождать
        // его здесь нельзя, пока синтез ещё пользуется токеном.
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Синтез уже завершился.
        }
    }

    [RelayCommand]
    private async Task EditPartAsync(ProjectPart? part)
    {
        if (part is null || IsBusy)
            return;

        var vm = new EditPartViewModel(part.Index, part.Text);
        var accepted = await AppServices.Instance.Dialogs.ShowEditPartDialogAsync(vm);
        if (!accepted)
            return;

        var newText = vm.ResultText?.Trim();
        if (string.IsNullOrWhiteSpace(newText) || newText == part.Text)
            return;

        var number = part.Index + 1;
        part.Text = newText!;
        _store.Save(Project);
        RefreshState();

        // Правка меняет текст, поэтому старая запись больше не подходит:
        // сразу отправляем часть на сервер и пересобираем результат.
        await RegeneratePartAsync(part, number);
    }

    /// <summary>Переозвучивает одну часть с показом статуса, как при обычном синтезе.</summary>
    private async Task RegeneratePartAsync(ProjectPart part, int number)
    {
        // Если играла именно эта часть или общий результат, звук сейчас
        // будет заменён — останавливаем заранее, чтобы не слышать старое.
        if (_requestedPath == part.AudioPath || _requestedPath == Project.ResultPath)
            StopPlayback();

        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = $"Часть №{number}: отправляю на сервер…";
        OnPropertyChanged(nameof(CanEditParts));

        try
        {
            var complete = await _synthesis.RegeneratePartAsync(Project, part, _cts.Token);

            RefreshState();

            StatusText = complete
                ? $"Часть №{number} переозвучена, result.wav пересобран"
                : $"Часть №{number} переозвучена. result.wav собран из готовых " +
                  "частей — остальные ещё не синтезированы";
        }
        catch (OperationCanceledException)
        {
            RefreshState();
            StatusText = $"Часть №{number}: переозвучивание остановлено";
        }
        catch (Exception ex)
        {
            RefreshState();
            await AppServices.Instance.Dialogs.ShowErrorAsync(
                "Не удалось переозвучить часть", ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(CanEditParts));
        }
    }

    /// <summary>Пересобрать result.wav из готовых частей без обращения к серверу.</summary>
    [RelayCommand]
    private async Task RebuildResultAsync()
    {
        try
        {
            _synthesis.Combine(Project);
            _store.Save(Project);
            RefreshState();
            await AppServices.Instance.Dialogs.ShowInfoAsync("Готово", "Результирующий файл пересобран.");
        }
        catch (Exception ex)
        {
            await AppServices.Instance.Dialogs.ShowErrorAsync("Не удалось пересобрать", ex.Message);
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (!AppServices.Instance.Dialogs.OpenInFileManager(Project.AudioDir))
        {
            await AppServices.Instance.Dialogs.ShowErrorAsync(
                "Не удалось открыть папку", $"Откройте вручную:\n\n{Project.AudioDir}");
        }
    }

    [RelayCommand]
    private async Task OpenSourceFileAsync()
    {
        if (!AppServices.Instance.Dialogs.OpenInFileManager(Project.SourcePath))
        {
            await AppServices.Instance.Dialogs.ShowErrorAsync(
                "Не удалось открыть файл", $"Откройте вручную:\n\n{Project.SourcePath}");
        }
    }

    /// <summary>
    /// Сохраняет текст из встроенного редактора в source.txt и пересобирает части.
    /// Старое аудио больше не соответствует тексту, поэтому удаляется.
    /// </summary>
    [RelayCommand]
    private async Task SaveSourceAsync()
    {
        if (IsBusy)
            return;

        var services = AppServices.Instance;
        var newText = SourceText;
        var newTarget = Math.Clamp(EditorTargetChars, 20, 5000);

        if (string.IsNullOrWhiteSpace(newText))
        {
            await services.Dialogs.ShowErrorAsync(
                "Текст пуст", "Введите текст в поле редактора перед сохранением.");
            return;
        }

        try
        {
            _store.EnsureFolders(Project);

            var chunks = TextPartitioner.Split(newText, newTarget);
            if (chunks.Count == 0)
            {
                await services.Dialogs.ShowErrorAsync(
                    "Не удалось разбить текст",
                    "После правки текст оказался пустым либо не содержит ни одного предложения.");
                return;
            }

            // Пишем исходный файл в папку проекта.
            await File.WriteAllTextAsync(Project.SourcePath, newText);

            Project.Parts.Clear();
            for (int i = 0; i < chunks.Count; i++)
            {
                Project.Parts.Add(new ProjectPart
                {
                    Index = i,
                    Text = chunks[i],
                    ProjectIdRef = Project.Id
                });
            }

            Project.State = SynthesisState.NotStarted;
            Project.LastError = null;

            CleanupOrphanAudio();
            _store.Save(Project);

            var settings = services.Settings;
            settings.TargetPartChars = newTarget;
            services.SaveSettings(settings);

            HasUnsavedText = false;
            SelectedPart = null;
            RefreshState();
            StatusText = $"Текст сохранён. Частей: {Project.TotalParts}. Нажмите «Синтезировать».";
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось сохранить исходный текст", ex);
            await services.Dialogs.ShowErrorAsync("Не удалось сохранить", ex.Message);
        }
    }

    /// <summary>Удаляет wav-файлы частей, для которых больше нет соответствующей части.</summary>
    private void CleanupOrphanAudio()
    {
        try
        {
            var valid = Project.Parts.Select(p => p.AudioFileName).ToHashSet(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(Project.AudioDir, "part_*.wav"))
            {
                var name = Path.GetFileName(file);
                if (!valid.Contains(name))
                    File.Delete(file);
            }

            if (File.Exists(Project.ResultPath))
                File.Delete(Project.ResultPath);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось почистить старые аудиофайлы", ex);
        }
    }
}
