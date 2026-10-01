using Avalonia;
using System;
using readingFish.Services;

namespace readingFish;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppLog.Initialize();

        // Проверяем единственный экземпляр ДО создания окна и UI-цикла.
        // Иначе Avalonia успеет поднять диспетчер, и ранний выход приведёт к падению.
        using var guard = new SingleInstanceGuard();
        if (!guard.TryAcquire())
        {
            AppLog.Info("Приложение уже запущено — второй экземпляр закрыт");
            return;
        }

        AppLog.Info("Запуск readingFish");
        AppServices.Init();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLog.Error("Критическая ошибка при запуске", ex);
            throw;
        }
        finally
        {
            // Сервисы могли быть уже освобождены обработчиком Closed — повтор безопасен.
            AppServices.TryGetInstance()?.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
