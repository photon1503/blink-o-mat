namespace blink_o_mat.Services;

/// <summary>
/// Canonical filter categories inferred from the raw filter name. Detection
/// is based on the first non-whitespace letter, which is assumed to be unique
/// across the supported astrophotography filters (Ha, Oiii, Sii, L, R, G, B).
/// </summary>
public enum FilterCategory
{
    Unknown,
    Ha,
    OIII,
    SII,
    Lum,
    Red,
    Green,
    Blue,
}

/// <summary>High-level grouping of filter categories.</summary>
public enum FilterGroup
{
    Other,
    Narrowband,
    Lrgb,
}

/// <summary>
/// Lightweight classifier that maps arbitrary user-entered filter names
/// (e.g. "Halpha", "H_a", "OIII_3nm", "Lum", "Red") to a known category,
/// display name, group, sort order and chip color.
/// Unknown filters keep their original name and a neutral color.
/// </summary>
public static class FilterClassifier
{
    public static FilterCategory Classify(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return FilterCategory.Unknown;
        }

        var trimmed = rawName.TrimStart();
        if (trimmed.Length == 0)
        {
            return FilterCategory.Unknown;
        }

        return char.ToUpperInvariant(trimmed[0]) switch
        {
            'H' => FilterCategory.Ha,
            'O' => FilterCategory.OIII,
            'S' => FilterCategory.SII,
            'L' => FilterCategory.Lum,
            'R' => FilterCategory.Red,
            'G' => FilterCategory.Green,
            'B' => FilterCategory.Blue,
            _ => FilterCategory.Unknown,
        };
    }

    public static FilterGroup GetGroup(FilterCategory category) => category switch
    {
        FilterCategory.Ha or FilterCategory.OIII or FilterCategory.SII => FilterGroup.Narrowband,
        FilterCategory.Lum or FilterCategory.Red or FilterCategory.Green or FilterCategory.Blue => FilterGroup.Lrgb,
        _ => FilterGroup.Other,
    };

    public static string GetGroupDisplay(FilterGroup group) => group switch
    {
        FilterGroup.Narrowband => "Narrowband",
        FilterGroup.Lrgb => "LRGB",
        _ => "Other",
    };

    /// <summary>Returns the canonical short name for the category, or the
    /// original name when the category is <see cref="FilterCategory.Unknown"/>.</summary>
    public static string GetCanonicalDisplay(FilterCategory category, string original) => category switch
    {
        FilterCategory.Ha => "Ha",
        FilterCategory.OIII => "OIII",
        FilterCategory.SII => "SII",
        FilterCategory.Lum => "L",
        FilterCategory.Red => "R",
        FilterCategory.Green => "G",
        FilterCategory.Blue => "B",
        _ => original,
    };

    /// <summary>Sort order for grouped display: narrowband first (Ha, Oiii, Sii),
    /// then LRGB (L, R, G, B), then unknown filters.</summary>
    public static int GetSortOrder(FilterCategory category) => category switch
    {
        FilterCategory.Ha => 0,
        FilterCategory.OIII => 1,
        FilterCategory.SII => 2,
        FilterCategory.Lum => 10,
        FilterCategory.Red => 11,
        FilterCategory.Green => 12,
        FilterCategory.Blue => 13,
        _ => 100,
    };

    /// <summary>Background / border / foreground hex strings for a chip
    /// representing the given category. Tuned for the dark UI theme.</summary>
    public static (string Background, string Border, string Foreground) GetColors(FilterCategory category) => category switch
    {
        FilterCategory.Ha => ("#33C44D4D", "#E07A7A", "#FFFFE8E8"),
        FilterCategory.OIII => ("#3340B5B5", "#6FD8D8", "#FFE0FFFF"),
        FilterCategory.SII => ("#33C7A23A", "#E0C36F", "#FFFFF4D8"),
        FilterCategory.Lum => ("#33CCCCCC", "#DDDDDD", "#FFFFFFFF"),
        FilterCategory.Red => ("#33D14545", "#FF7070", "#FFFFE0E0"),
        FilterCategory.Green => ("#3358B85F", "#7FE090", "#FFE0FFE5"),
        FilterCategory.Blue => ("#334F78D1", "#8FB3FF", "#FFE8F0FF"),
        _ => ("#222F426B", "#6D88C4", "#FFE8F0FF"),
    };
}