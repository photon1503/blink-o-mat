namespace Rejector.Core.Models;

/// <summary>
/// PixInsight-style Screen Transfer Function parameters for image stretching.
/// </summary>
/// <param name="Shadows">Shadow clipping point [0..1]. Lower values darken shadows. Default: 0.0</param>
/// <param name="Midtones">Midtone balance [0..1]. Lower values brighten midtones. Default: 0.5</param>
/// <param name="Highlights">Highlight clipping point [0..1]. Lower values compress highlights. Default: 1.0</param>
public sealed record StfParameters(double Shadows, double Midtones, double Highlights)
{
    /// <summary>
    /// Default STF parameters: no shadow clipping, balanced midtones, no highlight clipping.
    /// </summary>
    public static readonly StfParameters Default = new(0.0, 0.5, 1.0);

    public StfParameters WithShadows(double shadows) => this with { Shadows = shadows };
    public StfParameters WithMidtones(double midtones) => this with { Midtones = midtones };
    public StfParameters WithHighlights(double highlights) => this with { Highlights = highlights };
}
