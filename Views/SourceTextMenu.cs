using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;

namespace readingFish.Views;

/// <summary>
/// Контекстное меню поля исходного текста: стандартные правки плюс вставка
/// тегов паузы.
///
/// Меню приходится собирать целиком, а не дополнять: у TextBox штатное меню
/// лежит в ContextFlyout и создаётся темой как общий для всех полей ввода.
/// Добавленные туда пункты появились бы ещё и в полях имени голоса, где
/// пауза бессмысленна, поэтому каждому полю мы задаём своё меню.
///
/// Стандартный набор (вырезать/копировать/вставить) повторяем сами: ссылки
/// на команды темы при копировании теряются, это проверено.
/// </summary>
public static class SourceTextMenu
{
    /// <summary>
    /// Тег паузы. Скобки круглые: сервер понимает и круглые, и квадратные
    /// (проверено на живом сервере), а круглые записаны в ТЕГИ-ПОДАЧИ.md,
    /// так что пишем в том виде, в каком описан проект.
    /// </summary>
    public const string PauseTag = "(pause)";

    /// <summary>Вешает меню на поле. Вызывать после создания поля, до пока.</summary>
    public static void Attach(TextBox box)
    {
        var cutItem = new MenuItem
        {
            Header = "Вырезать",
            InputGesture = new KeyGesture(Key.X, KeyModifiers.Control),
            Command = new RelayCommand(() => box.Cut())
        };
        var copyItem = new MenuItem
        {
            Header = "Копировать",
            InputGesture = new KeyGesture(Key.C, KeyModifiers.Control),
            Command = new RelayCommand(() => box.Copy())
        };
        var pasteItem = new MenuItem
        {
            Header = "Вставить",
            InputGesture = new KeyGesture(Key.V, KeyModifiers.Control),
            Command = new RelayCommand(() => box.Paste())
        };

        var pauseItem = new MenuItem
        {
            Header = $"Вставить паузу   {PauseTag}",
            Command = new RelayCommand(() => InsertTag(box, PauseTag))
        };

        var menu = new MenuFlyout();
        menu.Items.Add(cutItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(pauseItem);

        // Доступность пунктов считаем при открытии. У MenuItem она не следует
        // за Command.CanExecute, а события смены выделения у TextBox нет —
        // так что момент открытия меню единственное верное время.
        menu.Opening += (_, _) =>
        {
            var hasSelection = box.SelectedText.Length > 0;
            var editable = !box.IsReadOnly;

            cutItem.IsEnabled = editable && hasSelection;
            copyItem.IsEnabled = hasSelection;
            pasteItem.IsEnabled = editable;
            pauseItem.IsEnabled = editable;
        };

        box.ContextFlyout = menu;
    }

    /// <summary>
    /// Вставляет тег в позицию каретки, отделяя его пробелами: прилипший к
    /// знаку препинания, он читается хуже. Если что-то выделено, тег заменяет
    /// выделение — как обычный набор текста. Идём через SelectedText, а не
    /// через присваивание Text: так сохраняется история правок, и Ctrl+Z
    /// отменяет вставку.
    /// </summary>
    public static void InsertTag(TextBox box, string tag)
    {
        var text = box.Text ?? string.Empty;
        var caret = Math.Clamp(box.CaretIndex, 0, text.Length);

        // При выделенном фрагменте вставляем на его место, а не в каретку:
        // пока текст выделен, CaretIndex остаётся прежним (часто — в конце
        // строки), и опора на него съела бы всё до него.
        var selStart = Math.Clamp(box.SelectionStart, 0, text.Length);
        var selEnd = Math.Clamp(box.SelectionEnd, 0, text.Length);

        int start, end;
        if (selStart != selEnd)
        {
            start = Math.Min(selStart, selEnd);
            end = Math.Max(selStart, selEnd);
        }
        else
        {
            start = end = caret;
        }

        var before = text[..start];
        var after = text[end..];

        var head = before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? " " : string.Empty;
        var tail = after.Length > 0 && !char.IsWhiteSpace(after[0]) ? " " : string.Empty;

        box.SelectionStart = start;
        box.SelectionEnd = end;
        box.SelectedText = head + tag + tail;

        box.CaretIndex = start + head.Length + tag.Length;
        box.Focus();
    }
}
