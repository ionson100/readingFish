using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace readingFish.ViewModels;

/// <summary>Диалог подтверждения. True — пользователь согласился.</summary>
public partial class ConfirmViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = "Подтверждение";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private string _confirmText = "Да";

    [ObservableProperty]
    private string _cancelText = "Отмена";

    public event EventHandler<bool>? Closed;

    public ConfirmViewModel()
    {
    }

    public ConfirmViewModel(string title, string message, string confirmText = "Да")
    {
        _title = title;
        _message = message;
        _confirmText = confirmText;
    }

    [RelayCommand]
    private void Confirm() => Closed?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);
}
