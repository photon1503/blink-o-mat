using System.Globalization;

namespace Rejector.Core.Models;

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

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--headless":
                    headless = true;
                    break;
                case "--input":
                    inputFolder = ReadValue(args, ref index);
                    break;
                case "--rejected":
                    rejectedFolder = ReadValue(args, ref index);
                    break;
                case "--max-fwhm":
                    thresholds.MaxFwhm = ParseDouble(ReadValue(args, ref index), thresholds.MaxFwhm);
                    break;
                case "--max-hfr":
                    thresholds.MaxHfr = ParseDouble(ReadValue(args, ref index), thresholds.MaxHfr);
                    break;
                case "--max-ecc":
                    thresholds.MaxEccentricity = ParseDouble(ReadValue(args, ref index), thresholds.MaxEccentricity);
                    break;
                case "--max-bg":
                    thresholds.MaxMeanBackground = ParseDouble(ReadValue(args, ref index), thresholds.MaxMeanBackground);
                    break;
                case "--allow-trails":
                    thresholds.MinSatelliteConfidence = 0;
                    break;
            }
        }

        return new CommandLineOptions
        {
            IsHeadless = headless,
            InputFolder = inputFolder,
            RejectedFolder = rejectedFolder,
            Thresholds = thresholds,
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
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}