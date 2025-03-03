using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SkiaSharp;
using DALib.Data;
using DALib.Definitions;
using DALib.Drawing;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace MapToScroll
{
    public partial class MainWindow : Window
    {
        private const string SeoDatPath = @"C:\Users\Admin\Documents\Unora\seo.dat";
        private const string IaDatPath = @"C:\Users\Admin\Documents\Unora\ia.dat";
        private const string JsonDirectory = @"C:\Users\Admin\Documents\GitHub\Unora\Data\Configuration\Templates\Maps"; // Update this path
        private const string BackgroundImageResource = "MapToScroll.Assets.background.png";
        private int BackgroundWidth = 568;
        private int BackgroundHeight = 406;
        private string mapFilePath;
        private SKImage renderedMapImage;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void SelectMapFile_Click(object sender, RoutedEventArgs e)
        {
            mapFilePath = SelectFile("Map Files (*.map)|*.map", "Select a Map File");
        }

        private string SelectFile(string filter, string title)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = filter,
                Title = title
            };

            return openFileDialog.ShowDialog() == true ? openFileDialog.FileName : null;
        }

        private async Task<bool> ProcessMapFileAsync(string mapFilePath, string saveDirectory)
        {
            try
            {
                int? mapNumber = ExtractMapNumber(mapFilePath);
                if (mapNumber == null) return false;

                string jsonFilePath = Path.Combine(JsonDirectory, $"{mapNumber}.json");
                if (!File.Exists(jsonFilePath)) return false;

                (int width, int height)? dimensions = LoadMapDimensions(jsonFilePath);
                if (dimensions == null) return false;

                int x = dimensions.Value.width;
                int y = dimensions.Value.height;

                var map = MapFile.FromFile(mapFilePath, x, y);

                using var seo = DataArchive.FromFile(SeoDatPath);
                using var ia = DataArchive.FromFile(IaDatPath);
                var renderedImage = await Task.Run(() => Graphics.RenderMap(map, seo, ia));

                if (renderedImage == null) return false;

                // Define the output paths
                string renderedMapFilePath = Path.Combine(saveDirectory, $"_t{mapNumber}.spf");
                string nameplateFilePath = Path.Combine(saveDirectory, $"_t{mapNumber}n.spf");

                // Apply background before saving
                var backgroundImage = LoadEmbeddedBackgroundImage();
                var finalImage = OverlayMapOnBackground(renderedImage, backgroundImage);

                // Convert final image to SPF and save
                var spfFile = SpfFile.FromImages(finalImage);
                spfFile.Save(renderedMapFilePath);

                // Copy and rename nameplate SPF
                CopyAndRenameNameplate(nameplateFilePath);

                // Append to _tcoord.txt
                AppendToTcoordFile(mapNumber.Value, x, y, finalImage.Width, finalImage.Height);

                LogListBox.Items.Add($"Converted: {Path.GetFileName(mapFilePath)} -> {renderedMapFilePath}");
                return true;
            }
            catch (Exception ex)
            {
                LogListBox.Items.Add($"Failed: {Path.GetFileName(mapFilePath)} - {ex.Message}");
                return false;
            }
        }


        private async void BatchConvertMaps_Click(object sender, RoutedEventArgs e)
        {
            const string MapInstancesRoot = @"C:\Users\Admin\Documents\GitHub\Unora\Data\Configuration\MapInstances";

            var folderDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select Folder Containing .map Files"
            };

            if (folderDialog.ShowDialog() != CommonFileDialogResult.Ok)
                return;

            string selectedFolder = folderDialog.FileName;

            // Get all valid map numbers from MapInstancesRoot
            HashSet<string> validMapNumbers = GetUsedMapNumbers(MapInstancesRoot);

            if (validMapNumbers.Count == 0)
            {
                MessageBox.Show(
                    "No valid map instances found in MapInstancesRoot.",
                    "Info",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            // Find map files that match the valid templateKeys
            string[] mapFiles = Directory.GetFiles(selectedFolder, "*.map")
                                         .Where(file => validMapNumbers.Contains(ExtractMapNumber(file).ToString()))
                                         .ToArray();

            if (mapFiles.Length == 0)
            {
                MessageBox.Show(
                    "No matching .map files found in the selected folder.",
                    "Info",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            // Define the SPF save directory
            string saveDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SPF_Maps");
            Directory.CreateDirectory(saveDirectory);

            ProgressBar.Value = 0;
            ProgressBar.Maximum = mapFiles.Length;
            LogListBox.Items.Clear();

            List<string> failedFiles = new List<string>();

            foreach (var mapFilePath in mapFiles)
            {
                bool success = await ProcessMapFileAsync(mapFilePath, saveDirectory);

                if (!success)
                {
                    failedFiles.Add(Path.GetFileName(mapFilePath));
                }

                ProgressBar.Value++;
            }

            if (failedFiles.Count > 0)
            {
                MessageBox.Show(
                    $"Batch processing completed with {failedFiles.Count} errors.",
                    "Info",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(
                    "Batch processing completed successfully!",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private HashSet<string> GetUsedMapNumbers(string rootDirectory)
        {
            HashSet<string> mapNumbers = new HashSet<string>();

            try
            {
                foreach (string file in Directory.EnumerateFiles(rootDirectory, "instance.json", SearchOption.AllDirectories))
                {
                    string jsonContent = File.ReadAllText(file);
                    using JsonDocument doc = JsonDocument.Parse(jsonContent);

                    if (doc.RootElement.TryGetProperty("templateKey", out JsonElement templateKeyElement))
                    {
                        string? mapNumber = templateKeyElement.GetString();
                        if (!string.IsNullOrEmpty(mapNumber))
                        {
                            mapNumbers.Add(mapNumber);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error scanning MapInstancesRoot: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return mapNumbers;
        }

        
        private async void RenderMap_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(mapFilePath))
            {
                MessageBox.Show("Please select a .map file.", "Missing File", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int? mapNumber = ExtractMapNumber(mapFilePath);
            if (mapNumber == null)
            {
                MessageBox.Show("Invalid map file format. Could not extract number.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string jsonFilePath = Path.Combine(JsonDirectory, $"{mapNumber}.json");
            if (!File.Exists(jsonFilePath))
            {
                MessageBox.Show($"Matching JSON file not found: {jsonFilePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            (int width, int height)? dimensions = LoadMapDimensions(jsonFilePath);
            if (dimensions == null)
            {
                MessageBox.Show("Failed to read map dimensions from JSON.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            int x = dimensions.Value.width;
            int y = dimensions.Value.height;

            try
            {
                var map = MapFile.FromFile(mapFilePath, x, y);

                using var seo = DataArchive.FromFile(SeoDatPath);
                using var ia = DataArchive.FromFile(IaDatPath);
                renderedMapImage = await Task.Run(() => DALib.Drawing.Graphics.RenderMap(map, seo, ia));

                if (renderedMapImage != null)
                {
                    MapImage.Source = ConvertSkImageToBitmapSource(renderedMapImage);
                }
                else
                {
                    MessageBox.Show("Failed to render the map.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Rendering error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private int? ExtractMapNumber(string filePath)
        {
            string filename = Path.GetFileNameWithoutExtension(filePath);
            string numberPart = filename.Replace("lod", "");

            return int.TryParse(numberPart, out int result) ? result : null;
        }

        private (int width, int height)? LoadMapDimensions(string jsonFilePath)
        {
            try
            {
                string jsonContent = File.ReadAllText(jsonFilePath);
                using JsonDocument doc = JsonDocument.Parse(jsonContent);

                int width = doc.RootElement.GetProperty("width").GetInt32();
                int height = doc.RootElement.GetProperty("height").GetInt32();
                

                return (width, height);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void SaveImage_Click(object sender, RoutedEventArgs e)
        {
            if (renderedMapImage == null)
            {
                MessageBox.Show("No image to save. Render a map first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(mapFilePath))
            {
                MessageBox.Show("Map file path is not set.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            int? mapNumber = ExtractMapNumber(mapFilePath);
            if (mapNumber == null)
            {
                MessageBox.Show("Invalid map file format. Could not extract number.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Define the new save directory
            string projectDirectory = AppDomain.CurrentDomain.BaseDirectory; // Gets project execution path
            string saveDirectory = Path.Combine(projectDirectory, "SPF_Maps");

            // Ensure the directory exists
            if (!Directory.Exists(saveDirectory))
            {
                Directory.CreateDirectory(saveDirectory);
            }

            // Define file paths in SPF_Maps directory
            string renderedMapFilePath = Path.Combine(saveDirectory, $"_t{mapNumber}.spf");
            string nameplateFilePath = Path.Combine(saveDirectory, $"_t{mapNumber}n.spf");

            // Step 1: Load and apply the background
            var backgroundImage = LoadEmbeddedBackgroundImage();
            var finalImage = OverlayMapOnBackground(renderedMapImage, backgroundImage);

            // Step 2: Convert final image to SPF
            var spfFile = SpfFile.FromImages(finalImage);
            spfFile.Save(renderedMapFilePath); // Save rendered map as _t{mapNumber}.spf

            // Step 3: Copy and rename nameplate SPF as _t{mapNumber}n.spf
            CopyAndRenameNameplate(nameplateFilePath);

            // Step 4: Append to _tcoord.txt
            AppendToTcoordFile();

            MessageBox.Show($"SPF files saved in {saveDirectory}:\n- {renderedMapFilePath}\n- {nameplateFilePath}\nMap added to _tcoord.txt!", 
                "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        
        private void CopyAndRenameNameplate(string destinationPath)
        {
            string resourcePath = "MapToScroll.Assets._nameplate.spf"; // Ensure this is embedded in your project

            try
            {
                using Stream resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourcePath);
                if (resourceStream == null)
                {
                    throw new FileNotFoundException("Embedded resource not found: " + resourcePath);
                }

                using FileStream fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
                resourceStream.CopyTo(fileStream);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy nameplate file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        
        private void AppendToTcoordFile()
        {
            const string TcoordFilePath = @"C:\Users\Admin\Documents\GitHub\Unora\Custom Client Mods\National.dat\Map GUI\_tcoord.txt"; // Update as needed

            const string MapInstancesRoot =
                @"C:\Users\Admin\Documents\GitHub\Unora\Data\Configuration\MapInstances"; // Root folder of map instances

            int? mapNumber = ExtractMapNumber(mapFilePath);

            if (mapNumber == null)
            {
                MessageBox.Show(
                    "Invalid map file format. Could not extract number.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            string jsonFilePath = Path.Combine(JsonDirectory, $"{mapNumber}.json");

            if (!File.Exists(jsonFilePath))
            {
                MessageBox.Show(
                    $"Matching JSON file not found: {jsonFilePath}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            (int width, int height)? dimensions = LoadMapDimensions(jsonFilePath);

            if (dimensions == null)
            {
                MessageBox.Show(
                    "Failed to read map dimensions from JSON.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            int x = dimensions.Value.width;
            int y = dimensions.Value.height;

            // Step 1: Find the map instance JSON file
            string mapInstancePath = FindInstanceJsonFile(MapInstancesRoot, mapNumber.ToString());

            // Step 2: Extract the actual map name
            string mapName = "Untitled" + mapNumber; // Default if not found

            if (!string.IsNullOrEmpty(mapInstancePath))
            {
                string? extractedName = ExtractMapName(mapInstancePath);

                if (!string.IsNullOrEmpty(extractedName))
                {
                    mapName = extractedName;
                }
            }

            var xCoord = -((BackgroundWidth - renderedMapImage.Width) / 2);
            var yCoord = ((BackgroundHeight - renderedMapImage.Height) / 2);
            
            // Step 3: Construct the new entry
            string newEntry = $"{mapNumber} {mapName} {xCoord},{yCoord} {x} {y}";

            // Ensure file exists
            if (!File.Exists(TcoordFilePath))
            {
                File.WriteAllText(TcoordFilePath, newEntry + Environment.NewLine);
            }
            else
            {
                var lines = File.ReadAllLines(TcoordFilePath);

                // Avoid duplicate entries
                bool exists = lines.Any(line => line.StartsWith($"{mapNumber} "));

                if (!exists)
                {
                    File.AppendAllText(TcoordFilePath, newEntry + Environment.NewLine);
                }
            }
        }

        private void AppendToTcoordFile(int mapNumber, int tileWidth, int tileHeight, int imageWidth, int imageHeight)
        {
            const string TcoordFilePath = @"C:\Users\Admin\Documents\GitHub\Unora\Custom Client Mods\National.dat\Map GUI\_tcoord.txt";

            string mapInstancePath = FindInstanceJsonFile(@"C:\Users\Admin\Documents\GitHub\Unora\Data\Configuration\MapInstances", mapNumber.ToString());

            string mapName = "Untitled" + mapNumber;
            if (!string.IsNullOrEmpty(mapInstancePath))
            {
                string? extractedName = ExtractMapName(mapInstancePath);
                if (!string.IsNullOrEmpty(extractedName))
                {
                    mapName = extractedName;
                }
            }

            var xCoord = -((BackgroundWidth - imageWidth) / 2);
            var yCoord = ((BackgroundHeight - imageHeight) / 2);
            
            string newEntry = $"{mapNumber} {mapName} {xCoord},{yCoord} {tileWidth} {tileHeight}";

            if (!File.Exists(TcoordFilePath))
            {
                File.WriteAllText(TcoordFilePath, newEntry + Environment.NewLine);
            }
            else
            {
                var lines = File.ReadAllLines(TcoordFilePath);
                bool exists = lines.Any(line => line.StartsWith($"{mapNumber} "));

                if (!exists)
                {
                    File.AppendAllText(TcoordFilePath, newEntry + Environment.NewLine);
                }
            }
        }

        private string FindInstanceJsonFile(string rootDirectory, string mapNumber)
        {
            try
            {
                // Search all subdirectories for an instance.json file
                foreach (string file in Directory.EnumerateFiles(rootDirectory, "instance.json", SearchOption.AllDirectories))
                {
                    // Open the file and check if its "templateKey" matches the map number
                    string jsonContent = File.ReadAllText(file);
                    using JsonDocument doc = JsonDocument.Parse(jsonContent);

                    if (doc.RootElement.TryGetProperty("templateKey", out JsonElement templateKeyElement))
                    {
                        if (templateKeyElement.GetString() == mapNumber)
                        {
                            return file; // Found the correct file
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error searching for instance.json: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return string.Empty; // Not found
        }

        private string? ExtractMapName(string instanceJsonPath)
        {
            try
            {
                string jsonContent = File.ReadAllText(instanceJsonPath);
                using JsonDocument doc = JsonDocument.Parse(jsonContent);

                if (doc.RootElement.TryGetProperty("name", out JsonElement nameElement))
                {
                    return nameElement.GetString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading map name: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return null; // If name is missing
        }

        
        private SKImage LoadEmbeddedBackgroundImage()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using Stream stream = assembly.GetManifestResourceStream(BackgroundImageResource);
            if (stream == null)
            {
                throw new FileNotFoundException("Embedded background image not found.");
            }

            using var skBitmap = SKBitmap.Decode(stream);
            return SKImage.FromBitmap(skBitmap);
        }

        private SKImage OverlayMapOnBackground(SKImage mapImage, SKImage backgroundImage)
        {
            int targetWidth = backgroundImage.Width;
            int targetHeight = backgroundImage.Height;

            var croppedMap = CropTopEmptySpace(mapImage);
            var resizedMap = ResizeImage(croppedMap, targetWidth, targetHeight);

            using var surface = SKSurface.Create(new SKImageInfo(targetWidth, targetHeight));
            var canvas = surface.Canvas;

            canvas.DrawImage(backgroundImage, 0, 0);
            int xOffset = (targetWidth - resizedMap.Width) / 2;
            int yOffset = (targetHeight - resizedMap.Height) / 2;
            canvas.DrawImage(resizedMap, xOffset, yOffset);

            return surface.Snapshot();
        }

        private SKImage CropTopEmptySpace(SKImage image)
        {
            using var bitmap = SKBitmap.FromImage(image);
            int width = bitmap.Width;
            int height = bitmap.Height;

            int cropStartY = 0;
            SKColor backgroundColor = bitmap.GetPixel(width / 2, 0);

            for (int y = 0; y < height; y++)
            {
                bool hasContent = false;
                for (int x = 0; x < width; x++)
                {
                    SKColor pixel = bitmap.GetPixel(x, y);
                    if (pixel.Alpha > 0 && pixel != backgroundColor)
                    {
                        hasContent = true;
                        break;
                    }
                }
                if (hasContent)
                {
                    cropStartY = y;
                    break;
                }
            }

            int newHeight = height - cropStartY;
            using var croppedBitmap = new SKBitmap(width, newHeight);
            using var canvas = new SKCanvas(croppedBitmap);
            canvas.DrawBitmap(bitmap, new SKRect(0, cropStartY, width, height), new SKRect(0, 0, width, newHeight));

            return SKImage.FromBitmap(croppedBitmap);
        }

        private SKImage ResizeImage(SKImage image, int maxWidth, int maxHeight)
        {
            using var bitmap = SKBitmap.FromImage(image);

            float aspectRatio = (float)bitmap.Width / bitmap.Height;
            int newWidth = maxWidth;
            int newHeight = (int)(maxWidth / aspectRatio);

            if (newHeight > maxHeight)
            {
                newHeight = maxHeight;
                newWidth = (int)(maxHeight * aspectRatio);
            }

            using var resizedBitmap = new SKBitmap(newWidth, newHeight);
            using var canvas = new SKCanvas(resizedBitmap);
            var paint = new SKPaint
            {
                FilterQuality = SKFilterQuality.None,
                IsAntialias = false
            };

            canvas.DrawBitmap(bitmap, new SKRect(0, 0, bitmap.Width, bitmap.Height), new SKRect(0, 0, newWidth, newHeight), paint);

            return SKImage.FromBitmap(resizedBitmap);
        }

        private void SaveSkImageToFile(SKImage skImage, string filePath)
        {
            using var skBitmap = SKBitmap.FromImage(skImage);
            using var imageStream = File.OpenWrite(filePath);
            skBitmap.Encode(imageStream, SKEncodedImageFormat.Png, 100);
        }
        
        private BitmapSource ConvertSkImageToBitmapSource(SKImage skImage)
        {
            using var skBitmap = SKBitmap.FromImage(skImage);
            using var imageStream = new MemoryStream();

            skBitmap.Encode(imageStream, SKEncodedImageFormat.Png, 100);
            imageStream.Seek(0, SeekOrigin.Begin);

            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.StreamSource = imageStream;
            bitmapImage.EndInit();

            return bitmapImage;
        }

    }
}