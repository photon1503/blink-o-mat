using Rejector.Core.Models;
using Rejector.Core.Services;

var options = CommandLineOptions.Parse(args);

if (!options.IsHeadless)
{
	Console.WriteLine("Rejector CLI");
	Console.WriteLine("Use --headless --input <folder> --rejected <folder> to run frame analysis and move rejected files.");
	return 0;
}

return await new HeadlessRunner().RunAsync(options, CancellationToken.None);
