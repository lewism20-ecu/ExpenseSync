using ExpenseSync.App.Services;

namespace ExpenseSync.App;

public partial class App : Application
{
	public App(SyncCoordinator syncCoordinator)
	{
		InitializeComponent();
		syncCoordinator.Start();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}