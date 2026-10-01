using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using readingFish.Models;

namespace readingFish.Services;

/// <summary>Ошибка обращения к fish-speech. Текст показывается пользователю с кнопкой копирования.</summary>
public sealed class FishApiException : Exception
{
    public FishApiException(string message, Exception? inner = null) : base(message, inner) { }

    public FishApiException(string message, HttpStatusCode status, string? body)
        : base(BuildMessage(message, status, body))
    {
        Status = status;
    }

    public HttpStatusCode? Status { get; }

    private static string BuildMessage(string message, HttpStatusCode status, string? body)
    {
        var sb = new StringBuilder(message);
        sb.Append("\n\nHTTP ").Append((int)status).Append(' ').Append(status);

        if (!string.IsNullOrWhiteSpace(body))
        {
            var trimmed = body.Trim();
            if (trimmed.Length > 2000)
                trimmed = trimmed[..2000] + "…";
            sb.Append("\n\nОтвет сервера:\n").Append(trimmed);
        }

        return sb.ToString();
    }
}

/// <summary>Клиент HTTP API fish-speech (порт 8080).</summary>
public sealed class FishSpeechClient : IDisposable
{
    private HttpClient _http;
    private Uri _base;
    private readonly TimeSpan _timeout;

    public FishSpeechClient(string serverUrl, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        _base = new Uri(Normalize(serverUrl));
        _http = CreateClient(_base);
    }

    /// <summary>
    /// BaseAddress у HttpClient менять нельзя после первого запроса, поэтому
    /// при смене адреса клиент создаётся заново.
    /// </summary>
    private HttpClient CreateClient(Uri baseAddress)
    {
        var client = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = _timeout
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "http://127.0.0.1:8080/";

        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }

        return url.EndsWith('/') ? url : url + "/";
    }

    public string ServerUrl => _base.ToString();

    public void UpdateServerUrl(string url)
    {
        var normalized = Normalize(url);
        var target = new Uri(normalized);
        if (target == _base)
            return;

        var previous = _http;
        _base = target;
        _http = CreateClient(target);
        previous.Dispose();
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("v1/health", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Собирает тело запроса /v1/tts. Набор полей и границы — из схемы
    /// fish-speech ServeTTSRequest. Вынесено отдельно, чтобы состав запроса
    /// можно было проверить без обращения к серверу.
    /// </summary>
    public static Dictionary<string, object?> BuildTtsPayload(string text, string voiceId, SynthesisOptions options)
    {
        var opts = options.Clamped();

        var payload = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["format"] = "wav",
            ["chunk_length"] = opts.ChunkLength,
            ["max_new_tokens"] = opts.MaxNewTokens,
            ["top_p"] = opts.TopP,
            ["repetition_penalty"] = opts.RepetitionPenalty,
            ["temperature"] = opts.Temperature,
            ["use_memory_cache"] = opts.MemoryCache == MemoryCacheMode.On ? "on" : "off",
            ["normalize"] = opts.Normalize,
            ["latency"] = opts.Latency == LatencyMode.Balanced ? "balanced" : "normal",

            // Клиент всегда ждёт весь файл целиком, потоковая выдача не нужна.
            ["streaming"] = false
        };

        // null означает «сервер выберет сам», поле в этом случае не отправляем.
        if (opts.Seed is { } seed)
            payload["seed"] = seed;

        if (!string.IsNullOrWhiteSpace(voiceId))
            payload["reference_id"] = voiceId;

        return payload;
    }

    /// <summary>
    /// Синтез одной части. Параметры приходят из проекта, поэтому у разных
    /// проектов они могут отличаться.
    /// </summary>
    public async Task<byte[]> SynthesizeAsync(
        string text,
        string voiceId,
        SynthesisOptions options,
        CancellationToken ct = default)
    {
        var payload = BuildTtsPayload(text, voiceId, options);

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/tts") { Content = content };

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new FishApiException(
                $"Не удалось соединиться с сервером fish-speech по адресу {_base}.\n" +
                "Проверьте, что сервер запущен, и адрес в настройках.", ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FishApiException("Сервер не ответил вовремя. Превышено время ожидания.");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
                throw new FishApiException("Сервер отклонил запрос синтеза.", resp.StatusCode, body);
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length < 44)
                throw new FishApiException("Сервер вернул пустой или повреждённый аудиофайл.");

            return bytes;
        }
    }

    public async Task<List<string>> ListVoicesAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/references/list");

        // Без этого заголовка сервер отдаёт MessagePack, и разбор падает.
        // Задаём ровно application/json: добавление второго такого же значения
        // или перечисление форматов приводит к msgpack обратно.
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
            throw new FishApiException("Не удалось получить список голосов.", resp.StatusCode, body);
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // Отдельно упоминаем msgpack: это самая частая причина, и по тексту
            // JsonException её не угадать.
            var type = resp.Content.Headers.ContentType?.MediaType ?? "неизвестный";
            throw new FishApiException(
                $"Сервер ответил не в JSON, а в формате «{type}». " +
                "Нужен JSON: клиент запрашивает его заголовком Accept.",
                ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new FishApiException(
                    "Сервер не смог вернуть список голосов." +
                    (string.IsNullOrWhiteSpace(message) ? "" : $"\n\n{message}"));
            }

            if (root.TryGetProperty("reference_ids", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                return arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList();
            }
        }

        // Поля нет — это не ошибка формата: у сервера просто нет референсов.
        return new List<string>();
    }

    public async Task AddVoiceAsync(string id, string audioPath, string text, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(audioPath);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(id), "id");
        form.Add(new StringContent(text), "text");

        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "audio", Path.GetFileName(audioPath));

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/references/add") { Content = form };
        using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
            throw new FishApiException($"Не удалось загрузить голос «{id}» на сервер.", resp.StatusCode, body);
        }
    }

    public async Task DeleteVoiceAsync(string id, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { reference_id = id });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Delete, "v1/references/delete") { Content = content };

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new FishApiException($"Не удалось удалить голос «{id}» на сервере.", ex);
        }

        using (resp)
        {
            // 404 — голоса на сервере уже нет, это не ошибка для пользователя.
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
            {
                var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
                throw new FishApiException($"Не удалось удалить голос «{id}» на сервере.", resp.StatusCode, body);
            }
        }
    }

    private static async Task<string?> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
