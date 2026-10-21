using ExpenseSync.App.ViewModels;

namespace ExpenseSync.App.Views;

public partial class ExpenseEditPage : ContentPage
{
    private readonly ExpenseEditViewModel viewModel;

    public ExpenseEditPage(ExpenseEditViewModel viewModel)
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
