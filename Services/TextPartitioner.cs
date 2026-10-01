namespace readingFish.Services;

/// <summary>
/// Разбиение текста на части строго по границам предложений.
///
/// Правило (размер цели — например 200 символов):
///   1. Смотрим окно от текущей позиции длиной targetChars.
///   2. Если внутри окна есть граница предложения — режем по ПОСЛЕДНЕЙ из них.
///      Например, последняя точка попала на 167-й символ — режем там.
///   3. Если внутри окна границы нет (предложение длиннее цели) — идём за пределы
///      окна до ближайшей границы и режем по ней. Часть получится длиннее цели,
///      зато предложение не разорвётся пополам.
///   4. Если границы нет вообще — берём остаток целиком.
///
/// Многоточие границей не считается: «Он ушёл... Я ждал» — одна фраза, и резать
/// её после многоточия нельзя. Точка концом предложения является, только если
/// рядом с ней нет других точек.
///
/// Предложение никогда не режется посреди фразы: последний аварийный случай —
/// очень длинная «фраза» без знаков конца, тогда режем по словам.
/// </summary>
public static class TextPartitioner
{
    private static readonly char[] Terminators = { '.', '!', '?', '…', '。', '！', '？', '؟', '۔' };

    /// <summary>Многоточие одним знаком. Границей предложения не считается.</summary>
    private const char Ellipsis = '…';

    /// <summary>Кавычки и скобки, которые «прилипают» к концу предложения.</summary>
    private static readonly char[] Trailing = { '"', '\'', '»', '”', '’', ')', ']', '}', '」', '』', '）' };

    public static List<string> Split(string text, int targetChars)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        // Защита от вырожденных значений. Рабочий диапазон (50..5000)
        // задаётся в настройках — здесь не подменяем намерение вызывающего.
        targetChars = Math.Clamp(targetChars, 10, 100_000);

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        int pos = 0;
        while (pos < normalized.Length)
        {
            // Пропускаем пробелы и переносы строк между частями.
            while (pos < normalized.Length && char.IsWhiteSpace(normalized[pos]))
                pos++;

            if (pos >= normalized.Length)
                break;

            int cut = AvoidEllipsisTail(normalized, pos, FindCut(normalized, pos, targetChars));
            var piece = normalized[pos..cut].Trim();

            if (piece.Length > 0)
                result.Add(piece);

            pos = cut;
        }

        return result;
    }

    /// <summary>Возвращает позицию конца следующей части (не включая её).</summary>
    private static int FindCut(string text, int start, int targetChars)
    {
        int windowEnd = Math.Min(start + targetChars, text.Length);

        // 1) Последняя граница предложения внутри окна.
        int inside = LastBoundary(text, start, windowEnd);
        if (inside >= 0)
            return inside;

        // 2) Границы в окне нет — берём ближайшую за пределами окна.
        int beyond = NextBoundary(text, windowEnd);
        if (beyond >= 0)
            return beyond;

        // 3) Границ нет вовсе. Так бывает на тексте без знаков конца.
        //    Резать посреди фразы нельзя, но и одна часть на 5000 символов
        //    бесполезна — сервер не осилит. Поэтому режем по словам,
        //    как только остаток заметно больше цели.
        int remaining = text.Length - start;
        if (remaining > targetChars * 2)
            return WordBoundary(text, start, start + targetChars);

        return text.Length;
    }

    /// <summary>Последняя граница предложения в диапазоне [from, toExclusive).</summary>
    private static int LastBoundary(string text, int from, int toExclusive)
    {
        for (int i = toExclusive - 1; i >= from; i--)
        {
            if (IsBoundary(text, i))
                return EndOfSentence(text, i);
        }

        return -1;
    }

    /// <summary>Первая граница предложения начиная с from.</summary>
    private static int NextBoundary(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (IsBoundary(text, i))
                return EndOfSentence(text, i);
        }

        return -1;
    }

    /// <summary>
    /// Граница ли это предложения? Знак должен быть настоящим концом фразы:
    /// дальше пробел, конец текста либо это CJK-знак (там пробелов не бывает).
    /// Иначе это точка в числе «3.14» или в сокращении.
    ///
    /// Многоточие отсекается отдельно: точка из «...» концом предложения не
    /// считается, даже если дальше пробел. Иначе фраза «Он ушёл... Я ждал»
    /// распалась бы на две части прямо посреди мысли.
    /// </summary>
    private static bool IsBoundary(string text, int i)
    {
        char c = text[i];
        if (!IsTerminator(c))
            return false;

        if (IsCjkTerminator(c))
            return true;

        // Знак многоточия сам по себе многоточие: резать по нему нельзя.
        if (c == Ellipsis)
            return false;

        // Точка из серии: если рядом есть ещё точка, это многоточие.
        if (c == '.')
        {
            if (i > 0 && text[i - 1] == '.')
                return false;

            if (i + 1 < text.Length && text[i + 1] == '.')
                return false;
        }

        return i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]);
    }

    /// <summary>
    /// Сдвигает разрез назад, если часть закончилась бы многоточием.
    ///
    /// Многоточие — это пауза внутри фразы, и резать по нему нельзя: пауза
    /// встала бы в самый конец части, и следующая начиналась бы с обрыва.
    /// Раньше это зависело от везения: аварийный разрез по словам берёт
    /// последний пробел до конца окна, и стоило пробелу после многоточия
    /// случайно оказаться последним — разрез падал ровно на многоточие.
    /// Теперь правило выполняется всегда.
    /// </summary>
    private static int AvoidEllipsisTail(string text, int start, int cut)
    {
        // Конец текста трогать незачем: следующей части не будет.
        if (cut >= text.Length || cut <= start)
            return cut;

        var p = cut - 1;
        while (p > start && char.IsWhiteSpace(text[p]))
            p--;

        // Отступать некуда: начало части. Лучше лишняя точка, чем пустая часть.
        if (p <= start || !IsEllipsisRunEnd(text, p))
            return cut;

        while (p > start && !char.IsWhiteSpace(text[p]))
            p--;

        return p > start ? p + 1 : cut;
    }

    /// <summary>Заканчивается ли тут серия точек, то есть многоточие.</summary>
    private static bool IsEllipsisRunEnd(string text, int p)
    {
        if (text[p] == Ellipsis)
            return true;

        if (text[p] != '.')
            return false;

        return (p > 0 && text[p - 1] == '.') || (p + 1 < text.Length && text[p + 1] == '.');
    }

    /// <summary>Позиция сразу после знака и прилипших кавычек/скобок.</summary>
    private static int EndOfSentence(string text, int terminatorIndex)
    {
        int j = terminatorIndex + 1;
        while (j < text.Length && IsTrailing(text[j]))
            j++;

        return j;
    }

    /// <summary>Аварийный разрез по словам, когда предложения конца нет.</summary>
    private static int WordBoundary(string text, int start, int limit)
    {
        limit = Math.Min(limit, text.Length);

        for (int i = limit - 1; i > start; i--)
        {
            if (char.IsWhiteSpace(text[i]))
                return i + 1;
        }

        return limit;
    }

    private static bool IsTerminator(char c) => Array.IndexOf(Terminators, c) >= 0;

    private static bool IsCjkTerminator(char c) => c is '。' or '！' or '？' or '؟';

    private static bool IsTrailing(char c) => Array.IndexOf(Trailing, c) >= 0;
}
