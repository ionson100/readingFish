using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readingFish.Models;
using readingFish.Services;

namespace readingFish.ViewModels;

/// <summary>Управление примерами голоса: добавление, удаление, синхронизация с сервером.</summary>
public partial class VoicesViewModel : ViewModelBase
{
    public ObservableCollection<VoiceSample> Voices { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private VoiceSample? _selectedVoice;

    private readonly VoiceStore _store = AppServices.Instance.Voices;
    private readonly FishSpeechClient _client = AppServices.Instance.Client;

    [RelayCommand]
    private async Task AddVoiceAsync()
    {
        var dialogs = AppServices.Instance.Dialogs;
        var audioPath = await dialogs.PickAudioFileAsync();
        if (audioPath is null)
            return;

        // Предлагаем имя файла как имя голоса и просим текст записи.
        var suggestedId = Path.GetFileNameWithoutExtension(audioPath);
        var text = await RequestReferenceTextAsync(suggestedId);
        if (text is null)
            return;

        IsBusy = true;
        try
        {
            var voice = _store.Add(audioPath, text);

            // Отправляем на сервер, чтобы синтез мог использовать голос.
            try
            {
                await _client.AddVoiceAsync(voice.Id, voice.AudioPath, voice.Text);
            }
            catch (Exception ex)
            {
                var keep = await dialogs.ConfirmAsync(
                    "Голос не отправлен на сервер",
                    $"Голос сохранён локально, но сервер его не принял:\n\n{ex.Message}\n\n" +
                    "Оставить голос локально?",
                    "Оставить");

                if (!keep)
                {
                    _store.Delete(voice.Id);
                    return;
                }
            }

            await ReloadAsync();
            StatusText = $"Голос «{voice.Id}» добавлен.";
        }
        catch (Exception ex)
        {
            await dialogs.ShowErrorAsync("Не удалось добавить голос", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<string?> RequestReferenceTextAsync(string suggestedId)
    {
        var vm = new ReferenceTextViewModel(suggestedId);
        var ok = await AppServices.Instance.Dialogs.ShowReferenceTextDialogAsync(vm);
        return ok ? vm.Text : null;
    }

    [RelayCommand]
    private async Task DeleteVoiceAsync(VoiceSample? voice)
    {
        if (voice is null)
            return;

        var dialogs = AppServices.Instance.Dialogs;

        // Серверный голос без локальной папки удаляется только на сервере.
        var detail = voice.IsServerOnly
            ? $"Голос «{voice.DisplayName}» будет удалён с сервера. На этом компьютере его нет."
            : $"Голос «{voice.DisplayName}» будет удалён локально и с сервера.";

        var ok = await dialogs.ConfirmAsync(
            "Удалить голос",
            detail + "\n\nПроекты, использующие его, перестанут синтезироваться, " +
            "пока не смените голос.",
            "Удалить");

        if (!ok)
            return;

        IsBusy = true;
        try
        {
            try
            {
                await _client.DeleteVoiceAsync(voice.Id);
            }
            catch
            {
                // Сервер мог не знать этот голос — не мешает удалить локально.
            }

            // У серверного голоса папки нет: Delete бросил бы исключение.
            if (voice.LocalExists)
                _store.Delete(voice.Id);

            await ReloadAsync();
            StatusText = $"Голос «{voice.Id}» удалён.";
        }
        catch (Exception ex)
        {
            await dialogs.ShowErrorAsync("Не удалось удалить голос", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync();

    /// <summary>
    /// Перечитывает голоса и сверяет их с сервером. Показываем объединение:
    /// сервер знает голоса, которых нет на этом компьютере, и наоборот.
    /// </summary>
    public async Task ReloadAsync()
    {
        var local = _store.LoadAll();

        List<string> serverIds;
        try
        {
            serverIds = await _client.ListVoicesAsync();
        }
        catch (Exception ex)
        {
            // Без сервера показываем локальные голоса и честно говорим об этом.
            foreach (var v in local)
                v.IsOnServer = false;

            Fill(local);
            StatusText = $"Сервер недоступен, показаны только локальные голоса " +
                $"({local.Count} шт.): {ex.Message.Split('\n')[0]}";
            return;
        }

        var merged = _store.Merge(local, serverIds);
        Fill(merged);

        var serverOnly = merged.Count(v => v.IsServerOnly);
        var localOnly = merged.Count(v => v.IsLocalOnly);

        StatusText = BuildStatus(merged.Count, serverIds.Count, serverOnly, localOnly);
    }

    private static string BuildStatus(int total, int onServer, int serverOnly, int localOnly)
    {
        var parts = new List<string> { $"всего {total}" };

        if (onServer == 0)
            return "На сервере нет ни одного голоса. Показан список с этого компьютера.";

        parts.Add($"на сервере {onServer}");

        if (serverOnly > 0)
            parts.Add($"{serverOnly} только на сервере — синтез сработает, прослушать нельзя");

        if (localOnly > 0)
            parts.Add($"{localOnly} только локально — на сервер не отправлен");

        return string.Join(" · ", parts);
    }

    private void Fill(List<VoiceSample> voices)
    {
        Voices.Clear();
        foreach (var v in voices)
            Voices.Add(v);
    }
}

/// <summary>Ввод имени голоса и текста записи при добавлении.</summary>
public partial class ReferenceTextViewModel : ViewModelBase
{
    [ObservableProperty] private string _voiceId = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string? _errorText;

    public event EventHandler<bool>? Closed;

    public ReferenceTextViewModel(string suggestedId)
    {
        _voiceId = AppPaths.Sanitize(suggestedId);
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(VoiceId))
        {
            ErrorText = "Укажите имя голоса.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Text))
        {
            ErrorText = "Укажите текст, произнесённый в записи.";
            return;
        }

        ErrorText = null;
        Closed?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);
}
