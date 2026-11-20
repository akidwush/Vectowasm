namespace Vecto.Core;

public enum ImageStyle
{
    Auto,
    /// <summary>Aliased art (flat colors, hard edges) — palette taken verbatim when small enough.</summary>
    Crisp,
    /// <summary>Anti-aliased art — palette inferred from flat areas, fringes excluded.</summary>
    Blended,
    /// <summary>Continuous-tone input — denoised first, tighter cluster merging.</summary>
    Photo,
}

public enum DetailLevel { Low, Medium, High }

public enum PaletteMode { Auto, FixedCount }

public enum ShapeMode { Planar, Stacked }

/// <summary>Controls which post-fit node reduction algorithm is run.</summary>
public enum BezierOptimizationMode { Pairwise, GlobalDp }

public sealed class TraceOptions
{
    public PaletteMode PaletteMode { get; set; } = PaletteMode.Auto;
    /// <summary>Upper bound on inferred colors when <see cref="PaletteMode.Auto"/>.</summary>
    public int MaxColors { get; set; } = 16;
    /// <summary>Exact color count when <see cref="PaletteMode.FixedCount"/>.</summary>
    public int ColorCount { get; set; } = 12;
    public ImageStyle Style { get; set; } = ImageStyle.Auto;
    public ShapeMode ShapeMode { get; set; } = ShapeMode.Planar;
    public DetailLevel Detail { get; set; } = DetailLevel.Medium;
    /// <summary>false → emit simplified polygons instead of fitted curves (debug/inspection).</summary>
    public bool CurveFitting { get; set; } = true;
    /// <summary>Reduce redundant fitted cubic segments while preserving sharp corners.</summary>
    public bool OptimizeNodes { get; set; } = true;
    /// <summary>Only used when OptimizeNodes and CurveFitting are enabled. GlobalDp remains production default.</summary>
    public BezierOptimizationMode BezierMode { get; set; } = BezierOptimizationMode.GlobalDp;
    /// <summary>Experimental edge-coverage isovalue; default 0.5 retains the production behavior.</summary>
    public double EdgeCoverageThreshold { get; set; } = 0.5;
    /// <summary>Optional source RGBA for sub-pixel sampling only. Null uses tracing raster (production behavior).</summary>
    public RasterImage? EdgeReferenceImage { get; set; }
    /// <summary>Experiment only. Reproject smooth points to the local 50% coverage crossing after smoothing.</summary>
    public bool LocalEdgeRefinementV3 { get; set; } = false;
    /// <summary>Maximum additional point shift in source pixels; does not unlock corners or junctions.</summary>
    public double LocalEdgeMaxShiftV3 { get; set; } = 0.20;
    /// <summary>V3 confidence filter: directional coverage change across one pixel.</summary>
    public double LocalEdgeMinGradientV3 { get; set; } = 0.25;
    /// <summary>V3 confidence filter: do not move vertices for negligible coverage residual.</summary>
    public double LocalEdgeMinCandidateShiftV3 { get; set; } = 0.012;
    /// <summary>Experiment only. Source-aware merging of tiny colour-fringe regions before extracting boundaries.</summary>
    public bool RegionMergeV1 { get; set; } = false;
    /// <summary>Maximum area of a region eligible for guarded merge; 1..128.</summary>
    public int RegionMergeMaxAreaV1 { get; set; } = 24;
    /// <summary>Experimental: compact compatible Stacked shapes into fewer paint layers.
    /// Requires ShapeMode.Stacked; preserves the default path structure when false.</summary>
    public bool StackedLayerReasoningV1 { get; set; } = false;
    /// <summary>Polygon-mode simplification tolerance; negative → derive from Detail, 0 → exact lattice polygons.</summary>
    public double PolygonEpsilon { get; set; } = -1;
    public int Seed { get; set; } = 1;
}

/// <summary>Every tuning constant the pipeline actually runs with, resolved from options + image stats.</summary>
public sealed class EffectiveParams
{
    public ImageStyle Style;
    public bool ExactPalette;
    public bool ExcludeEdgeSamples;
    public int KMeansK;
    public bool MergeClusters;
    public double MergeThreshold;     // Oklab distance (black↔white ≈ 1.0)
    public int MinRegionArea;         // px; smaller regions are absorbed into a neighbor
    public bool MedianPrefilter;
    public bool Noisy;
    /// <summary>Continuous-tone input (photo/textured art/old scan): fringe relabel and noisy treatment skipped.</summary>
    public bool Textured;
    public double NoiseShare;
    public int SmoothIterations;
    public double SmoothLambda;
    public double SmoothClamp;        // px; max drift of a boundary point from the raster crack
    public double CornerThresholdDeg; // turn between adjacent optimal-polygon edges (lattice) to become a corner
    public double CornerKeepDeg;      // the same turn re-measured on the refined geometry to stay one
    public double SubpixelMaxShift;   // px; cap on normal-direction edge refinement
    public double CornerRelocateMaxShift; // px; a corner may leave its lattice pin by at most this
    public double AngleSnapDeg;       // flank directions this close to a 45° multiple snap exactly
    public double FitToleranceSq;     // px²
    public double PolygonEpsilon;
    public int Seed;
}
