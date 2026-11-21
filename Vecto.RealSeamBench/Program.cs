using Stopwatch = System.Diagnostics.Stopwatch;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vecto.Core;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: real-seam-bench <input.rgba> <width> <height> <out-dir>");
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
    Style = ImageStyle.Crisp,
    ShapeMode = ShapeMode.Stacked,
    Detail = DetailLevel.High,
    PaletteMode = PaletteMode.FixedCount,
    ColorCount = 7,
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
string offPath = Path.Combine(folder, "tiger_stacked_OFF.svg");
string onPath = Path.Combine(folder, "tiger_stacked_ON.svg");
File.WriteAllText(offPath, offSvg);
File.WriteAllText(onPath, onSvg);

var originalOnly = new Regex(@"<path [^>]*?/>", RegexOptions.Compiled | RegexOptions.Singleline);
string[] OrigPaths(string svg) => originalOnly.Matches(svg).Select(m => m.Value)
    .Where(p => !p.Contains("data-seam", StringComparison.Ordinal)).ToArray();
string[] basePaths = OrigPaths(offSvg), protectedPaths = OrigPaths(onSvg);
bool unchanged = basePaths.SequenceEqual(protectedPaths);
int AllPaths(string svg) => originalOnly.Matches(svg).Count;
int SubPaths(string svg) => Regex.Matches(svg, @"\sM\s").Count;
int Curves(string svg) => Regex.Matches(svg, @"\sC\s").Count;
long offBytes = System.Text.Encoding.UTF8.GetByteCount(offSvg);
long onBytes = System.Text.Encoding.UTF8.GetByteCount(onSvg);
var record = new
{
    fixture = "previously_uploaded_real_tiger_vector_rasterized_into_RGBA",
    source = "tests/fixtures/tiger_source_user.svg",
    width, height, style = "Crisp", colors = 7, detail = "High",
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
    off_subpaths = SubPaths(offSvg),
    on_subpaths = SubPaths(onSvg),
    off_cubic_commands = Curves(offSvg),
    on_cubic_commands = Curves(onSvg),
    main_paths_byte_identical = unchanged,
    off_ms = offMs, on_ms = onMs,
    off_nodes = original.Diagnostics.NodeCount,
    on_nodes = protectedTrace.Diagnostics.NodeCount,
    stats = protectedTrace.Diagnostics.TopologySeams,
};
string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(folder, "benchmark_core.json"), json);
Console.WriteLine("TIGER_REAL_BENCH " + json);
if (!unchanged) throw new InvalidOperationException("A visible-region path changed under seam protection");
if (offBytes == onBytes && protectedTrace.Diagnostics.TopologySeams.UnderpaintRuns > 0)
    throw new InvalidOperationException("Seam helpers were selected but no extra SVG output was generated");
return 0;
