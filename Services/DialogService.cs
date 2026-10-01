using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace readingFish.Services;

/// <summary>
/// Диалоги и системные вызовы: файловые диалоги, подтверждения, показ ошибок,
/// буфер обмена, открытие папки.
/// </summary>
public sealed class DialogService
{
    private Window? _owner;

    public void Attach(Window owner) => _owner = owner;

    private Window? Owner => _owner;

    // ---------- диалоги ----------

    public async Task ShowErrorAsync(string title, string message)
    {
        await ShowDialogAsync(new Views.ErrorDialog
        {
            DataContext = ViewModels.MessageViewModel.Error(title, message)
        });
    }

    public async Task ShowInfoAsync(string title, string message)
    {
        await ShowDialogAsync(new Views.ErrorDialog
        {
            DataContext = ViewModels.MessageViewModel.Info(title, message)
        });
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Да")
    {
        var result = await ShowDialogAsync(new Views.ConfirmDialog
        {
            DataContext = new ViewModels.ConfirmViewModel(title, message, confirmText)
        });

        return result is true;
    }

    /// <summary>Диалог создания проекта. True — проект создан.</summary>
    public async Task<bool> ShowCreateProjectDialogAsync(ViewModels.CreateProjectViewModel vm)
    {
        var result = await ShowDialogAsync(new Views.CreateProjectDialog { DataContext = vm });
        return result is true && vm.CreatedProject is not null;
    }

    /// <summary>Диалог редактирования части. True — текст подтверждён.</summary>
    public async Task<bool> ShowEditPartDialogAsync(ViewModels.EditPartViewModel vm)
    {
        var result = await ShowDialogAsync(new Views.EditPartDialog { DataContext = vm });
        return result is true;
    }

    /// <summary>Диалог параметров синтеза. True — пользователь нажал «Сохранить».</summary>
    public async Task<bool> ShowSynthesisOptionsDialogAsync(ViewModels.SynthesisOptionsViewModel vm)
    {
        var result = await ShowDialogAsync(new Views.SynthesisOptionsDialog { DataContext = vm });
        return result is true;
    }

    /// <summary>Диалог ввода имени голоса и текста записи.</summary>
    public async Task<bool> ShowReferenceTextDialogAsync(ViewModels.ReferenceTextViewModel vm)
    {
        var result = await ShowDialogAsync(new Views.ReferenceTextDialog { DataContext = vm });
        return result is true;
    }

    private async Task<object?> ShowDialogAsync(Window window)
    {
        if (Owner is { } owner && owner.IsVisible)
            return await window.ShowDialog<object?>(owner);

        // Без владельца окно показать нельзя — закрываем сразу, чтобы не блокировать.
        window.Close(false);
        return null;
    }

    // ---------- файловые диалоги ----------

    private static FilePickerFileType AllFiles => new("Все файлы") { Patterns = new[] { "*" } };

    /// <summary>Выбор текстового файла. Возвращает полный путь или null.</summary>
    public async Task<string?> PickTextFileAsync()
    {
        if (Owner is not { } owner)
            return null;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите файл с текстом перевода",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Текстовые файлы") { Patterns = new[] { "*.txt", "*.md", "*.text" } },
                AllFiles
            }
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Выбор аудиофайла для примера голоса.</summary>
    public async Task<string?> PickAudioFileAsync()
    {
        if (Owner is not { } owner)
            return null;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите запись голоса",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Аудио")
                {
                    Patterns = new[] { "*.wav", "*.mp3", "*.flac", "*.ogg", "*.m4a", "*.opus", "*.aac" }
                },
                AllFiles
            }
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    // ---------- системные вызовы ----------

    public async Task CopyToClipboardAsync(string text)
    {
        if (Owner is not { } owner || owner.Clipboard is not { } clipboard)
            return;

        await clipboard.SetTextAsync(text);
    }

    /// <summary>Открывает папку в файловом менеджере системы.</summary>
    public bool OpenInFileManager(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
                return true;
            }

            if (File.Exists(path))
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer",
                        Arguments = $"/select,\"{path}\""
                    });
                    return true;
                }

                // В Linux выделяем файл в файловом менеджере, если он его поддерживает.
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                return true;
            }
        }
        catch
        {
            // Файлового менеджера нет — покажем путь пользователю вручную.
        }

        return false;
    }

    /// <summary>Безопасный вызов с UI-потока (нужно из фоновых задач).</summary>
    public static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
