using readingFish.Models;

namespace readingFish.Services;

/// <summary>
/// Гарантирует, что запущен только один экземпляр приложения.
/// Использует эксклюзивный lock-файл — работает и в Windows, и в Linux.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private FileStream? _stream;

    public bool TryAcquire()
    {
        AppPaths.EnsureCreated();

        try
        {
            _stream = new FileStream(
                AppPaths.LockFile,
                mode: FileMode.OpenOrCreate,
                access: FileAccess.ReadWrite,
                share: FileShare.None);

            var info = $"pid={Environment.ProcessId}{Environment.NewLine}";
            _stream.SetLength(0);
            _stream.Write(System.Text.Encoding.UTF8.GetBytes(info));
            _stream.Flush();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // освобождение лока не должно бросать исключение при выходе
        }
    }
}
