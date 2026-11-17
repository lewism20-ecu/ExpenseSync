using ExpenseSync.App.ViewModels;

namespace ExpenseSync.App.Views;

public partial class ConflictHistoryPage : ContentPage
{
    private readonly ConflictHistoryViewModel viewModel;

    public ConflictHistoryPage(ConflictHistoryViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = this.viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await viewModel.LoadCommand.ExecuteAsync(null);
    }
}
