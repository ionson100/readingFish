using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readingFish.Models;
using readingFish.Services;

namespace readingFish.ViewModels;

/// <summary>
/// Параметры синтеза одного проекта. Диалог правит копию и отдаёт её
/// обратно: изменения попадают в проект только по кнопке «Сохранить».
/// </summary>
public partial class SynthesisOptionsViewModel : ViewModelBase
{
    /// <summary>Голоса, доступные для выбора: локальные плюс серверные.</summary>
    public ObservableCollection<VoiceSample> Voices { get; } = new();

    /// <summary>Выбранный голос проекта. Попадёт в reference_id при синтезе.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceChanged))]
    private string? _selectedVoiceId;

    /// <summary>Голос отличается от того, с которым проект был открыт.</summary>
    public bool VoiceChanged =>
        !string.IsNullOrWhiteSpace(SelectedVoiceId) &&
        !string.Equals(SelectedVoiceId, _originalVoiceId, StringComparison.Ordinal);

    /// <summary>Список голосов пуст — выбирать нечего.</summary>
    public bool HasVoices => Voices.Count > 0;

    private readonly string? _originalVoiceId;

    [ObservableProperty] private int _chunkLength = 200;
    [ObservableProperty] private int _maxNewTokens = 1024;
    [ObservableProperty] private double _topP = 0.8;
    [ObservableProperty] private double _repetitionPenalty = 1.1;
    [ObservableProperty] private double _temperature = 0.8;
    [ObservableProperty] private string _seedText = "";
    [ObservableProperty] private bool _useMemoryCache;
    [ObservableProperty] private bool _normalize = true;
    [ObservableProperty] private bool _balancedLatency;

    [ObservableProperty] private string? _seedError;

    // Границы — обычные свойства, а не const: скомпилированная привязка XAML
    // не видит статические члены.
    public int MinChunkLength => SynthesisOptions.MinChunkLength;
    public int MaxChunkLength => SynthesisOptions.MaxChunkLength;
    public int MinMaxNewTokens => SynthesisOptions.MinMaxNewTokens;
    public int MaxMaxNewTokens => SynthesisOptions.MaxMaxNewTokens;
    public double MinTopP => SynthesisOptions.MinTopP;
    public double MaxTopP => SynthesisOptions.MaxTopP;
    public double MinRepetitionPenalty => SynthesisOptions.MinRepetitionPenalty;
    public double MaxRepetitionPenalty => SynthesisOptions.MaxRepetitionPenalty;
    public double MinTemperature => SynthesisOptions.MinTemperature;
    public double MaxTemperature => SynthesisOptions.MaxTemperature;

    public string ChunkLengthRange => $"{MinChunkLength}–{MaxChunkLength} символов";
    public string MaxNewTokensRange => $"{MinMaxNewTokens}–{MaxMaxNewTokens} токенов";
    public string TopPRange => $"{MinTopP:0.0}–{MaxTopP:0.0}";
    public string RepetitionPenaltyRange => $"{MinRepetitionPenalty:0.0}–{MaxRepetitionPenalty:0.0}";
    public string TemperatureRange => $"{MinTemperature:0.0}–{MaxTemperature:0.0}";

    public SynthesisOptionsViewModel()
    {
    }

    public SynthesisOptionsViewModel(SynthesisOptions source, string? voiceId)
    {
        _originalVoiceId = voiceId;
        _selectedVoiceId = voiceId;
        Load(source);
    }

