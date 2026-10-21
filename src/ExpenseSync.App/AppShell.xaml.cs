using ExpenseSync.App.Views;

namespace ExpenseSync.App;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute(nameof(ExpenseEditPage), typeof(ExpenseEditPage));
	}
}
