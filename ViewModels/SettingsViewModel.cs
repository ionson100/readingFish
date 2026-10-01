using readingFish.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readingFish.Services;

namespace readingFish.ViewModels;

/// <summary>Настройки: адрес сервера, параметры генерации, разбиения текста.</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services = AppServices.Instance;

    [ObservableProperty] private string _serverUrl = "";
    [ObservableProperty] private int _targetPartChars = 200;
    [ObservableProperty] private int _maxNewTokens = 1024;
    [ObservableProperty] private double _temperature = 0.7;
    [ObservableProperty] private string _serverStatus = "не проверялось";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _appFolder = "";

    public event EventHandler? BackRequested;

    public SettingsViewModel()
    {
        var s = _services.Settings;
        _serverUrl = s.ServerUrl;
        _targetPartChars = s.TargetPartChars;
        _maxNewTokens = s.MaxNewTokens;
        _temperature = s.Temperature;
        _appFolder = AppPaths.Root;
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task SaveAsync()
    {
        var settings = _services.Settings;
        settings.ServerUrl = ServerUrl.Trim();
        settings.TargetPartChars = Math.Clamp(TargetPartChars, 50, 5000);
        settings.MaxNewTokens = Math.Clamp(MaxNewTokens, 64, 4096);
        settings.Temperature = Math.Clamp(Temperature, 0.1, 1.0);

        TargetPartChars = settings.TargetPartChars;
        MaxNewTokens = settings.MaxNewTokens;
        Temperature = settings.Temperature;

        _services.SaveSettings(settings);
        await _services.Dialogs.ShowInfoAsync("Сохранено", "Настройки записаны в settings.json.");
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        ServerStatus = "проверяем...";
        try
        {
            _services.Client.UpdateServerUrl(ServerUrl.Trim());
            var ok = await _services.Client.PingAsync();
            ServerStatus = ok
                ? $"сервер доступен ({_services.Client.ServerUrl})"
                : "сервер не отвечает";
        }
        catch (Exception ex)
        {
            ServerStatus = "ошибка: " + ex.Message.Split('\n')[0];
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenAppFolderAsync()
    {
        if (!_services.Dialogs.OpenInFileManager(AppPaths.Root))
        {
            await _services.Dialogs.ShowErrorAsync(
                "Не удалось открыть папку", $"Откройте вручную:\n\n{AppPaths.Root}");
        }
    }
}
