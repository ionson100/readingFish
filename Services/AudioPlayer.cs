using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace readingFish.Services;

public enum AudioPlaybackResult
{
    Started,
    StartedExternally,
    Failed
}

/// <summary>Состояние плеера — на него подписан интерфейс.</summary>
public enum AudioPlaybackState
{
    Idle,
    Playing,
    Finished,
    Stopped,
    Failed
}

public sealed class AudioPlaybackChangedEventArgs : EventArgs
{
    public required AudioPlaybackState State { get; init; }

    public string? Path { get; init; }

    /// <summary>Можно ли остановить воспроизведение из приложения.</summary>
    public required bool CanStop { get; init; }

    /// <summary>Файл открыт системным плеером — момент окончания нам неизвестен.</summary>
    public required bool IsExternal { get; init; }
}

/// <summary>
/// Воспроизведение wav. Основной путь — встроенный проигрыватель
/// (winmm в Windows, ALSA в Linux). Если аудиоустройство недоступно
/// или формат нечем играть, файл открывается системным плеером.
/// Одновременно звучит только один файл: новый вызов снимает предыдущий.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly object _gate = new();
    private IAudioBackend? _backend;
    private IAudioSession? _session;
    private string? _currentPath;
    private bool _canStop;
    private bool _isExternal;
    private int _generation;

    /// <summary>
    /// Событие приходит из потока драйвера — подписчик обязан сам
    /// переключиться на UI-поток.
    /// </summary>
    public event EventHandler<AudioPlaybackChangedEventArgs>? PlaybackChanged;

    public AudioPlaybackState State { get; private set; } = AudioPlaybackState.Idle;

    public string? CurrentPath => _currentPath;

    public bool IsPlaying => State == AudioPlaybackState.Playing;

    public bool CanStop => _canStop;

    public bool IsExternal => _isExternal;

    public async Task<AudioPlaybackResult> PlayAsync(string path)
    {
        if (!File.Exists(path))
        {
            SetState(AudioPlaybackState.Failed, path, canStop: false);
            return AudioPlaybackResult.Failed;
        }

        // Новое воспроизведение всегда снимает предыдущее.
        Stop();

        PcmAudio? pcm = null;
        string? problem = null;

        try
        {
            // Читаем в фоновом потоке: result.wav длинной главы весит десятки
            // мегабайт, и синхронное чтение подвешивало бы интерфейс.
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            pcm = WavTools.DecodeToPcm16(bytes);
            if (pcm is null)
                problem = "Формат аудио не поддерживается встроенным плеером.";
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            AppLog.Error($"Не удалось прочитать аудиофайл {path}", ex);
        }

        if (pcm is not null && GetBackend() is { } backend)
        {
            int generation;
            lock (_gate)
            {
                generation = ++_generation;
            }

            var session = backend.TryStart(pcm, () => OnSessionEnded(generation), out problem);

            if (session is not null)
            {
                lock (_gate)
                {
                    _session = session;
                    _currentPath = path;
                    _canStop = true;
                    _isExternal = false;
                }

                SetState(AudioPlaybackState.Playing, path, canStop: true);
                return AudioPlaybackResult.Started;
            }

            AppLog.Info($"Встроенный плеер не включил {Path.GetFileName(path)}: {problem}");
        }

        // Встроенный плеер не справился — отдаём файл системному.
        if (OpenExternally(path))
        {
            lock (_gate)
            {
                _session = null;
                _currentPath = path;
                _canStop = false;
                _isExternal = true;
            }

            SetState(AudioPlaybackState.Playing, path, canStop: false, isExternal: true);
            return AudioPlaybackResult.StartedExternally;
        }

        SetState(AudioPlaybackState.Failed, path, canStop: false);
        return AudioPlaybackResult.Failed;
    }

    /// <summary>Останавливает текущее воспроизведение. False — играть нечему.</summary>
    public bool Stop()
    {
        IAudioSession? session;
        string? path;

        lock (_gate)
        {
            if (_session is null)
                return false;

            session = _session;
            _session = null;
            path = _currentPath;
            _currentPath = null;
            _canStop = false;
            _isExternal = false;

            // Позднее уведомление от остановленной сессии больше не устроит.
            _generation++;
        }

        session.Stop();
        SetState(AudioPlaybackState.Stopped, path, canStop: false);
        return true;
    }

    public void Dispose() => Stop();

    private void OnSessionEnded(int generation)
    {
        string? path;

        lock (_gate)
        {
            if (generation != _generation)
                return;     // сессия уже не текущая: состояние перебито новым воспроизведением

            _session = null;
            path = _currentPath;
            _currentPath = null;
            _canStop = false;
            _isExternal = false;
        }

        SetState(AudioPlaybackState.Finished, path, canStop: false);
    }

    private IAudioBackend? GetBackend()
    {
        lock (_gate)
        {
            if (_backend is not null)
                return _backend;

            _backend = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new WinMmBackend()
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? new AlsaBackend()
                    : null;

            return _backend;
        }
    }

    private void SetState(AudioPlaybackState state, string? path, bool canStop, bool isExternal = false)
    {
        State = state;
        PlaybackChanged?.Invoke(this, new AudioPlaybackChangedEventArgs
        {
            State = state,
            Path = path,
            CanStop = canStop,
            IsExternal = isExternal
        });
    }

    private static bool OpenExternally(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Один запущенный сеанс воспроизведения.</summary>
internal interface IAudioSession : IDisposable
{
    /// <summary>Запрашивает остановку. Память освобождает сам сеанс.</summary>
    void Stop();
}

internal interface IAudioBackend
{
    /// <summary>
    /// Начинает воспроизведение. onEnded вызывается, когда звук доиграл или остановлен.
    /// null в ответе — устройство недоступно или формат не подошёл, причина в error.
    /// </summary>
    IAudioSession? TryStart(PcmAudio pcm, Action onEnded, out string? error);
}

// ---------------------------------------------------------------------------------------------
// Windows: winmm, waveOut с колбэком завершения
// ---------------------------------------------------------------------------------------------

internal sealed class WinMmBackend : IAudioBackend
{
    internal const uint WaveMapper = 0xFFFFFFFF;
    internal const uint CallbackFunction = 0x00030000;

    private const ushort WaveFormatPcm = 1;

    // WOM_DONE из mmreg.h. Не путать с WHDR_DONE (0x1) в dwFlags заголовка:
    // это разные вещи, и фильтр по неверному значению молча теряет завершение.
    private const uint WaveMessageDone = 0x03BD;

    internal delegate void WaveProc(
        IntPtr hwo, uint message, IntPtr instance, IntPtr param, IntPtr reserved);

    // Делегат держим в статическом поле: иначе GC соберёт его, и драйвер
    // продолжит писать в освобождённую память.
    private static readonly WaveProc Callback = OnWaveProc;
    internal static readonly IntPtr CallbackPtr = Marshal.GetFunctionPointerForDelegate(Callback);

    // sizeof(WAVEHDR) различается на x86 (32) и x64 (48), поэтому размер
    // берём у структуры, а не задаём константой.
    internal static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();

    // Драйвер сообщает о завершении, зная только свой handle, поэтому сессию
    // адресуем по идентификатору, переданному в dwInstance при открытии.
    private static readonly ConcurrentDictionary<int, WinMmSession> Active = new();

    internal static int LastId;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveFormat
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
    }

    // Часть полей WAVEHDR заполняет сам драйвер — предупреждения о неиспользуемых полях ожидаемы.
#pragma warning disable CS0649
    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveHeader
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }
#pragma warning restore CS0649

    [DllImport("winmm.dll")]
    internal static extern int waveOutOpen(out IntPtr hWaveOut, uint deviceId, ref WaveFormat format,
        IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    internal static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    internal static extern int waveOutWrite(IntPtr hWaveOut, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    internal static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    internal static extern int waveOutClose(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    internal static extern int waveOutReset(IntPtr hWaveOut);

    public IAudioSession? TryStart(PcmAudio pcm, Action onEnded, out string? error)
    {
        error = null;

        var blockAlign = (ushort)(pcm.Channels * 2);
        var length = pcm.Samples.Length * 2;
        length -= length % blockAlign;       // длина обязана быть кратна nBlockAlign
        if (length <= 0)
        {
            error = "Аудиофайл не содержит отсчётов.";
            return null;
        }

        var format = new WaveFormat
        {
            wFormatTag = WaveFormatPcm,
            nChannels = (ushort)pcm.Channels,
            nSamplesPerSec = (uint)pcm.SampleRate,
            wBitsPerSample = 16
        };
        format.nBlockAlign = blockAlign;
        format.nAvgBytesPerSec = format.nSamplesPerSec * blockAlign;

        var raw = new byte[length];
        Buffer.BlockCopy(pcm.Samples, 0, raw, 0, length);

        var session = new WinMmSession(raw, onEnded);
        if (session.Start(format, out error))
            return session;

        session.Release();
        return null;
    }

    internal static void Register(int id, WinMmSession session) => Active[id] = session;

    internal static void Unregister(int id) => Active.TryRemove(id, out _);

    private static void OnWaveProc(IntPtr hwo, uint message, IntPtr instance, IntPtr param, IntPtr reserved)
    {
        if (message != WaveMessageDone)
            return;

        if (Active.TryGetValue((int)instance, out var session))
            session.SignalDone();
    }
}

/// <summary>
/// Один вызов waveOut. Память освобождается только после того, как драйвер
/// закончил с ней работать: иначе он читает уже освобождённый буфер.
/// </summary>
internal sealed class WinMmSession : IAudioSession
{
    private readonly byte[] _raw;
    private readonly Action _onEnded;
    private readonly ManualResetEventSlim _done = new(false);

    private GCHandle _pinned;
    private IntPtr _device;
    private IntPtr _header;
    private int _id;
    private bool _prepared;
    private int _released;

    public WinMmSession(byte[] raw, Action onEnded)
    {
        _raw = raw;
        _onEnded = onEnded;
    }

    public bool Start(WinMmBackend.WaveFormat format, out string? error)
    {
        error = null;

        _id = Interlocked.Increment(ref WinMmBackend.LastId);
        WinMmBackend.Register(_id, this);

        var opened = WinMmBackend.waveOutOpen(out _device, WinMmBackend.WaveMapper, ref format,
            WinMmBackend.CallbackPtr, (IntPtr)_id, WinMmBackend.CallbackFunction);

        if (opened != 0)
        {
            WinMmBackend.Unregister(_id);
            _device = IntPtr.Zero;
            error = $"Не удалось открыть устройство вывода звука (waveOutOpen = {opened}).";
            return false;
        }

        // Закрепляем массив: адрес не должен двигаться, пока драйвер читает буфер.
        _pinned = GCHandle.Alloc(_raw, GCHandleType.Pinned);
        _header = Marshal.AllocHGlobal((int)WinMmBackend.HeaderSize);

        var header = new WinMmBackend.WaveHeader
        {
            lpData = _pinned.AddrOfPinnedObject(),
            dwBufferLength = (uint)_raw.Length,

            // dwFlags намеренно 0: WHDR_DONE выставляет драйвер, когда буфер доигран.
            // Если выставить его заранее, ожидание завершения отработает мгновенно
            // и память освободится под работающим драйвером.
            dwFlags = 0
        };
        Marshal.StructureToPtr(header, _header, false);

        var prepared = WinMmBackend.waveOutPrepareHeader(_device, _header, WinMmBackend.HeaderSize);
        if (prepared != 0)
        {
            error = $"Драйвер отклонил буфер (waveOutPrepareHeader = {prepared}).";
            return false;
        }

        _prepared = true;

        var written = WinMmBackend.waveOutWrite(_device, _header, WinMmBackend.HeaderSize);
        if (written != 0)
        {
            error = $"Драйвер не принял звук (waveOutWrite = {written}).";
            return false;
        }

        // Поток только ждёт: освобождать память должен он, и лишь после сигнала.
        var waiter = new Thread(WaitAndRelease) { IsBackground = true, Name = "winmm-wav" };
        waiter.Start();
        return true;
    }

    private void WaitAndRelease()
    {
        try
        {
            _done.Wait();
        }
        catch
        {
            // Прерванное ожидание не мешает освобождению ресурсов.
        }
        finally
        {
            Release();
            try
            {
                _onEnded();
            }
            catch
            {
                // Уведомление UI не должно ронять поток.
            }
        }
    }

    internal void SignalDone() => _done.Set();

    public void Stop()
    {
        var device = _device;
        if (device == IntPtr.Zero || Volatile.Read(ref _released) != 0)
        {
            // Потока ожидания нет — освобождаем на месте.
            Release();
            return;
        }

        // Сброс отменяет всю очередь, после него драйвер перестаёт читать буфер.
        WinMmBackend.waveOutReset(device);
        _done.Set();
    }

    /// <summary>Закрывает устройство и отпускает память. Повторный вызов безопасен.</summary>
    internal void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;

        WinMmBackend.Unregister(_id);

        if (_device != IntPtr.Zero)
        {
            WinMmBackend.waveOutReset(_device);

            // Порядок важен: unprepare имеет смысл только после reset,
            // иначе драйвер вернёт WAVERR_STILLPLAYING.
            if (_prepared)
                WinMmBackend.waveOutUnprepareHeader(_device, _header, WinMmBackend.HeaderSize);

            WinMmBackend.waveOutClose(_device);
            _device = IntPtr.Zero;
        }

        if (_header != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_header);
            _header = IntPtr.Zero;
        }

        if (_pinned.IsAllocated)
            _pinned.Free();
    }

    public void Dispose() => Stop();
}

