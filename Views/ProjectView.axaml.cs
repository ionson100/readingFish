using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using readingFish.ViewModels;

namespace readingFish.Views;

/// <summary>
/// Разделитель между списком частей и редактором исходного текста.
///
/// Высоты строк задаются отсюда, а не через {Binding} в RowDefinition.Height:
/// RowDefinition — это StyledElement вне визуального дерева, у него нет
/// DataContext, и привязка молча не срабатывает. Доля хранится во
/// ViewModel, поэтому переживает и смену размера окна, и перезапуск.
/// </summary>
public partial class ProjectView : UserControl
{
    private const int PartsRowIndex = 2;
    private const int EditorRowIndex = 4;

    private bool _dragging;
    private double _dragStartRatio;
    private double _dragStartY;

    public ProjectView()
    {
        InitializeComponent();

        // Меню с тегами паузы задаём сразу: пока поле не показано, шаблон
        // темы не применён, и штатного меню у него ещё нет.
        SourceTextMenu.Attach(SourceTextBox);

        DataContextChanged += (_, _) => ApplyEditorRatio();
    }

    private ProjectViewModel? Vm => DataContext as ProjectViewModel;

    /// <summary>Применяет долю из модели к высотам строк.</summary>
    private void ApplyEditorRatio()
    {
        var ratio = Vm?.EditorRatio ?? 0.5;
        ratio = Math.Clamp(ratio, 0.1, 0.9);

        var rows = RootGrid.RowDefinitions;
        if (rows.Count <= EditorRowIndex)
            return;

        rows[PartsRowIndex].Height = new GridLength(1 - ratio, GridUnitType.Star);
        rows[EditorRowIndex].Height = new GridLength(ratio, GridUnitType.Star);
    }

    private void OnEditorSplitterPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(EditorSplitter).Properties.IsLeftButtonPressed != true)
            return;

        if (Vm is not { } vm)
            return;

        _dragging = true;
        _dragStartRatio = vm.EditorRatio;
        _dragStartY = e.GetPosition(EditorSplitter).Y;

        e.Pointer.Capture(EditorSplitter);
        e.Handled = true;
    }

    private void OnEditorSplitterMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || Vm is not { } vm)
            return;

        var total = FlexibleHeight();
        if (total <= 0)
            return;

        // Тянем вниз — поле исходного текста становится меньше.
        var delta = e.GetPosition(EditorSplitter).Y - _dragStartY;
        vm.SetEditorRatio(_dragStartRatio - delta / total);
        ApplyEditorRatio();
    }

    private void OnEditorSplitterReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        e.Pointer.Capture(null);

        // Положение запоминаем по окончании перетаскивания, а не на каждом кадре.
        Vm?.SaveEditorLayout();
    }

    /// <summary>
    /// Указатель ушёл мимо кнопки (смена окна, потеря фокуса) — считаем,
    /// что перетаскивание закончено, иначе разделитель «залипнет».
    /// </summary>
    private void OnEditorSplitterCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        Vm?.SaveEditorLayout();
    }

    /// <summary>
    /// Высота, на которую делятся список частей и редактор. Берём по факту
    /// отрисованных строк, а не по заявленным звёздам: с учётом MinHeight
    /// звезда одна может не совпадать с реальной высотой.
    /// </summary>
    private double FlexibleHeight()
    {
        var rows = RootGrid.RowDefinitions;
        if (rows.Count <= EditorRowIndex)
            return 0;

        return rows[PartsRowIndex].ActualHeight + rows[EditorRowIndex].ActualHeight;
    }

}
