using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readingFish.Models;
using readingFish.Services;

namespace readingFish.ViewModels;

/// <summary>Список проектов + кнопка «Создать проект».</summary>
public partial class ProjectsViewModel : ViewModelBase
{
    public ObservableCollection<Project> Projects { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private Project? _selectedProject;
    [ObservableProperty] private string _emptyMessage = "Проектов пока нет. Создайте первый.";

    public bool HasProjects => Projects.Count > 0;
    public bool HasNoProjects => !HasProjects;

    /// <summary>Вызывается главным окном, чтобы открыть карточку проекта.</summary>
    public event EventHandler<Project>? ProjectOpened;

    [RelayCommand]
    private async Task CreateProjectAsync()
    {
        var services = AppServices.Instance;
        var sourcePath = await services.Dialogs.PickTextFileAsync();
        if (sourcePath is null)
            return;

        // Голоса показываем вместе с серверными: голос, который знает только
        // сервер, тоже подходит для синтеза.
        var voices = await LoadVoicesWithServerAsync(services.Voices.LoadAll());

        var vm = new CreateProjectViewModel(sourcePath, voices, services.Settings.DefaultVoiceId);
        var accepted = await services.Dialogs.ShowCreateProjectDialogAsync(vm);
        if (!accepted)
            return;

        await ReloadAsync();

        if (vm.CreatedProject is not null)
        {
            SelectedProject = Projects.FirstOrDefault(p => p.Id == vm.CreatedProject.Id);
            ProjectOpened?.Invoke(this, vm.CreatedProject);
        }
    }

    [RelayCommand]
    private void OpenProject(Project? project)
    {
        if (project is null)
            return;

        ProjectOpened?.Invoke(this, project);
    }

    /// <summary>
    /// Соединяет локальные голоса с серверными. Если сервер недоступен,
    /// отдаём локальные — создать проект всё равно можно.
    /// </summary>
    private static async Task<List<VoiceSample>> LoadVoicesWithServerAsync(List<VoiceSample> local)
    {
        try
        {
            var serverIds = await AppServices.Instance.Client.ListVoicesAsync();
            return AppServices.Instance.Voices.Merge(local, serverIds);
        }
        catch (Exception ex)
        {
            AppLog.Info($"Список голосов с сервера не получен: {ex.Message.Split('\n')[0]}");
            foreach (var v in local)
                v.IsOnServer = false;

            return local;
        }
    }

    [RelayCommand]
    private async Task DeleteProjectAsync(Project? project)
    {
        if (project is null)
            return;

        var services = AppServices.Instance;
        var ok = await services.Dialogs.ConfirmAsync(
            "Удалить проект",
            $"Проект «{project.Name}» и все его аудиофайлы будут удалены без возможности восстановления.\n\n" +
            $"Папка: {project.Dir}",
            "Удалить");

        if (!ok)
            return;

        try
        {
            services.Projects.Delete(project);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            await services.Dialogs.ShowErrorAsync("Не удалось удалить проект", ex.Message);
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync(Project? project)
    {
        if (project is null)
            return;

        if (!AppServices.Instance.Dialogs.OpenInFileManager(project.Dir))
        {
            await AppServices.Instance.Dialogs.ShowErrorAsync(
                "Не удалось открыть папку",
                $"Откройте папку вручную:\n\n{project.Dir}");
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync();

    public async Task ReloadAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var list = AppServices.Instance.Projects.LoadAll();
            Projects.Clear();
            foreach (var p in list)
                Projects.Add(p);

            EmptyMessage = Projects.Count == 0
                ? "Проектов пока нет. Создайте первый."
                : string.Empty;

            OnPropertyChanged(nameof(HasProjects));
            OnPropertyChanged(nameof(HasNoProjects));
        }
        catch (Exception ex)
        {
            await AppServices.Instance.Dialogs.ShowErrorAsync("Ошибка загрузки проектов", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
