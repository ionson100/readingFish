namespace readingFish.Services;

public sealed record WavFormat(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    ushort AudioFormat,
    /// <summary>Настоящий формат из GUID для WAVE_FORMAT_EXTENSIBLE, иначе 0.</summary>
    ushort SubFormatTag = 0);

public sealed class WavException : Exception
{
    public WavException(string message) : base(message) { }
}

/// <summary>Аудио, готовое к воспроизведению: PCM 16-bit с любым числом каналов.</summary>
public sealed record PcmAudio(short[] Samples, int SampleRate, int Channels)
{
    public int FrameCount => Samples.Length / Math.Max(1, Channels);

    public double DurationSeconds => SampleRate <= 0 ? 0 : (double)FrameCount / SampleRate;
}

/// <summary>
/// Минимальная работа с WAV: чтение заголовка и склейка нескольких файлов в один.
/// Поддерживается PCM 16-bit — этого достаточно для fish-speech.
/// </summary>
public static class WavTools
{
    private const int HeaderSize = 44;

    public const ushort WaveFormatPcm = 1;
    public const ushort WaveFormatIeeeFloat = 3;
    public const ushort WaveFormatExtensible = 0xFFFE;

    public static WavFormat ReadFormat(byte[] wav)
    {
        var fmt = Parse(wav).Format;
        return fmt;
    }

    /// <summary>Склеивает несколько WAV в один. Форматы должны совпадать.</summary>
    public static byte[] Combine(IEnumerable<byte[]> files)
    {
        var list = files.Where(f => f is { Length: > HeaderSize }).ToList();
        if (list.Count == 0)
            throw new WavException("Нет данных для склейки: все файлы пустые.");

        var first = Parse(list[0]);
        var sampleRate = first.Format.SampleRate;
        var channels = first.Format.Channels;
        var bits = first.Format.BitsPerSample;

        // Результат всегда пишем обычным PCM: WAVE_FORMAT_EXTENSIBLE требует
        // блок fmt в 40 байт, а BuildWav собирает классические 16.
        var format = ResolveFormat(first.Format);

        long totalData = 0;
        foreach (var f in list)
        {
            var p = Parse(f);
            if (p.Format.SampleRate != sampleRate || p.Format.Channels != channels || p.Format.BitsPerSample != bits)
            {
                throw new WavException(
                    "Части аудио имеют разный формат " +
                    $"({sampleRate} Гц/{channels} ch/{bits} бит против {p.Format.SampleRate} Гц/" +
                    $"{p.Format.Channels} ch/{p.Format.BitsPerSample} бит). Перегенерируйте проект.");
            }

            totalData += p.DataLength;
        }

        var outData = new byte[totalData];
        long offset = 0;
        foreach (var f in list)
        {
            var p = Parse(f);
            Array.Copy(f, p.DataOffset, outData, offset, p.DataLength);
            offset += p.DataLength;
        }

        return BuildWav(outData, sampleRate, channels, bits, format);
    }

