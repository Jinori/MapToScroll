#region
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DALib.Data;
using DALib.Drawing;
using DALib.Utility;
using SkiaSharp;
#endregion

const string SEO_DAT_PATH = @"C:\Users\Despe\Desktop\Unora\Unora\seo.dat";
const string IA_DAT_PATH = @"C:\Users\Despe\Desktop\Unora\Unora\ia.dat";
const string JSON_DIR = @"D:\repos\Jinori\Unora\Data\Configuration\Templates\Maps";
const string MAP_INSTANCES_DIR = @"D:\repos\Jinori\Unora\Data\Configuration\MapInstances";
const string BACKGROUND_IMAGE_RESOURCE = "GenerateTMaps.Assets.background.png";
const int FRAME_BACKGROUND_WIDTH = 568;
const int FRAME_BACKGROUND_HEIGHT = 406;
var someNumber = 0;

var renderLock = new Lock();
var backgroundImage = LoadEmbeddedBackgroundImage();

using var seoDat = DataArchive.FromFile(SEO_DAT_PATH);
using var iaDat = DataArchive.FromFile(IA_DAT_PATH);
var tileSet = Tileset.FromArchive("tilea", seoDat);
var bgPaletteLookup = PaletteLookup.FromArchive("mpt", seoDat);
var fgPaletteLookup = PaletteLookup.FromArchive("stc", iaDat);
var mapImageCache = new MapImageCache();

Console.Write("Map file dir: ");
var mapFileDir = Console.ReadLine();

// Get all valid map numbers from MapInstancesRoot
var validMapNumbers = GetUsedMapNumbers()
    .ToHashSet();

// Find map files that match the valid templateKeys
var mapFiles = Directory.EnumerateFiles(mapFileDir!, "*.map")
                        .Where(
                            path =>
                            {
                                var mapNumber = ExtractMapNumber(path);

                                if (!mapNumber.HasValue)
                                    return false;

                                return validMapNumbers.Contains(mapNumber.Value);
                            });

var saveDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SPF_Maps");
Directory.CreateDirectory(saveDirectory);

var tCoordTextBuilder = new StringBuilder();

Parallel.ForEach(
    mapFiles,
    mapFilePath => ProcessMapFileAsync(
        mapFilePath,
        saveDirectory,
        tCoordTextBuilder,
        tileSet,
        bgPaletteLookup,
        fgPaletteLookup,
        iaDat,
        mapImageCache));

File.WriteAllText("tcoord.tbl", tCoordTextBuilder.ToString());

void ProcessMapFileAsync(
    string mapFilePath,
    string localSaveDir,
    StringBuilder localTCoordBuilder,
    Tileset localTileSet,
    PaletteLookup localBgPaletteLookup,
    PaletteLookup localFgPaletteLookup,
    DataArchive localIaDat,
    MapImageCache localMapImageCache)
{
    var mapNumber = ExtractMapNumber(mapFilePath);

    if (!mapNumber.HasValue)
        return;

    var tileDimensions = LoadMapTileDimensions(mapNumber.Value);

    if (tileDimensions == null)
        return;

    var tileWidth = tileDimensions.Value.width;
    var tileHeight = tileDimensions.Value.height;

    var map = MapFile.FromFile(mapFilePath, tileWidth, tileHeight);
    SKImage? renderedImage;

    lock (renderLock)
        renderedImage = Graphics.RenderMap(
            map,
            localTileSet,
            localBgPaletteLookup,
            localFgPaletteLookup,
            localIaDat,
            0,
            localMapImageCache);

    var renderedMapFilePath = Path.Combine(localSaveDir, $"_t{mapNumber}.spf");
    var nameplateFilePath = Path.Combine(localSaveDir, $"_t{mapNumber}n.spf");

    var finalImage = OverlayMapOnBackground(renderedImage);

    if (finalImage == null)
        return;

    // Convert final image to SPF and save
    var spfFile = SpfFile.FromImages(finalImage);
    spfFile.Save(renderedMapFilePath);

    // Copy and rename nameplate SPF
    CopyAndRenameNameplate(nameplateFilePath);

    var mapName = ExtractMapName(mapNumber.Value) ?? "Untitled" + mapNumber;
    var xCoord = -((FRAME_BACKGROUND_WIDTH - finalImage.Width) / 2);
    var yCoord = (FRAME_BACKGROUND_HEIGHT - finalImage.Height) / 2;

    var newEntry = $"{mapNumber} {Regex.Replace(mapName, @"\d", "")} {xCoord},{yCoord} {tileWidth} {tileHeight}";

    //stringbuilder is not threadsafe
    lock (localTCoordBuilder)
        localTCoordBuilder.AppendLine(newEntry);
    
    renderedImage.Dispose();
}

#region Image Manipulation

SKImage? OverlayMapOnBackground(SKImage mapImage)
{
    var targetWidth = backgroundImage!.Width;
    var targetHeight = backgroundImage.Height;

    using var croppedMap = mapImage;//CropTopEmptySpace(mapImage);

    if (croppedMap == null)
        return null;

    using var resizedImage = ResizeImage(croppedMap, targetWidth, targetHeight);
    using var finalBitmap = new SKBitmap(targetWidth, targetHeight);
    using var canvas = new SKCanvas(finalBitmap);

    var targetX = (targetWidth - resizedImage.Width) / 2f;
    var targetY = (targetHeight - resizedImage.Height) / 2f;

    canvas.DrawImage(backgroundImage, 0, 0);
    canvas.DrawImage(resizedImage, targetX, targetY);
    canvas.Flush();
    
    return SKImage.FromBitmap(finalBitmap);
}

