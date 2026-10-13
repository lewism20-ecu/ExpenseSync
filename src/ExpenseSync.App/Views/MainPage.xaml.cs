using ExpenseSync.App.ViewModels;

namespace ExpenseSync.App.Views;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel viewModel;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = this.viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await viewModel.InitializeCommand.ExecuteAsync(null);
    }
}
