namespace blink_o_mat.Models;

public sealed class CommandLineOptions
{
    public bool IsHeadless { get; init; }
    public string? InputFolder { get; init; }
    public string? RejectedFolder { get; init; }
    public Thresholds Thresholds { get; init; } = new();

    public static CommandLineOptions Parse(string[] args)
    {
        var thresholds = new Thresholds();
        string? inputFolder = null;
        string? rejectedFolder = null;
        var headless = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--headless":
                    headless = true;
                    break;
                case "--input":
                    inputFolder = ReadValue(args, ref i);
                    break;
                case "--rejected":
                    rejectedFolder = ReadValue(args, ref i);
                    break;
                case "--max-fwhm":
                    thresholds.MaxFwhm = ParseDouble(ReadValue(args, ref i), thresholds.MaxFwhm);
                    break;
                case "--max-hfr":
                    thresholds.MaxHfr = ParseDouble(ReadValue(args, ref i), thresholds.MaxHfr);
                    break;
                case "--max-ecc":
                    thresholds.MaxEccentricity = ParseDouble(ReadValue(args, ref i), thresholds.MaxEccentricity);
                    break;
                case "--max-bg":
                    thresholds.MaxMeanBackground = ParseDouble(ReadValue(args, ref i), thresholds.MaxMeanBackground);
                    break;
                case "--allow-trails":
                    thresholds.RejectSatelliteTrail = false;
                    break;
            }
        }

        return new CommandLineOptions
        {
            IsHeadless = headless,
            InputFolder = inputFolder,
            RejectedFolder = rejectedFolder,
            Thresholds = thresholds
        };
    }

    private static string ReadValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            return string.Empty;
        }

        index++;
        return args[index];
    }

    private static double ParseDouble(string value, double fallback)
    {
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
