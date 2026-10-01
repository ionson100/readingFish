using System.Text;
using readingFish.Models;

namespace readingFish.Services;

/// <summary>
/// Пишет журнал в ~/fishApp/logs/app.log всегда в UTF-8 и дублирует в консоль.
/// Нужен, чтобы диагностировать проблемы: вывод в терминал может искажать кириллицу.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _file;

    public static string FilePath => _file ??= Path.Combine(AppPaths.LogsDir, "app.log");

    public static void Initialize()
    {
        try
        {
            AppPaths.EnsureCreated();
            Directory.CreateDirectory(AppPaths.LogsDir);

            // Консоль в UTF-8, иначе кириллица превращается в «????».
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch
            {
                // Не всем терминалам это доступно — не критично.
            }

            _file = FilePath;
        }
        catch
        {
            // Журнал не обязателен.
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
          .Append(" [").Append(level).Append("] ")
          .Append(message);

        if (ex is not null)
        {
            sb.AppendLine();
            sb.Append("    ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            if (ex.StackTrace is not null)
                sb.AppendLine().Append(ex.StackTrace);
        }

        var line = sb.ToString();

        try
        {
            lock (Gate)
            {
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);

                // Ограничиваем размер, чтобы журнал не рос бесконечно.
                var info = new FileInfo(FilePath);
                if (info.Length > 2 * 1024 * 1024)
                    RollOver();
            }
        }
        catch
        {
            // Не пишем ошибку об ошибке.
        }

        if (level == "ERROR")
            Console.Error.WriteLine(line);
        else
            Console.WriteLine(line);
    }

    private static void RollOver()
    {
        try
        {
            var old = FilePath + ".1";
            if (File.Exists(old))
                File.Delete(old);

            File.Move(FilePath, old);
        }
        catch
        {
            // Если не вышло — просто продолжим писать.
        }
    }
}
