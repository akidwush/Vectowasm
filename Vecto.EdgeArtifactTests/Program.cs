using Vecto.Core;

static void Check(bool ok, string description)
{
    if (!ok) throw new InvalidOperationException("FAIL: " + description);
    Console.WriteLine("PASS " + description);
}

const int W = 96, H = 64;
var dark = new Rgba32(24,24,24,255);
var light = new Rgba32(236,236,236,255);
var middle = new Rgba32(132,132,132,255);
var paints = new List<PaletteEntry> {
    new(){Color=dark,PixelCount=3000},
    new(){Color=light,PixelCount=3000},
    new(){Color=middle,PixelCount=100}
};
static (RasterImage Image,int[] Labels) TwoColor(int width=96,int height=64)
{
    var img = new RasterImage(width,height);
    var map = new int[width*height];
    for(int y=0;y<height;y++)
        for(int x=0;x<width;x++)
        {
            int i=y*width+x;
            if(x<48) img.SetPixel(x,y,new Rgba32(24,24,24,255));
            else { img.SetPixel(x,y,new Rgba32(236,236,236,255)); map[i]=1; }
        }
    return (img,map);
}

var (aaImg, aaLabels)=TwoColor();
foreach(int start in new[]{8,20,32,44})
for(int y=start;y<start+6;y++)
for(int x=47;x<=48;x++)
{
    byte v=(byte)(x==47?95:185);
    aaImg.SetPixel(x,y,new Rgba32(v,v,v,255));
    aaLabels[y*W+x]=2;
}
int[] originalBandLabels=(int[])aaLabels.Clone();
byte[] originalBandRaster=(byte[])aaImg.Pixels.Clone();
var v1Labels=(int[])aaLabels.Clone();
var v1=AntiAliasBandRefiner.Refine(aaImg,v1Labels,paints);
Check(v1.BandsReassigned==0 && v1Labels.SequenceEqual(aaLabels),
    "AA V1 leaves fragmented AA strips shorter than 8px");
var mergeLabels=(int[])aaLabels.Clone();
var merge=RegionMergeRefiner.Refine(aaImg,mergeLabels,paints);
Check(merge.RelabeledPixels==0 && mergeLabels.SequenceEqual(aaLabels),
    "Region Merge V1 declines two-sided slivers");
var combined=(int[])aaLabels.Clone();
AntiAliasBandRefiner.Refine(aaImg,combined,paints);
RegionMergeRefiner.Refine(aaImg,combined,paints);
Check(combined.SequenceEqual(aaLabels),
    "Legacy AA+Merge still leaves fragmented two-sided bands");
var cleanLabels=(int[])aaLabels.Clone();
var clean=EdgeArtifactCleanupV2.Refine(
    aaImg,cleanLabels,paints,new bool[cleanLabels.Length]);
Check(clean.AABandsRemoved==4 && clean.RingingFragmentsRemoved==0 &&
      clean.RelabeledPixels==48 && clean.RegionsBefore-clean.RegionsAfter==4 &&
      !clean.RolledBack,
    "V2 removes four disconnected two-pigment AA bands atomically");
Check(cleanLabels[9*W+47]==0 && cleanLabels[9*W+48]==1 &&
      aaImg.Pixels.SequenceEqual(originalBandRaster),
    "V2 assigns AA strip using source sRGB projection without editing RGBA");
var repeat=(int[])originalBandLabels.Clone();
Check(EdgeArtifactCleanupV2.Refine(aaImg,repeat,paints,new bool[repeat.Length])
      == clean with { DiagnosticRegionMap = clean.DiagnosticRegionMap } ||
      repeat.SequenceEqual(cleanLabels),
    "V2 AA segmentation is deterministic");

var (ringImg,ringLabels)=TwoColor();
for(int y=10;y<40;y++)
{
    ringImg.SetPixel(60,y,new Rgba32(230,230,230,255));
    ringLabels[y*W+60]=0;
}
var legacyRing=(int[])ringLabels.Clone();
var ringV1=RegionMergeRefiner.Refine(ringImg,legacyRing,paints);
Check(ringV1.RelabeledPixels==0 && legacyRing.SequenceEqual(ringLabels),
    "Legacy area-based merge misses detached 1x30 ringing sliver");
var ringBefore=(int[])ringLabels.Clone();
var ringOut=EdgeArtifactCleanupV2.Refine(ringImg,ringLabels,paints,new bool[ringLabels.Length]);
Check(ringOut.RingingFragmentsRemoved==1 && ringOut.RelabeledPixels==30 &&
      ringOut.RegionsBefore-ringOut.RegionsAfter==1 &&
      Enumerable.Range(10,30).All(y=>ringLabels[y*W+60]==1),
    "V2 catches long thin 1x30 A-label oscillation inside B");

