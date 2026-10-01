using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using readingFish.ViewModels;

namespace readingFish.Views;

public partial class EditPartDialog : Window
{
    public EditPartDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not EditPartViewModel vm)
            return;

        vm.Closed -= OnClosed;
        vm.Closed += OnClosed;
    }

    private void OnClosed(object? sender, bool result) => Close(result);
}
