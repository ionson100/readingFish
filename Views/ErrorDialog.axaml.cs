using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using readingFish.ViewModels;

namespace readingFish.Views;

public partial class ErrorDialog : Window
{
    public ErrorDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not MessageViewModel vm)
            return;

        vm.CloseRequested -= OnCloseRequested;
        vm.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
