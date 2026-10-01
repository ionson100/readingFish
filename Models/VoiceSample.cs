namespace readingFish.Models;

/// <summary>
/// Пример голоса. Физически хранится в ~/fishApp/voices/&lt;id&gt;/
/// и продублирован на сервере fish-speech (references/&lt;id&gt;).
/// </summary>
public sealed class VoiceSample
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    public const string AudioFileName = "sample.wav";
    public const string LabFileName = "sample.lab";

    public string AudioPath => Path.Combine(AppPaths.VoiceDir(Id), AudioFileName);
    public string LabPath => Path.Combine(AppPaths.VoiceDir(Id), LabFileName);
    public bool AudioExists => File.Exists(AudioPath);
    public bool LabExists => File.Exists(LabPath);

    /// <summary>Есть ли папка голоса локально. У серверного голоса её может не быть.</summary>
    public bool LocalExists => Directory.Exists(AppPaths.VoiceDir(Id));

    /// <summary>Сервер fish-speech знает этот голос. Заполняется при сверке.</summary>
    public bool IsOnServer { get; set; }

    /// <summary>Голос есть на сервере, но нет локальной записи: синтез сработает, а послушать нельзя.</summary>
    public bool IsServerOnly => IsOnServer && !LocalExists;

    /// <summary>Голос лежит локально, но сервер о нём не знает.</summary>
    public bool IsLocalOnly => !IsOnServer;

    /// <summary>Название для интерфейса.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Id) ? "(без имени)" : Id;

    /// <summary>Подпись под именем: где лежит голос и есть ли запись.</summary>
    public string StatusText
    {
        get
        {
            if (IsServerOnly)
                return "только на сервере — записи на этом компьютере нет";

            if (IsLocalOnly)
                return "локально, сервер о нём не знает";

            if (!AudioExists)
                return "на сервере и локально, но файла записи нет";

            return "на сервере и локально";
        }
    }

    /// <summary>Текст записи для показа; у серверного голоса его взять неоткуда.</summary>
    public string TextOrPlaceholder =>
        string.IsNullOrWhiteSpace(Text) ? "текст записи неизвестен" : Text;
}
