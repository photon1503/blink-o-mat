using Rejector.Core.Models;

namespace Rejector.Core.Services;

public sealed record FrameSortRule(string Field, bool IsAscending);

public sealed class FrameSortComparer : IComparer<ProcessedFrame>
{
    private readonly IReadOnlyList<FrameSortRule> _rules;

    public FrameSortComparer(IEnumerable<FrameSortRule>? rules)
    {
        _rules = (rules ?? []).ToArray();
    }

    public int Compare(ProcessedFrame? x, ProcessedFrame? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        foreach (var rule in _rules)
        {
            var comparison = CompareByRule(x, y, rule);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return StringComparer.OrdinalIgnoreCase.Compare(x.FileName, y.FileName);
    }

    private static int CompareByRule(ProcessedFrame left, ProcessedFrame right, FrameSortRule rule)
    {
        return rule.Field switch
        {
            _ when IsField(rule.Field, "score") => CompareValues(left.OverallScore, right.OverallScore, rule.IsAscending),
            _ when IsField(rule.Field, "observation date") => CompareNullableValues(left.ExposureDateTime, right.ExposureDateTime, rule.IsAscending),
            _ when IsField(rule.Field, "fwhm") => CompareValues(left.Metrics.Fwhm, right.Metrics.Fwhm, rule.IsAscending),
            _ when IsField(rule.Field, "fwhmarcsec") => CompareNullableValues(left.Metrics.FwhmArcsec, right.Metrics.FwhmArcsec, rule.IsAscending),
            _ when IsField(rule.Field, "sqm") => CompareNullableValues(left.Metrics.Sqm, right.Metrics.Sqm, rule.IsAscending),
            _ when IsField(rule.Field, "sky temp") => CompareNullableValues(left.Metrics.SkyTemp, right.Metrics.SkyTemp, rule.IsAscending),
            _ when IsField(rule.Field, "hfr") => CompareValues(left.Metrics.Hfr, right.Metrics.Hfr, rule.IsAscending),
            _ when IsField(rule.Field, "stars") => CompareValues(left.Metrics.StarCount, right.Metrics.StarCount, rule.IsAscending),
            _ when IsField(rule.Field, "eccentricity") => CompareValues(left.Metrics.Eccentricity, right.Metrics.Eccentricity, rule.IsAscending),
            _ when IsField(rule.Field, "mean background") => CompareValues(left.Metrics.MeanBackground, right.Metrics.MeanBackground, rule.IsAscending),
            _ when IsField(rule.Field, "median") => CompareValues(left.Metrics.Median, right.Metrics.Median, rule.IsAscending),
            _ when IsField(rule.Field, "mad") => CompareValues(left.Metrics.Mad, right.Metrics.Mad, rule.IsAscending),
            _ when IsField(rule.Field, "min") => CompareValues(left.Metrics.Min, right.Metrics.Min, rule.IsAscending),
            _ when IsField(rule.Field, "min count") => CompareValues(left.Metrics.MinCount, right.Metrics.MinCount, rule.IsAscending),
            _ when IsField(rule.Field, "max") => CompareValues(left.Metrics.Max, right.Metrics.Max, rule.IsAscending),
            _ when IsField(rule.Field, "max count") => CompareValues(left.Metrics.MaxCount, right.Metrics.MaxCount, rule.IsAscending),
            _ when IsField(rule.Field, "filename") => CompareValues(left.FileName, right.FileName, rule.IsAscending, StringComparer.OrdinalIgnoreCase),
            _ => 0,
        };
    }

    private static int CompareValues<T>(T left, T right, bool isAscending)
        where T : IComparable<T>
    {
        var comparison = left.CompareTo(right);
        return isAscending ? comparison : -comparison;
    }

    private static int CompareValues<T>(T left, T right, bool isAscending, IComparer<T> comparer)
    {
        var comparison = comparer.Compare(left, right);
        return isAscending ? comparison : -comparison;
    }

    private static int CompareNullableValues<T>(T? left, T? right, bool isAscending)
        where T : struct, IComparable<T>
    {
        if (!left.HasValue && !right.HasValue)
        {
            return 0;
        }

        if (!left.HasValue)
        {
            return 1;
        }

        if (!right.HasValue)
        {
            return -1;
        }

        var comparison = left.Value.CompareTo(right.Value);
        return isAscending ? comparison : -comparison;
    }

    private static bool IsField(string? fieldName, string expected)
    {
        return string.Equals(NormalizeField(fieldName), NormalizeField(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeField(string? fieldName)
    {
        return string.IsNullOrWhiteSpace(fieldName)
            ? string.Empty
            : new string(fieldName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