var (speckImg,speckLabels)=TwoColor();
foreach(int x in new[]{55,64,73})
for(int y=20;y<22;y++)
for(int dx=0;dx<2;dx++)
{
    speckImg.SetPixel(x+dx,y,new Rgba32(226,230,227,255));
    speckLabels[y*W+x+dx]=0;
}
var speckOut=EdgeArtifactCleanupV2.Refine(speckImg,speckLabels,paints,new bool[speckLabels.Length]);
Check(speckOut.RingingFragmentsRemoved==3 && speckOut.RelabeledPixels==12,
    "V2 removes JPEG-like 2x2 mislabeled specks at color border");

var (solidImg,solidLabels)=TwoColor();
for(int y=10;y<40;y++)
{
    solidImg.SetPixel(60,y,new Rgba32(24,24,24,255));
    solidLabels[y*W+60]=0;
}
var protectedStroke=DetailProtectionV2.Capture(
    solidImg,solidLabels,new[]{Oklab.FromRgb(24,24,24), Oklab.FromRgb(236,236,236)});
Check(protectedStroke.Stats.ProtectedRegions>0 && protectedStroke.Pixels[20*W+60],
    "Pre-absorption protection recognizes genuine high-contrast 1px stroke");
var solidBefore=(int[])solidLabels.Clone();
var solidStats=EdgeArtifactCleanupV2.Refine(solidImg,solidLabels,paints,protectedStroke.Pixels);
Check(solidLabels.SequenceEqual(solidBefore) && solidStats.RelabeledPixels==0,
    "True solid 1px x 30px stroke survives V2");

var (eyeImg,eyeLabels)=TwoColor();
eyeImg.SetPixel(20,20,new Rgba32(236,236,236,255));
eyeLabels[20*W+20]=1;
var eyeMask=DetailProtectionV2.Capture(
    eyeImg,eyeLabels,new[]{Oklab.FromRgb(24,24,24),Oklab.FromRgb(236,236,236)});
Check(eyeMask.Pixels[20*W+20], "Real white 1px eye glint protected before any absorption");
var eyeBefore=(int[])eyeLabels.Clone();
var eyeMerge=RegionMergeRefiner.Refine(eyeImg,eyeLabels,paints,24,default,eyeMask.Pixels);
Check(eyeLabels.SequenceEqual(eyeBefore) && eyeMerge.RelabeledPixels==0,
    "Protected white eye glint survives optional Region Merge V1");

var (alphaImg,alphaLabels)=TwoColor();
for(int y=23;y<30;y++)
{
    alphaImg.SetPixel(49,y,new Rgba32(0,0,0,0)); alphaLabels[y*W+49]=-1;
}
var alphaBefore=(int[])alphaLabels.Clone();
var alphaStats=EdgeArtifactCleanupV2.Refine(alphaImg,alphaLabels,paints,new bool[alphaLabels.Length]);
Check(alphaLabels.Count(x=>x<0)==alphaBefore.Count(x=>x<0),
    "V2 cannot change transparent pixels");

var (coreImg,coreLabels)=TwoColor();
for(int y=10;y<16;y++)
for(int x=47;x<=48;x++)
{
    coreImg.SetPixel(x,y,middle);
    coreLabels[y*W+x]=2;
}
var coreBefore=(int[])coreLabels.Clone();
var coreStats=EdgeArtifactCleanupV2.Refine(coreImg,coreLabels,paints,new bool[coreLabels.Length]);
Check(coreStats.RelabeledPixels==0 && coreLabels.SequenceEqual(coreBefore),
    "Real constant third-color ribbon is protected from AA cleanup");

Check(!new TraceOptions().EdgeArtifactCleanupV2 &&
      !new TraceOptions().EnableTopologySeamProtection,
    "V2 and seam-protection flags remain disabled by default");
var exact=Tracer.Trace(TwoColor().Image,new TraceOptions
{
    Style=ImageStyle.Crisp,PaletteMode=PaletteMode.FixedCount,ColorCount=2,
    EdgeArtifactCleanupV2=true
});
Check(exact.Diagnostics.V2Skipped && exact.Diagnostics.V2Artifacts.RelabeledPixels==0,
    "Exact-paletted artwork bypasses V2");

Console.WriteLine("EDGE_ARTIFACT_V2_SYNTHETIC_FIXTURES_PASS");
