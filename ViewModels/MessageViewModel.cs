using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readingFish.Services;

namespace readingFish.ViewModels;

/// <summary>Диалог сообщения. Может показывать ошибку или информацию.</summary>
public partial class MessageViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _header = "Ошибка";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isCopied;

    /// <summary>Вид подписывается и закрывает окно.</summary>
    public event EventHandler? CloseRequested;

    public bool IsError { get; private init; } = true;

    public string FullText => string.IsNullOrWhiteSpace(Title) ? Message : $"{Title}\n\n{Message}";

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(FullText));

    public static MessageViewModel Error(string title, string message) => new()
    {
        Title = title,
        Header = "Произошла ошибка",
        Message = message,
        IsError = true
    };

    public static MessageViewModel Info(string title, string message) => new()
    {
        Title = title,
        Header = "Информация",
        Message = message,
        IsError = false
    };

    [RelayCommand]
    private async Task CopyAsync()
    {
        await AppServices.Instance.Dialogs.CopyToClipboardAsync(FullText);
        IsCopied = true;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>Совместимое имя: старые вызовы ErrorViewModel продолжают работать.</summary>
public partial class ErrorViewModel : MessageViewModel
{
    public ErrorViewModel()
    {
    }

    public ErrorViewModel(string title, string message)
    {
        Title = title;
        Message = message;
    }
}
