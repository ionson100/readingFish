using readingFish.Models;
using readingFish.Services;

namespace readingFish.Services;

/// <summary>
/// Простой контейнер сервисов. Приложение небольшое, полноценный DI-контейнер
/// здесь был бы избыточен.
/// </summary>
public sealed class AppServices : IDisposable
{
    private static AppServices? _instance;

    public static AppServices Instance =>
        _instance ?? throw new InvalidOperationException("Сервисы не инициализированы.");

    public static bool IsInitialized => _instance is not null;

    /// <summary>
    /// Безопасный доступ: возвращает null вместо исключения, если сервисы
    /// ещё не созданы или уже освобождены. Для финальной очистки при выходе.
    /// </summary>
    public static AppServices? TryGetInstance() => _instance;

    public AppSettings Settings { get; private set; }
    public SettingsStore SettingsStore { get; }
    public FishSpeechClient Client { get; private set; }
    public ProjectStore Projects { get; }
    public VoiceStore Voices { get; }
    public SynthesisService Synthesis { get; }
    public DialogService Dialogs { get; }
    public AudioPlayer Audio { get; }

    private AppServices()
    {
        SettingsStore = new SettingsStore();
        Settings = SettingsStore.Load();

        Client = new FishSpeechClient(Settings.ServerUrl);
        Projects = new ProjectStore();
        Voices = new VoiceStore();
        Synthesis = new SynthesisService(Client, Projects);
        Dialogs = new DialogService();
        Audio = new AudioPlayer();
    }

    public static void Init()
    {
        if (_instance is not null)
            return;

        AppPaths.EnsureCreated();
        _instance = new AppServices();
    }

    /// <summary>Перечитывает настройки и применяет новый адрес сервера.</summary>
    public void ReloadSettings()
    {
        Settings = SettingsStore.Load();
        Client.UpdateServerUrl(Settings.ServerUrl);
    }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        SettingsStore.Save(settings);
        Client.UpdateServerUrl(settings.ServerUrl);
    }

    private bool _disposed;

    /// <summary>Освобождает ресурсы. Повторный вызов безопасен.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Audio.Dispose();
        Client.Dispose();

        if (ReferenceEquals(_instance, this))
            _instance = null;
    }
}