static SKImage? CropTopEmptySpace(SKImage image)
{
    using var bitmap = SKBitmap.FromImage(image);
    var width = bitmap.Width;
    var height = bitmap.Height;

    var backgroundColor = bitmap.GetPixel(0, 0);

    //iterate pixels left to right, top to bottom
    //till we find the first pixel that isnt the background color
    //then crop the image to that point (cut off the top to that point)

    for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);

            if (pixel != backgroundColor)
            {
                var cropRect = new SKRectI(
                    0,
                    y,
                    width,
                    height);

                return image.Subset(cropRect);
            }
        }

    return null;
}

static SKImage ResizeImage(SKImage image, int maxWidth, int maxHeight)
{
    var widthRatio = maxWidth / (float)image.Width;
    var heightRatio = maxHeight / (float)image.Height;
    var scaleFactor = Math.Min(widthRatio, heightRatio);

    var newWidth = (int)(image.Width * scaleFactor);
    var newHeight = (int)(image.Height * scaleFactor);

    using var resizedBitmap = new SKBitmap(newWidth, newHeight);
    using var canvas = new SKCanvas(resizedBitmap);

    var sampling = new SKSamplingOptions(SKFilterMode.Nearest);

    var destRect = new SKRect(
        0,
        0,
        newWidth,
        newHeight);

    canvas.DrawImage(image, destRect, sampling);

    canvas.Flush();

    var img = SKImage.FromBitmap(resizedBitmap);

    return img;
}
#endregion

#region Utility
IEnumerable<int> GetUsedMapNumbers()
{
    foreach (var file in Directory.EnumerateFiles(MAP_INSTANCES_DIR, "instance.json", SearchOption.AllDirectories))
    {
        using var jsonStream = File.OpenRead(file);
        var jsonObject = JsonSerializer.Deserialize<JsonObject>(jsonStream);

        if (jsonObject?.TryGetPropertyValue("templateKey", out var propertyNode) ?? false)
        {
            var mapNumStr = propertyNode!.GetValue<string>();

            if (!string.IsNullOrEmpty(mapNumStr) && int.TryParse(mapNumStr, out var mapNum))
                yield return mapNum;
        }
    }
}

static (int width, int height)? LoadMapTileDimensions(int mapNumber)
{
    var path = Path.Combine(JSON_DIR, $"{mapNumber}.json");

    if (!File.Exists(path))
        return null;

    using var jsonStream = File.OpenRead(path);
    var jsonObject = JsonSerializer.Deserialize<JsonObject>(jsonStream);

    if (jsonObject == null)
        return null;

    if (!jsonObject.TryGetPropertyValue("width", out var widthNode) || !jsonObject.TryGetPropertyValue("height", out var heightNode))
        return null;

    return (widthNode!.GetValue<int>(), heightNode!.GetValue<int>());
}

static int? ExtractMapNumber(string filePath)
{
    var filename = Path.GetFileNameWithoutExtension(filePath);
    var numberPart = filename[3..]; // skil "lod" prefix

    return int.TryParse(numberPart, out var result) ? result : null;
}

static SKImage LoadEmbeddedBackgroundImage()
{
    var assembly = Assembly.GetExecutingAssembly();
    using var stream = assembly.GetManifestResourceStream(BACKGROUND_IMAGE_RESOURCE);

    if (stream == null)
        throw new FileNotFoundException("Embedded background image not found.");

    using var skBitmap = SKBitmap.Decode(stream);

    return SKImage.FromBitmap(skBitmap);
}

static void CopyAndRenameNameplate(string destinationPath)
{
    var resourcePath = "GenerateTMaps.Assets._nameplate.spf"; // Ensure this is embedded in your project

    using var resourceStream = Assembly.GetExecutingAssembly()
                                       .GetManifestResourceStream(resourcePath);

    if (resourceStream == null)
        throw new FileNotFoundException("Embedded resource not found: " + resourcePath);

    using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
    resourceStream.CopyTo(fileStream);
}

static string? ExtractMapName(int mapNumber)
{
    foreach (var file in Directory.EnumerateFiles(MAP_INSTANCES_DIR, "instance.json", SearchOption.AllDirectories))
    {
        using var jsonStream = File.OpenRead(file);

        var jsonObject = JsonSerializer.Deserialize<JsonObject>(jsonStream);

        if (jsonObject?.TryGetPropertyValue("templateKey", out var propertyNode) ?? false)
        {
            var mapNumStr = propertyNode!.GetValue<string>();

            if (string.IsNullOrEmpty(mapNumStr) || !int.TryParse(mapNumStr, out var mapNum) || (mapNum != mapNumber))
                continue;

            if (jsonObject.TryGetPropertyValue("name", out var nameNode))
                return nameNode!.GetValue<string>();
        }
    }

    return null;
}
#endregion