// ---------------------------------------------------------------------------------------------
// Linux: ALSA
// ---------------------------------------------------------------------------------------------

internal sealed class AlsaBackend : IAudioBackend
{
    public IAudioSession? TryStart(PcmAudio pcm, Action onEnded, out string? error)
    {
        error = null;
        var session = new AlsaSession(pcm, onEnded);

        if (session.Start(out error))
            return session;

        session.Dispose();
        return null;
    }
}

internal sealed class AlsaSession : IAudioSession
{
    private const int SndPcmStreamPlayback = 0;
    private const int SndPcmFormatS16Le = 2;
    private const int SndPcmAccessRwInterleaved = 3;
    private const int ChunkBytes = 8192;

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_open", CharSet = CharSet.Ansi)]
    private static extern int snd_pcm_open(out IntPtr pcm, string name, int stream, int mode);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_set_params")]
    private static extern int snd_pcm_set_params(IntPtr pcm, int format, int access,
        int channels, uint rate, int softResample, uint latency);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_writei")]
    private static extern int snd_pcm_writei(IntPtr pcm, byte[] buffer, UIntPtr size);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_drain")]
    private static extern int snd_pcm_drain(IntPtr pcm);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_drop")]
    private static extern int snd_pcm_drop(IntPtr pcm);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_close")]
    private static extern int snd_pcm_close(IntPtr pcm);

