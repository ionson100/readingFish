using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readingFish.Models;
using readingFish.Services;

namespace readingFish.ViewModels;

/// <summary>Редактирование текста одной части проекта.</summary>
public partial class EditPartViewModel : ViewModelBase
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private int _index;
    [ObservableProperty] private int _charCount;

    public string? ResultText { get; private set; }

    public event EventHandler<bool>? Closed;

    public EditPartViewModel(int index, string text)
    {
        _index = index;
        _text = text;
        _charCount = text.Length;
    }

    public string Title => $"Часть №{Index + 1}";

    partial void OnTextChanged(string value)
    {
        CharCount = value.Length;
        OnPropertyChanged(nameof(CharCount));
    }

    [RelayCommand]
    private void Save()
    {
        ResultText = Text;
        Closed?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);
}