    private static byte[] BuildWav(byte[] data, int sampleRate, int channels, int bits, ushort format)
    {
        int byteRate = sampleRate * channels * bits / 8;
        int blockAlign = channels * bits / 8;
        var buffer = new byte[HeaderSize + data.Length];

        using var ms = new MemoryStream(buffer);
        using var w = new BinaryWriter(ms);

        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVE"u8);

        w.Write("fmt "u8);
        w.Write(16);
        w.Write(format);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bits);

        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);

        w.Flush();
        return buffer;
    }

    private readonly record struct Parsed(WavFormat Format, int DataOffset, int DataLength);

    private static Parsed Parse(byte[] wav)
    {
        if (wav.Length < 12)
            throw new WavException("Файл слишком короткий, это не WAV.");

        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
            throw new WavException("Нет сигнатуры RIFF — это не WAV-файл.");

        using var ms = new MemoryStream(wav);
        using var r = new BinaryReader(ms);

        r.ReadBytes(4);          // RIFF
        r.ReadUInt32();          // размер файла
        r.ReadBytes(4);          // WAVE

        int channels = 0, sampleRate = 0, bits = 0;
        ushort format = 0;
        ushort subFormat = 0;
        int dataOffset = -1, dataLength = 0;

        while (ms.Position + 8 <= wav.Length)
        {
            var chunkId = r.ReadBytes(4);
            var chunkSize = r.ReadInt32();

            if (chunkId[0] == 'f' && chunkId[1] == 'm' && chunkId[2] == 't')
            {
                if (chunkSize < 16)
                    throw new WavException("Повреждён WAV: блок fmt слишком мал.");

                format = r.ReadUInt16();
                channels = r.ReadInt16();
                sampleRate = r.ReadInt32();
                r.ReadInt32();              // byte rate
                r.ReadInt16();              // block align
                bits = r.ReadInt16();

                if (chunkSize > 16)
                {
                    var extra = chunkSize - 16;

                    // WAVE_FORMAT_EXTENSIBLE прячет настоящий формат в GUID:
                    // cbSize(2) + wValidBitsPerSample(2) + dwChannelMask(4) + SubFormat(16).
                    if (format == WaveFormatExtensible && chunkSize >= 40)
                    {
                        r.ReadUInt16();      // cbSize
                        r.ReadUInt16();      // wValidBitsPerSample
                        r.ReadUInt32();      // dwChannelMask
                        subFormat = r.ReadUInt16();
                        extra -= 10;         // эти десять байт уже прочитаны
                    }

                    ms.Seek(extra, SeekOrigin.Current);
                }
            }
            else if (chunkId[0] == 'd' && chunkId[1] == 'a' && chunkId[2] == 't' && chunkId[3] == 'a')
            {
                dataOffset = (int)ms.Position;
                dataLength = Math.Min(chunkSize, wav.Length - dataOffset);
                ms.Seek(dataLength, SeekOrigin.Current);
            }
            else
            {
                ms.Seek(chunkSize, SeekOrigin.Current);
            }

            if (chunkSize % 2 == 1)
                ms.Seek(1, SeekOrigin.Current);   // padding до чётной границы
        }

        if (dataOffset < 0)
            throw new WavException("Повреждён WAV: не найден блок данных.");

        return new Parsed(new WavFormat(sampleRate, channels, bits, format, subFormat), dataOffset, dataLength);
    }

    /// <summary>
    /// Приводит WAV к PCM 16-bit для воспроизведения. Понимает PCM 8/16/24/32 бита,
    /// IEEE float 32/64 бита и WAVE_FORMAT_EXTENSIBLE поверх них.
    /// null — файл не WAV или формат нечем играть без конвертации.
    /// </summary>
    public static PcmAudio? DecodeToPcm16(byte[] wav)
    {
        Parsed parsed;
        try
        {
            parsed = Parse(wav);
        }
        catch (WavException)
        {
            return null;
        }

        var fmt = parsed.Format;
        int channels = fmt.Channels;
        int bits = fmt.BitsPerSample;

        if (channels <= 0 || channels > 8 || fmt.SampleRate <= 0 || bits % 8 != 0 || bits < 8)
            return null;

        int frameBytes = channels * (bits / 8);
        int frames = parsed.DataLength / frameBytes;
        if (frames <= 0)
            return null;

        int offset = parsed.DataOffset;
        var samples = new short[frames * channels];

        var tag = ResolveFormat(fmt);

        if (tag == WaveFormatPcm)
        {
            switch (bits)
            {
                case 8:
                    // 8-битный PCM беззнаковый, ноль — это 128.
                    for (int i = 0; i < samples.Length; i++)
                        samples[i] = (short)((wav[offset + i] - 128) << 8);
                    break;

                case 16:
                    // WAV всегда little-endian, как и все платформы, где работает .NET.
                    Buffer.BlockCopy(wav, offset, samples, 0, samples.Length * 2);
                    break;

                case 24:
                    for (int i = 0; i < samples.Length; i++)
                    {
                        int p = offset + i * 3;
                        int v = wav[p] | (wav[p + 1] << 8) | (wav[p + 2] << 16);
                        samples[i] = (short)(v >> 8);
                    }
                    break;

                case 32:
                    for (int i = 0; i < samples.Length; i++)
                    {
                        int p = offset + i * 4;
                        int v = wav[p] | (wav[p + 1] << 8) | (wav[p + 2] << 16) | (wav[p + 3] << 24);
                        samples[i] = (short)(v >> 16);
                    }
                    break;

                default:
                    return null;
            }
        }
        else if (tag == WaveFormatIeeeFloat)
        {
            // Значения за пределами ±1.0 — перегруз; без ограничения будет щелчок.
            if (bits == 32)
            {
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = FloatToPcm16(BitConverter.ToSingle(wav, offset + i * 4));
            }
            else if (bits == 64)
            {
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = FloatToPcm16((float)BitConverter.ToDouble(wav, offset + i * 8));
            }
            else
            {
                return null;
            }
        }
        else
        {
            // ADPCM, mu-law, a-law и прочее — без кодеков не играем.
            return null;
        }

        return new PcmAudio(samples, fmt.SampleRate, channels);
    }

    /// <summary>Настоящий формат: у EXTENSIBLE он спрятан в GUID, а не в самом теге.</summary>
    public static ushort ResolveFormat(WavFormat format) =>
        format.AudioFormat == WaveFormatExtensible && format.SubFormatTag != 0
            ? format.SubFormatTag
            : format.AudioFormat;

    private static short FloatToPcm16(float value)    {
        if (float.IsNaN(value))
            return 0;

        float scaled = value * 32767f;
        if (scaled >= 32767f)
            return short.MaxValue;
        if (scaled <= -32768f)
            return short.MinValue;
        return (short)scaled;
    }

    /// <summary>Приблизительная длительность в секундах — для показа в интерфейсе.</summary>
    public static double DurationSeconds(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            var fmt = ReadFormat(data);
            var parsed = Parse(data);
            if (fmt.SampleRate <= 0)
                return 0;

            var bytesPerSecond = fmt.SampleRate * fmt.Channels * fmt.BitsPerSample / 8.0;
            return bytesPerSecond <= 0 ? 0 : parsed.DataLength / bytesPerSecond;
        }
        catch
        {
            return 0;
        }
    }

    public static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