    [DllImport("libasound.so.2", EntryPoint = "snd_strerror", CharSet = CharSet.Ansi)]
    private static extern IntPtr snd_strerror(int errnum);

    private readonly PcmAudio _pcm;
    private readonly Action _onEnded;

    private IntPtr _device;
    private Thread? _thread;
    private bool _stopRequested;
    private int _closed;

    public AlsaSession(PcmAudio pcm, Action onEnded)
    {
        _pcm = pcm;
        _onEnded = onEnded;
    }

    public bool Start(out string? error)
    {
        error = null;

        var opened = snd_pcm_open(out _device, "default", SndPcmStreamPlayback, 0);
        if (opened < 0)
        {
            _device = IntPtr.Zero;
            error = $"Не удалось открыть устройство вывода ALSA: {Describe(opened)}";
            return false;
        }

        var configured = snd_pcm_set_params(_device, SndPcmFormatS16Le, SndPcmAccessRwInterleaved,
            _pcm.Channels, (uint)_pcm.SampleRate, 1, 100_000);

        if (configured < 0)
        {
            error = $"ALSA отклонила параметры потока: {Describe(configured)}";
            Close();
            return false;
        }

        var thread = new Thread(WriteLoop) { IsBackground = true, Name = "alsa-wav" };
        _thread = thread;
        thread.Start();
        return true;
    }

