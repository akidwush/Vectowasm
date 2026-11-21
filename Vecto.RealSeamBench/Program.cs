using Stopwatch = System.Diagnostics.Stopwatch;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vecto.Core;

bool photo = args.Length == 5 && args[4] == "photo";
if (args.Length != 4 && !photo)
{
    Console.Error.WriteLine("Usage: real-seam-bench <input.rgba> <width> <height> <out-dir> [photo]");
    return 2;
}
string input = args[0];
int width = int.Parse(args[1]), height = int.Parse(args[2]);
string folder = args[3];
if (width < 1 || height < 1 || width > 1024 || height > 1024)
    throw new ArgumentOutOfRangeException(nameof(width));
byte[] rgba = File.ReadAllBytes(input);
if (rgba.Length != checked(width * height * 4))
    throw new InvalidDataException("Expected RGBA8888 input with exact width*height*4 bytes");
var image = new RasterImage(width, height, rgba);
Directory.CreateDirectory(folder);
TraceOptions Options(bool seam) => new()
{
    Style = photo ? ImageStyle.Photo : ImageStyle.Crisp,
    ShapeMode = ShapeMode.Stacked,
    Detail = photo ? DetailLevel.Medium : DetailLevel.High,
    PaletteMode = PaletteMode.FixedCount,
    ColorCount = photo ? 8 : 7,
    Seed = 1,
    CurveFitting = true,
    OptimizeNodes = true,
    StackedLayerReasoningV1 = false,
    EnableTopologySeamProtection = seam,
    TopologySeamBleedPx = .35
};

var watch = Stopwatch.StartNew();
var original = Tracer.Trace(image, Options(false));
long offMs = watch.ElapsedMilliseconds;
watch.Restart();
var protectedTrace = Tracer.Trace(image, Options(true));
long onMs = watch.ElapsedMilliseconds;
string offSvg = SvgWriter.Write(original.Document);
string onSvg = SvgWriter.Write(protectedTrace.Document);
string stem = photo ? "photo" : "tiger";
string offPath = Path.Combine(folder, stem + "_stacked_OFF.svg");
string onPath = Path.Combine(folder, stem + "_stacked_ON.svg");
File.WriteAllText(offPath, offSvg);
File.WriteAllText(onPath, onSvg);

var pathTags = new Regex(@"<path\b[^>]*?/>", RegexOptions.Compiled | RegexOptions.Singleline);
var dAttribute = new Regex(@"\sd=""([^""]*)""", RegexOptions.Compiled);
var commands = new Regex(@"[MmLlHhVvCcSsQqTtAaZz]", RegexOptions.Compiled);
string[] Tags(string svg) => pathTags.Matches(svg).Select(x => x.Value).ToArray();
string[] OrigPaths(string svg) => Tags(svg).Where(x => !x.Contains("data-seam", StringComparison.Ordinal)).ToArray();
string[] basePaths = OrigPaths(offSvg), protectedPaths = OrigPaths(onSvg);
bool unchanged = basePaths.SequenceEqual(protectedPaths);
var baseTags = Tags(offSvg);
var onTags = Tags(onSvg);
int AllPaths(string svg) => Tags(svg).Length;
// Count explicit commands in BOTH compact/relative helper paths and main paths.
int Count(string[] tags, string types, bool? seam = null) => tags
    .Where(tag => seam is null || tag.Contains("data-seam", StringComparison.Ordinal) == seam)
    .Sum(tag => commands.Matches(dAttribute.Match(tag).Groups[1].Value)
         .Count(cmd => types.Contains(cmd.Value[0])));
const string draw = "LlHhVvCcSsQqTtAa";
const string move = "Mm";
const string cubic = "CcSs";
const string line = "LlHhVv";
int SourceHelperCurves() => protectedTrace.Document.Regions.Sum(r =>
    r.SeamUnderpaints.Sum(x => x.Curves.Count));
long offBytes = System.Text.Encoding.UTF8.GetByteCount(offSvg);
long onBytes = System.Text.Encoding.UTF8.GetByteCount(onSvg);
var record = new
{
    fixture = photo ? "native_raster_astronaut_png" : "previously_uploaded_real_tiger_vector_rasterized_into_RGBA",
    source = photo ? "skimage.data.astronaut" : "tests/fixtures/tiger_source_user.svg",
    width, height, style = photo ? "Photo" : "Crisp", colors = photo ? 8 : 7, detail = photo ? "Medium" : "High",
    setup = "Stacked Shapes; original fitter; 0.35px underpaint ONLY when flag ON",
    off_bytes = offBytes,
    on_bytes = onBytes,
    off_kb = Math.Round(offBytes / 1024.0, 3),
    on_kb = Math.Round(onBytes / 1024.0, 3),
    increase_pct = Math.Round(100.0 * (onBytes - offBytes) / Math.Max(offBytes, 1), 4),
    off_paths = AllPaths(offSvg),
    on_paths = AllPaths(onSvg),
    off_main_paths = basePaths.Length,
    on_main_paths = protectedPaths.Length,
    off_subpaths = Count(baseTags, move),
    on_subpaths = Count(onTags, move),
    off_cubic_commands = Count(baseTags, cubic),
    on_cubic_commands = Count(onTags, cubic),
    off_line_commands = Count(baseTags, line),
    on_line_commands = Count(onTags, line),
    source_underpaint_curves = SourceHelperCurves(),
    emitted_underpaint_cubic_commands = Count(onTags, cubic, true),
    emitted_underpaint_line_commands = Count(onTags, line, true),
    emitted_underpaint_nodes = Count(onTags, draw + move, true),
    main_paths_byte_identical = unchanged,
    off_ms = offMs, on_ms = onMs,
    off_nodes = Count(baseTags, draw + move),
    on_nodes = Count(onTags, draw + move),
    original_region_nodes_off = original.Diagnostics.NodeCount,
    original_region_nodes_on = protectedTrace.Diagnostics.NodeCount,
    stats = protectedTrace.Diagnostics.TopologySeams,
};
string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(folder, "benchmark_core.json"), json);
Console.WriteLine("TIGER_REAL_BENCH " + json);
if (!unchanged) throw new InvalidOperationException("A visible-region path changed under seam protection");
if (SourceHelperCurves() != protectedTrace.Diagnostics.TopologySeams.UnderpaintCurves)
    throw new InvalidOperationException("Helper source-curve count is inconsistent");
if (Count(onTags, draw, true) == 0 && SourceHelperCurves() > 0)
    throw new InvalidOperationException("No helper geometry survived serialization");
if (offBytes == onBytes && protectedTrace.Diagnostics.TopologySeams.UnderpaintRuns > 0)
    throw new InvalidOperationException("Seam helpers were selected but no extra SVG output was generated");
return 0;
