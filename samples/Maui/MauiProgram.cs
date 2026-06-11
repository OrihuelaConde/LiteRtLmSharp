using LiteLMSharp.SampleMaui.Pages;
using LiteLMSharp.SampleMaui.Services;
using Microsoft.Extensions.Logging;

namespace LiteLMSharp.SampleMaui;

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

		// App services (singletons: one engine + one store per process).
		builder.Services.AddSingleton<ModelStore>();
		builder.Services.AddSingleton<EngineService>();
		builder.Services.AddTransient<ModelsPage>();
		builder.Services.AddTransient<ChatPage>();
		builder.Services.AddTransient<ToolsPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
