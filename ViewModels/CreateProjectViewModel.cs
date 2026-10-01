using readingFish.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readingFish.Services;

namespace readingFish.ViewModels;

/// <summary>Создание проекта: выбор файла уже сделан, осталось имя и голос.</summary>
public partial class CreateProjectViewModel : ViewModelBase
{
    private readonly List<VoiceSample> _voices;
    private readonly FishSpeechClient _client;
    private readonly string _defaultVoiceId;
    private readonly string _sourcePath;

    [ObservableProperty] private string _projectName = "";
    [ObservableProperty] private string? _selectedVoiceId;
    [ObservableProperty] private string _sourceFileName = "";
    [ObservableProperty] private string _sourcePreview = "";
    [ObservableProperty] private int _targetChars = 200;
    [ObservableProperty] private int _estimatedParts;
    [ObservableProperty] private bool _isBusy;

    public Project? CreatedProject { get; private set; }

    public IReadOnlyList<VoiceSample> Voices => _voices;

    public event EventHandler<bool>? Closed;

    public CreateProjectViewModel(string sourcePath, List<VoiceSample> voices, string? defaultVoiceId)
    {
        _voices = voices;
        _client = AppServices.Instance.Client;
        _defaultVoiceId = defaultVoiceId ?? "";
        _targetChars = AppServices.Instance.Settings.TargetPartChars;
        _sourcePath = sourcePath;

        SourceFileName = Path.GetFileName(sourcePath);
        ProjectName = Path.GetFileNameWithoutExtension(sourcePath);
        SelectedVoiceId = _voices.Any(v => v.Id == _defaultVoiceId)
            ? _defaultVoiceId
            : _voices.FirstOrDefault()?.Id;

        LoadPreview(sourcePath);
    }

    private void LoadPreview(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            SourcePreview = text.Length > 600 ? text[..600] + "\n…" : text;
            Recalculate();
        }
        catch (Exception ex)
        {
            SourcePreview = "";
            DialogService.OnUiThread(async () =>
                await AppServices.Instance.Dialogs.ShowErrorAsync("Не удалось прочитать файл", ex.Message));
        }
    }

    partial void OnTargetCharsChanged(int value) => Recalculate();

    private void Recalculate()
    {
        if (string.IsNullOrWhiteSpace(SourcePreview))
        {
            EstimatedParts = 0;
            return;
        }

        try
        {
            EstimatedParts = TextPartitioner.Split(SourcePreview, Math.Clamp(TargetChars, 50, 5000)).Count;
        }
        catch
        {
            EstimatedParts = 0;
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (IsBusy)
            return;

        if (string.IsNullOrWhiteSpace(ProjectName))
        {
            await AppServices.Instance.Dialogs.ShowErrorAsync(
                "Не указано название", "Введите название проекта.");
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedVoiceId))
        {
            await AppServices.Instance.Dialogs.ShowErrorAsync(
                "Не выбран голос",
                "Выберите пример голоса для проекта. Добавить голос можно в разделе «Голоса».");
            return;
        }

        IsBusy = true;
        try
        {
            var services = AppServices.Instance;
            var path = _sourcePath;

            CreatedProject = services.Projects.Create(
                path,
                ProjectName.Trim(),
                SelectedVoiceId!,
                Math.Clamp(TargetChars, 50, 5000));

            services.Settings.DefaultVoiceId = CreatedProject.VoiceId;
            services.SaveSettings(services.Settings);

            // Голос должен существовать и на сервере, иначе синтез не сработает.
            await EnsureVoiceOnServerAsync(CreatedProject.VoiceId);

            Closed?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            await AppServices.Instance.Dialogs.ShowErrorAsync("Не удалось создать проект", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnsureVoiceOnServerAsync(string voiceId)
    {
        try
        {
            var onServer = await _client.ListVoicesAsync();
            if (onServer.Contains(voiceId))
                return;

            var voice = _voices.FirstOrDefault(v => v.Id == voiceId);
            if (voice is null || !voice.AudioExists)
                return;

            await _client.AddVoiceAsync(voiceId, voice.AudioPath, voice.Text);
        }
        catch
        {
            // Если сервер недоступен — синтез покажет понятную ошибку при запуске.
        }
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);
}
