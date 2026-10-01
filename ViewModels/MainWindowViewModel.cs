using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readingFish.Models;
using readingFish.Services;

namespace readingFish.ViewModels;

/// <summary>Главное окно: навигация между разделами.</summary>
public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty] private ViewModelBase? _currentPage;
    [ObservableProperty] private string _currentPageTitle = "Проекты";
    [ObservableProperty] private int _selectedNavIndex;

    public ProjectsViewModel ProjectsPage { get; }
    public VoicesViewModel VoicesPage { get; }
    public SettingsViewModel SettingsPage { get; }

    public MainWindowViewModel()
    {
        ProjectsPage = new ProjectsViewModel();
        VoicesPage = new VoicesViewModel();
        SettingsPage = new SettingsViewModel();

        ProjectsPage.ProjectOpened += OnProjectOpened;
        SettingsPage.BackRequested += (_, _) => _ = ShowProjectsAsync();
        ProjectPageHolder.BackRequested += (_, _) => _ = BackFromProjectAsync();

        // Стартовая страница — список проектов.
        SelectedNavIndex = 0;
        CurrentPageTitle = "Проекты";
        CurrentPage = ProjectsPage;
    }

    // Отдельные команды без параметров: RelayCommand<T> в XAVAML спотыкается
    // о строковый CommandParameter и бросает ArgumentException.

    [RelayCommand]
    private async Task ShowProjectsAsync()
    {
        LeaveProject();
        SelectedNavIndex = 0;
        CurrentPageTitle = "Проекты";
        CurrentPage = ProjectsPage;
        await ProjectsPage.ReloadAsync();
    }

    [RelayCommand]
    private async Task ShowVoicesAsync()
    {
        LeaveProject();
        SelectedNavIndex = 1;
        CurrentPageTitle = "Примеры голоса";
        CurrentPage = VoicesPage;
        await VoicesPage.ReloadAsync();
    }

    [RelayCommand]
    private void ShowSettings()
    {
        LeaveProject();
        SelectedNavIndex = 2;
        CurrentPageTitle = "Настройки";
        CurrentPage = SettingsPage;
    }

    /// <summary>
    /// «Назад» на странице проекта: возвращаемся к списку и освобождаем карточку.
    /// Раньше кнопка только звала освобождение, а страница оставалась прежней —
    /// поэтому казалось, что она ничего не делает.
    /// </summary>
    [RelayCommand]
    private async Task BackFromProjectAsync()
    {
        await ShowProjectsAsync();
    }

    /// <summary>Освобождает карточку проекта, если она открыта.</summary>
    private static void LeaveProject() => ProjectPageHolder.Release();

    private void OnProjectOpened(object? sender, Project project)
    {
        ProjectPageHolder.Open(project);
        CurrentPageTitle = "Проект";
        CurrentPage = ProjectPageHolder.Current;
        SelectedNavIndex = -1;
    }

    /// <summary>Текущая карточка проекта и её освобождение при уходе со страницы.</summary>
    public static class ProjectPageHolder
    {
        private static ProjectViewModel? _current;

        public static ProjectViewModel? Current => _current;

        /// <summary>Нажата «Назад» на странице проекта.</summary>
        public static event EventHandler? BackRequested;

        public static void Open(Project project)
        {
            var vm = new ProjectViewModel(project);
            vm.BackRequested += (_, _) => BackRequested?.Invoke(null, EventArgs.Empty);
            _current?.Dispose();
            _current = vm;
        }

        /// <summary>
        /// Освобождает карточку проекта. Вызывается при любом уходе со
        /// страницы — и по «Назад», и по пункту меню: иначе она остаётся
        /// подписанной на плеер и удерживает звук.
        /// </summary>
        public static void Release()
        {
            var vm = _current;
            _current = null;
            vm?.Dispose();
        }
    }
}