    private void WriteLoop()
    {
        var buffer = new byte[ChunkBytes];
        var total = _pcm.Samples.Length * 2;

        try
        {
            int offset = 0;
            while (offset < total && !Volatile.Read(ref _stopRequested))
            {
                var len = Math.Min(ChunkBytes, total - offset);
                Buffer.BlockCopy(_pcm.Samples, offset, buffer, 0, len);

                var written = snd_pcm_writei(_device, buffer, (UIntPtr)(len / 2));
                if (written < 0)
                    break;

                offset += len;
            }

            if (Volatile.Read(ref _stopRequested))
                snd_pcm_drop(_device);
            else
                snd_pcm_drain(_device);
        }
        catch
        {
            // Ошибка воспроизведения не должна ничего ломать.
        }
        finally
        {
            Close();
            try
            {
                _onEnded();
            }
            catch
            {
                // Уведомление UI не критично.
            }
        }
    }

    public void Stop() => Volatile.Write(ref _stopRequested, true);

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        if (_device != IntPtr.Zero)
        {
            snd_pcm_close(_device);
            _device = IntPtr.Zero;
        }
    }

    private static string Describe(int code)
    {
        var text = snd_strerror(code);
        return text == IntPtr.Zero ? code.ToString() : Marshal.PtrToStringAnsi(text) ?? code.ToString();
    }

    public void Dispose()
    {
        Stop();

        // Ждём поток, иначе он обратится к уже закрытому устройству.
        _thread?.Join(2000);
    }
}