    /// <summary>
    /// Подставляет список голосов: сначала те, что знает сервер, затем
    /// локальные, которых на сервере ещё нет. Голос, выбранный в проекте,
    /// остаётся выбранным, даже если его нет в списке.
    /// </summary>
    public async Task LoadVoicesAsync(string? currentVoiceId)
    {
        var local = AppServices.Instance.Voices.LoadAll();

        List<string> serverIds;
        try
        {
            serverIds = await AppServices.Instance.Client.ListVoicesAsync();
        }
        catch (Exception ex)
        {
            // Без сервера показываем локальные голоса: синтез всё равно
            // не запустится, но выбор будет.
            AppLog.Info($"Список голосов с сервера не получен: {ex.Message.Split('\n')[0]}");
            foreach (var v in local)
                v.IsOnServer = false;

            serverIds = new List<string>();
        }

        var merged = AppServices.Instance.Voices.Merge(local, serverIds);

        Voices.Clear();
        foreach (var voice in merged)
            Voices.Add(voice);

        // Голос проекта может отсутствовать в списке: показываем его,
        // чтобы выбор не слетел молча.
        if (!string.IsNullOrWhiteSpace(currentVoiceId) &&
            !Voices.Any(v => string.Equals(v.Id, currentVoiceId, StringComparison.Ordinal)))
        {
            Voices.Insert(0, new VoiceSample { Id = currentVoiceId, IsOnServer = true });
        }

        SelectedVoiceId ??= currentVoiceId;
        OnPropertyChanged(nameof(HasVoices));
    }

    /// <summary>Запись выбранного голоса, чтобы отправить её на сервер при необходимости.</summary>
    public VoiceSample? SelectedVoice =>
        Voices.FirstOrDefault(v => string.Equals(v.Id, SelectedVoiceId, StringComparison.Ordinal));

    /// <summary>Заполняет поля из готовых параметров.</summary>
    public void Load(SynthesisOptions options)
    {
        var opts = options.Clamped();

        _loading = true;
        try
        {
            ChunkLength = opts.ChunkLength;
            MaxNewTokens = opts.MaxNewTokens;
            TopP = opts.TopP;
            RepetitionPenalty = opts.RepetitionPenalty;
            Temperature = opts.Temperature;
            SeedText = opts.Seed?.ToString() ?? "";
            UseMemoryCache = opts.MemoryCache == MemoryCacheMode.On;
            Normalize = opts.Normalize;
            BalancedLatency = opts.Latency == LatencyMode.Balanced;
        }
        finally
        {
            _loading = false;
        }

        SeedError = null;
    }

    private bool _loading;

    /// <summary>Собирает параметры. Значения вне границ подтягиваются к допустимым.</summary>
    public SynthesisOptions ToOptions()
    {
        int? seed = null;
        if (!string.IsNullOrWhiteSpace(SeedText))
        {
            if (int.TryParse(SeedText.Trim(), out var parsed) && parsed >= 0)
                seed = parsed;
        }

        return new SynthesisOptions
        {
            ChunkLength = ChunkLength,
            MaxNewTokens = MaxNewTokens,
            TopP = TopP,
            RepetitionPenalty = RepetitionPenalty,
            Temperature = Temperature,
            Seed = seed,
            MemoryCache = UseMemoryCache ? MemoryCacheMode.On : MemoryCacheMode.Off,
            Normalize = Normalize,
            Latency = BalancedLatency ? LatencyMode.Balanced : LatencyMode.Normal
        }.Clamped();
    }

    /// <summary>Проверяет зерно: пустое поле означает «сервер выберет сам».</summary>
    public bool ValidateSeed()
    {
        if (string.IsNullOrWhiteSpace(SeedText))
        {
            SeedError = null;
            return true;
        }

        if (!int.TryParse(SeedText.Trim(), out var parsed) || parsed < 0)
        {
            SeedError = "Зерно должно быть целым неотрицательным числом. Оставьте поле пустым, чтобы сервер выбрал сам.";
            return false;
        }

        SeedError = null;
        return true;
    }

    /// <summary>Вернуть серверные умолчания.</summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        Load(new SynthesisOptions());
        StatusHint = "Возвращены значения по умолчанию. Не забудьте сохранить.";
    }

    [ObservableProperty] private string _statusHint = "";

    partial void OnSeedTextChanged(string value)
    {
        if (!_loading)
            ValidateSeed();
    }

    /// <summary>true, если значения отличаются от серверных умолчаний.</summary>
    public bool HasCustomValues => ToOptions().IsCustom;

    [RelayCommand]
    private void Save()
    {
        if (!ValidateSeed())
            return;

        Closed?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);

    public event EventHandler<bool>? Closed;
}
