using ExpenseSync.App.Data;
using ExpenseSync.App.ViewModels;
using ExpenseSync.App.Views;
using Microsoft.Extensions.Logging;
#if MAUI_DEVFLOW
using Microsoft.Maui.DevFlow.Agent;
#endif

namespace ExpenseSync.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// Local data (one shared SQLite connection for the whole app)
		builder.Services.AddSingleton<ExpenseDatabase>();

		// Pages and view models
		builder.Services.AddSingleton<MainViewModel>();
		builder.Services.AddSingleton<MainPage>();
		// A fresh edit page/view model for every navigation
		builder.Services.AddTransient<ExpenseEditViewModel>();
		builder.Services.AddTransient<ExpenseEditPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

#if MAUI_DEVFLOW
		builder.AddMauiDevFlowAgent();
#endif

		return builder.Build();
	}
}
