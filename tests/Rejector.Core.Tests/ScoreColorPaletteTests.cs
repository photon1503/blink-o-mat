using Rejector.Core.Services;

namespace Rejector.Core.Tests;

public class ScoreColorPaletteTests
{
    [Fact]
    public void ForScore_UsesGradientAcrossScoreRange()
    {
        Assert.Equal("#CD5C5C", ScoreColorPalette.ForScore(0.0));
        Assert.Equal("#DAA520", ScoreColorPalette.ForScore(2.0));
        Assert.Equal("#5CA36E", ScoreColorPalette.ForScore(5.0));
        Assert.NotEqual("#CD5C5C", ScoreColorPalette.ForScore(1.0));
        Assert.NotEqual("#DAA520", ScoreColorPalette.ForScore(1.0));
    }

    [Fact]
    public void ForBackground_UsesAlphaizedRgb()
    {
        Assert.Equal("#33DAA520", ScoreColorPalette.ForBackground(2.0));
    }
}
