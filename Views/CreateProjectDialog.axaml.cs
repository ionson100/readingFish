using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using readingFish.ViewModels;

namespace readingFish.Views;

public partial class CreateProjectDialog : Window
{
    public CreateProjectDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not CreateProjectViewModel vm)
            return;

        vm.Closed -= OnClosed;
        vm.Closed += OnClosed;
    }

    private void OnClosed(object? sender, bool result) => Close(result);
}
