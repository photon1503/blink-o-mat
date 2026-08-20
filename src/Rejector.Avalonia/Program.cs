using Avalonia;

namespace Rejector.Avalonia;

internal static class Program
{
	[STAThread]
	public static void Main(string[] args)
	{
		// Frame analysis mixes blocking file I/O (Task.Run) with nested Parallel.For decode work on the
		// same global ThreadPool; without this, the pool's slow thread-injection rate (~1 thread/500ms
		// under sustained demand) starves the nested decode work and serializes concurrent frame loads.
		System.Threading.ThreadPool.SetMinThreads(Environment.ProcessorCount * 4, Environment.ProcessorCount * 4);
		BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
	}

	public static AppBuilder BuildAvaloniaApp()
	{
		return AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.WithInterFont()
			.LogToTrace();
	}
}
