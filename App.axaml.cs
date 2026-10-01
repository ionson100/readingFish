using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using readingFish.Services;
using readingFish.ViewModels;
using readingFish.Views;

namespace readingFish;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Ловим необработанные ошибки, чтобы пользователь видел текст, а не «????».
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppServices.Init();

            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };

            // Диалогам нужен владелец: модальность, буфер обмена, файловые диалоги.
            AppServices.Instance.Dialogs.Attach(window);
            window.Closed += (_, _) => AppServices.TryGetInstance()?.Dispose();

            desktop.MainWindow = window;
            AppLog.Info("Приложение запущено");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDispatcherUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Необработанная ошибка в UI", e.Exception);
        e.Handled = true;
        ShowError(e.Exception);
    }

    private void OnDomainUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error("Критическая ошибка", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Ошибка фоновой задачи", e.Exception);
        e.SetObserved();
    }

    private static void ShowError(Exception ex)
    {
        void Show()
        {
            if (!AppServices.IsInitialized)
                return;

            AppServices.Instance.Dialogs
                .ShowErrorAsync("Непредвиденная ошибка", ex.ToString())
                .GetAwaiter().GetResult();
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                Show();
            else
                Dispatcher.UIThread.Post(Show);
        }
        catch
        {
            // Если и диалог не открылся — пользователь увидит текст в консоли и журнале.
        }
    }
}
