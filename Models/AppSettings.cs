using System.Text.Json.Serialization;

namespace readingFish.Models;

/// <summary>Настройки приложения, хранятся в ~/fishApp/settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Адрес fish-speech сервера. По умолчанию — локальный.</summary>
    public string ServerUrl { get; set; } = "http://127.0.0.1:8080";

    /// <summary>Голос по умолчанию для новых проектов.</summary>
    public string? DefaultVoiceId { get; set; }

    /// <summary>Целевой размер части текста в символах.</summary>
    public int TargetPartChars { get; set; } = 200;

    /// <summary>Лимит токенов генерации на одну часть.</summary>
    public int MaxNewTokens { get; set; } = 1024;

    /// <summary>Temperature для генерации.</summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>Считать результат устаревшим при смене голоса проекта.</summary>
    public bool InvalidateOnVoiceChange { get; set; } = true;

    /// <summary>Кегль шрифта в поле исходного текста на странице проекта.</summary>
    public double EditorFontSize { get; set; } = 13;

    /// <summary>Жирный шрифт в поле исходного текста.</summary>
    public bool EditorFontBold { get; set; }

    /// <summary>
    /// Доля высоты, которую занимает поле исходного текста на странице проекта
    /// (0.05..0.95 от свободного места между шапкой и результатом).
    /// </summary>
    public double EditorRatio { get; set; } = 0.5;

    [JsonIgnore]
    public Uri ServerUri
    {
        get
        {
            var url = string.IsNullOrWhiteSpace(ServerUrl)
                ? "http://127.0.0.1:8080"
                : ServerUrl.Trim();

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "http://" + url;
            }

            if (!url.EndsWith('/'))
                url += "/";

            return new Uri(url);
        }
    }
}
