using System.Text.Json.Serialization;

namespace readingFish.Models;

/// <summary>Режим кэширования референсов на сервере.</summary>
public enum MemoryCacheMode
{
    Off,
    On
}

/// <summary>Режим задержки. Применяется сервером api.fish.audio.</summary>
public enum LatencyMode
{
    Normal,
    Balanced
}

/// <summary>
/// Параметры синтеза для одного проекта. Хранятся в project.json,
/// поэтому у каждого проекта свои значения.
///
/// Границы взяты из схемы сервера fish-speech (ServeTTSRequest):
/// chunk_length 100..1000, top_p 0.1..1.0, repetition_penalty 0.9..1.9,
/// temperature 0.1..1.0. Выход за них сервер отклоняет.
/// </summary>
public sealed class SynthesisOptions
{
    /// <summary>
    /// Длина текста, обрабатываемого за один проход модели, в символах.
    /// Больше — меньше пауз и ровнее интонация, но выше требования к памяти.
    /// </summary>
    public int ChunkLength { get; set; } = 200;

    /// <summary>Потолок токенов на одну часть. Обрезает слишком длинные части.</summary>
    public int MaxNewTokens { get; set; } = 1024;

    /// <summary>
    /// Nucleus sampling: доля вероятностей, из которой берётся следующий токен.
    /// Ниже — строже и повторяемее, выше — свободнее и разнообразнее.
    /// </summary>
    public double TopP { get; set; } = 0.8;

    /// <summary>
    /// Штраф за повторяющиеся слова. Выше 1.1 убирает заикание, но может
    /// «сгладить» речь. Верхняя граница 1.9, а не 2.0: сервер проверяет
    /// значение строго и отклоняет ровно 2.0.
    /// </summary>
    public double RepetitionPenalty { get; set; } = 1.1;

    /// <summary>Случайность выбора. Низкая — ровно и монотонно, высокая — живее, но может уйти в шум.</summary>
    public double Temperature { get; set; } = 0.8;

    /// <summary>Фиксированное зерно генератора. null — сервер выбирает случайное.</summary>
    public int? Seed { get; set; }

    /// <summary>Кэшировать референс в памяти сервера. Ускоряет повторы, тратит видеопамять.</summary>
    public MemoryCacheMode MemoryCache { get; set; } = MemoryCacheMode.Off;

    /// <summary>Нормализация чисел и сокращений. Помогает для en и zh, для ru безвредна.</summary>
    public bool Normalize { get; set; } = true;

    /// <summary>Режим задержки сервера. Для локального сервера значения неважны.</summary>
    public LatencyMode Latency { get; set; } = LatencyMode.Normal;

    /// <summary>Копия настроек: их правят в диалоге, потом записывают в проект.</summary>
    public SynthesisOptions Clone() => new()
    {
        ChunkLength = ChunkLength,
        MaxNewTokens = MaxNewTokens,
        TopP = TopP,
        RepetitionPenalty = RepetitionPenalty,
        Temperature = Temperature,
        Seed = Seed,
        MemoryCache = MemoryCache,
        Normalize = Normalize,
        Latency = Latency
    };

    /// <summary>
    /// Приводит значения к допустимым. Нужна при чтении project.json:
    /// файл мог отредактировать человек или значения сменились после обновления.
    /// </summary>
    public SynthesisOptions Clamped()
    {
        var seed = Seed;
        if (seed is < 0 or > int.MaxValue - 1)
            seed = null;

        return new SynthesisOptions
        {
            ChunkLength = Math.Clamp(ChunkLength, MinChunkLength, MaxChunkLength),
            MaxNewTokens = Math.Clamp(MaxNewTokens, MinMaxNewTokens, MaxMaxNewTokens),
            TopP = Math.Clamp(TopP, MinTopP, MaxTopP),
            RepetitionPenalty = Math.Clamp(RepetitionPenalty, MinRepetitionPenalty, MaxRepetitionPenalty),
            Temperature = Math.Clamp(Temperature, MinTemperature, MaxTemperature),
            Seed = seed,
            MemoryCache = MemoryCache,
            Normalize = Normalize,
            Latency = Latency
        };
    }

    /// <summary>Есть ли значения, отличающиеся от серверных умолчаний.</summary>
    [JsonIgnore]
    public bool IsCustom =>
        ChunkLength != 200 || MaxNewTokens != 1024 ||
        Math.Abs(TopP - 0.8) > 0.0001 || Math.Abs(RepetitionPenalty - 1.1) > 0.0001 ||
        Math.Abs(Temperature - 0.8) > 0.0001 || Seed is not null ||
        MemoryCache != MemoryCacheMode.Off || !Normalize || Latency != LatencyMode.Normal;

    // Границы серверной схемы. Дублируются здесь, чтобы клиент отсекал
    // недопустимое до отправки и не получал отказ от сервера.
    //
    // Про repetition_penalty: в схеме указано le=2.0, но рантайм проверяет
    // строгое неравенство «(0, 2)» — значение ровно 2.0 сервер отклоняет с
    // 500. Проверено на живом сервере, поэтому верхняя граница здесь 1.9.
    public const int MinChunkLength = 100;
    public const int MaxChunkLength = 1000;
    public const int MinMaxNewTokens = 64;
    public const int MaxMaxNewTokens = 8192;
    public const double MinTopP = 0.1;
    public const double MaxTopP = 1.0;
    public const double MinRepetitionPenalty = 0.9;
    public const double MaxRepetitionPenalty = 1.9;
    public const double MinTemperature = 0.1;
    public const double MaxTemperature = 1.0;
}
