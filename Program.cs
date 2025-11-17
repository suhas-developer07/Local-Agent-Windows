using System.Drawing;
using System.Drawing.Printing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Docnet.Core;
using Docnet.Core.Models;

// Build configuration
var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<RedisService>();
        services.AddHostedService<PrintWorkerService>();
    })
    .Build();

await host.RunAsync();

// ============== REDIS SERVICE ==============
public class RedisService
{
    private readonly IDatabase _db;
    private readonly IConfiguration _config;
    private readonly string _queueName;

    public RedisService(IConfiguration config)
    {
        _config = config;
        _queueName = config["Redis:QueueName"] ?? "print_jobs";

        var redisConfig = new ConfigurationOptions
        {
            EndPoints = { $"{config["Redis:Host"]}:{config["Redis:Port"]}" },
            User = config["Redis:Username"],
            Password = config["Redis:Password"],
            Ssl = config.GetValue<bool>("Redis:UseTLS"),
            AbortOnConnectFail = false,
            ConnectRetry = 5,
            ConnectTimeout = 10000,
            SyncTimeout = 5000
        };

        var redis = ConnectionMultiplexer.Connect(redisConfig);
        _db = redis.GetDatabase();

        Console.WriteLine("✅ Connected to Redis Cloud");
    }

    public async Task<PrintJob?> GetNextJob()
    {
        try
        {
            var jobJson = await _db.ListLeftPopAsync(_queueName);
            if (jobJson.IsNullOrEmpty) return null;

            var job = JsonSerializer.Deserialize<PrintJob>(jobJson!);
            return job;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Redis error: {ex.Message}");
            return null;
        }
    }

    public async Task RequeueJob(PrintJob job)
    {
        var jobJson = JsonSerializer.Serialize(job);
        await _db.ListRightPushAsync(_queueName, jobJson);
        Console.WriteLine("🔄 Job requeued for retry");
    }
}

// ============== WORKER SERVICE ==============
public class PrintWorkerService : BackgroundService
{
    private readonly RedisService _redis;
    private readonly IConfiguration _config;
    private readonly PrintService _printService;

    public PrintWorkerService(RedisService redis, IConfiguration config)
    {
        _redis = redis;
        _config = config;
        _printService = new PrintService(config);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("🚀 Print Agent Started - Waiting for jobs from Redis...");
        Console.WriteLine($"📋 Queue: {_config["Redis:QueueName"]}");
        Console.WriteLine($"🖨️  Color Printer: {_config["Printers:ColorPrinter"]}");
        Console.WriteLine($"🖨️  B&W Printer: {_config["Printers:BlackAndWhitePrinter"]}\n");

        var pollInterval = _config.GetValue<int>("PrintAgent:PollIntervalMs");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await _redis.GetNextJob();

                if (job == null)
                {
                    await Task.Delay(pollInterval, stoppingToken);
                    continue;
                }

                Console.WriteLine($"\n📥 New Job Received: {job.JobId}");
                await ProcessJob(job);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Worker error: {ex.Message}");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ProcessJob(PrintJob job)
    {
        var maxRetries = _config.GetValue<int>("PrintAgent:MaxRetries");
        var retryDelay = _config.GetValue<int>("PrintAgent:RetryDelayMs");

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Console.WriteLine($"🔄 Attempt {attempt}/{maxRetries}");

                // Download file
                var filePath = await DownloadFile(job.FileUrl);
                Console.WriteLine($"✅ File downloaded: {filePath}");

                // Print file
                await _printService.PrintFile(filePath, job.Options);
                Console.WriteLine($"✅ Job {job.JobId} completed successfully\n");

                // Cleanup
                CleanupFile(filePath);
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Attempt {attempt} failed: {ex.Message}");

                if (attempt < maxRetries)
                {
                    Console.WriteLine($"⏳ Retrying in {retryDelay}ms...");
                    await Task.Delay(retryDelay);
                }
                else
                {
                    Console.WriteLine($"💀 Job {job.JobId} failed after {maxRetries} attempts\n");
                    // Optionally requeue or log to dead letter queue
                }
            }
        }
    }

    private async Task<string> DownloadFile(string url)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"print-{Guid.NewGuid()}.pdf");

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(5);

        var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        await using var fs = new FileStream(tempPath, FileMode.Create);
        await response.Content.CopyToAsync(fs);

        return tempPath;
    }

    private void CleanupFile(string filePath)
    {
        var cleanupDelay = _config.GetValue<int>("PrintAgent:TempFileCleanupDelayMs");
        _ = Task.Run(async () =>
        {
            await Task.Delay(cleanupDelay);
            try
            {
                File.Delete(filePath);
                Console.WriteLine($"🗑️  Temp file cleaned: {Path.GetFileName(filePath)}");
            }
            catch { }
        });
    }
}

// ============== PRINT SERVICE ==============
public class PrintService
{
    private readonly IConfiguration _config;
    private readonly string _colorPrinter;
    private readonly string _bwPrinter;

    public PrintService(IConfiguration config)
    {
        _config = config;
        _colorPrinter = config["Printers:ColorPrinter"] ?? "EPSON EM-C800";
        _bwPrinter = config["Printers:BlackAndWhitePrinter"] ?? "KYOCERA";
    }

    public async Task PrintFile(string filePath, PrintOptions options)
    {
        await Task.Run(() =>
        {
            var extension = Path.GetExtension(filePath).ToLower();

            Console.WriteLine($"📄 File Type: {extension}");
            Console.WriteLine($"🖨️  Printer: {GetPrinterName(options)}");
            Console.WriteLine($"📋 Copies: {options.Copies}");
            Console.WriteLine($"🔄 Duplex: {options.Duplex}");
            Console.WriteLine($"🎨 Color: {options.ColorMode}");
            Console.WriteLine($"📃 Pages: {options.PageRange}");

            switch (extension)
            {
                case ".pdf":
                    PrintPdf(filePath, options);
                    break;
                case ".txt":
                    PrintTextFile(filePath, options);
                    break;
                case ".jpg":
                case ".jpeg":
                case ".png":
                case ".bmp":
                case ".gif":
                    PrintImage(filePath, options);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported file type: {extension}");
            }
        });
    }

    private string GetPrinterName(PrintOptions options)
    {
        return options.ColorMode?.ToLower() == "color" ? _colorPrinter : _bwPrinter;
    }

    private void PrintPdf(string pdfPath, PrintOptions options)
    {
        var bitmaps = new List<Bitmap>();

        try
        {
            using var library = DocLib.Instance;
            using var docReader = library.GetDocReader(pdfPath, new PageDimensions(2400, 2400));

            var pageCount = docReader.GetPageCount();
            var pagesToPrint = ParsePageRange(options.PageRange, pageCount);

            Console.WriteLine($"📖 Rendering {pagesToPrint.Count}/{pageCount} pages...");

            foreach (var pageNumber in pagesToPrint)
            {
                using var pageReader = docReader.GetPageReader(pageNumber);
                var rawBytes = pageReader.GetImage();
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();

                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var bitmapData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);

                try
                {
                    Marshal.Copy(rawBytes, 0, bitmapData.Scan0, rawBytes.Length);
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }

                bitmaps.Add(bitmap);
            }

            PrintBitmaps(bitmaps, options, pagesToPrint);
        }
        finally
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }
    }

    private void PrintTextFile(string textPath, PrintOptions options)
    {
        var lines = File.ReadAllLines(textPath);
        var lineIndex = 0;

        using var printDoc = new PrintDocument();
        ApplyPrinterSettings(printDoc, options);

        printDoc.PrintPage += (sender, e) =>
        {
            if (e?.Graphics == null) return;

            var font = new Font("Courier New", 10);
            var yPos = (float)e.MarginBounds.Top;
            var lineHeight = font.GetHeight(e.Graphics);
            var linesPerPage = (int)(e.MarginBounds.Height / lineHeight);

            while (lineIndex < lines.Length && linesPerPage > 0)
            {
                e.Graphics.DrawString(lines[lineIndex], font, Brushes.Black, e.MarginBounds.Left, yPos);
                lineIndex++;
                yPos += lineHeight;
                linesPerPage--;
            }

            e.HasMorePages = lineIndex < lines.Length;
        };

        printDoc.Print();
    }

    private void PrintImage(string imagePath, PrintOptions options)
    {
        using var image = Image.FromFile(imagePath);
        using var printDoc = new PrintDocument();

        ApplyPrinterSettings(printDoc, options);

        printDoc.PrintPage += (sender, e) =>
        {
            if (e?.Graphics == null) return;
            var imgRect = GetScaledImageRectangle(image, e.PageBounds);
            e.Graphics.DrawImage(image, imgRect);
            e.HasMorePages = false;
        };

        printDoc.Print();
    }

    private void PrintBitmaps(List<Bitmap> bitmaps, PrintOptions options, List<int> pageNumbers)
    {
        using var printDoc = new PrintDocument();
        ApplyPrinterSettings(printDoc, options);

        var pageIndex = 0;

        printDoc.PrintPage += (sender, e) =>
        {
            if (e?.Graphics == null) return;

            if (pageIndex < bitmaps.Count)
            {
                var bitmap = bitmaps[pageIndex];
                var imgRect = GetScaledImageRectangle(bitmap, e.PageBounds);
                e.Graphics.DrawImage(bitmap, imgRect);

                Console.WriteLine($"  ✓ Printed page {pageNumbers[pageIndex] + 1}");
                pageIndex++;
                e.HasMorePages = pageIndex < bitmaps.Count;
            }
            else
            {
                e.HasMorePages = false;
            }
        };

        printDoc.Print();
    }

    private void ApplyPrinterSettings(PrintDocument printDoc, PrintOptions options)
    {
        var printerName = GetPrinterName(options);
        printDoc.PrinterSettings.PrinterName = printerName;

        if (!printDoc.PrinterSettings.IsValid)
        {
            throw new Exception($"Printer not found: {printerName}");
        }

        IntPtr hPrinter = IntPtr.Zero;
        if (!Win32.OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
        {
            Console.WriteLine("⚠️  Using basic settings (Win32 API unavailable)");
            ApplyBasicSettings(printDoc, options);
            return;
        }

        try
        {
            int sizeNeeded = Win32.DocumentProperties(IntPtr.Zero, hPrinter, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            IntPtr pDevMode = Marshal.AllocHGlobal(sizeNeeded);

            try
            {
                Win32.DocumentProperties(IntPtr.Zero, hPrinter, printerName, pDevMode, IntPtr.Zero, Win32.DM_OUT_BUFFER);
                var devMode = Marshal.PtrToStructure<Win32.DEVMODE>(pDevMode);

                devMode.dmCopies = (short)options.Copies;
                devMode.dmFields |= Win32.DM_COPIES;

                devMode.dmDuplex = options.Duplex?.ToLower() switch
                {
                    "vertical" or "double" => Win32.DMDUP_VERTICAL,
                    "horizontal" => Win32.DMDUP_HORIZONTAL,
                    _ => Win32.DMDUP_SIMPLEX
                };
                devMode.dmFields |= Win32.DM_DUPLEX;

                devMode.dmColor = options.ColorMode?.ToLower() == "color" ? Win32.DMCOLOR_COLOR : Win32.DMCOLOR_MONOCHROME;
                devMode.dmFields |= Win32.DM_COLOR;

                devMode.dmOrientation = options.Orientation?.ToLower() == "landscape" ? Win32.DMORIENT_LANDSCAPE : Win32.DMORIENT_PORTRAIT;
                devMode.dmFields |= Win32.DM_ORIENTATION;

                Marshal.StructureToPtr(devMode, pDevMode, true);
                Win32.DocumentProperties(IntPtr.Zero, hPrinter, printerName, pDevMode, pDevMode, Win32.DM_IN_BUFFER | Win32.DM_OUT_BUFFER);

                printDoc.PrinterSettings.SetHdevmode(pDevMode);
                printDoc.DefaultPageSettings.SetHdevmode(pDevMode);

                Console.WriteLine($"  ✓ Settings applied via Win32 API");
            }
            finally
            {
                Marshal.FreeHGlobal(pDevMode);
            }
        }
        finally
        {
            Win32.ClosePrinter(hPrinter);
        }
    }

    private void ApplyBasicSettings(PrintDocument printDoc, PrintOptions options)
    {
        printDoc.PrinterSettings.Copies = (short)options.Copies;
        printDoc.DefaultPageSettings.Color = options.ColorMode?.ToLower() == "color";

        printDoc.PrinterSettings.Duplex = options.Duplex?.ToLower() switch
        {
            "vertical" or "double" => Duplex.Vertical,
            "horizontal" => Duplex.Horizontal,
            _ => Duplex.Simplex
        };

        printDoc.DefaultPageSettings.Landscape = options.Orientation?.ToLower() == "landscape";
    }

    private Rectangle GetScaledImageRectangle(Image image, Rectangle pageRect)
    {
        var scale = Math.Min((float)pageRect.Width / image.Width, (float)pageRect.Height / image.Height);
        var newWidth = (int)(image.Width * scale);
        var newHeight = (int)(image.Height * scale);
        var x = pageRect.X + (pageRect.Width - newWidth) / 2;
        var y = pageRect.Y + (pageRect.Height - newHeight) / 2;
        return new Rectangle(x, y, newWidth, newHeight);
    }

    private List<int> ParsePageRange(string? pageRange, int totalPages)
    {
        var pages = new List<int>();

        if (string.IsNullOrEmpty(pageRange) || pageRange.ToLower() == "all")
        {
            for (int i = 0; i < totalPages; i++) pages.Add(i);
            return pages;
        }

        foreach (var range in pageRange.Split(','))
        {
            if (range.Contains('-'))
            {
                var parts = range.Split('-');
                var start = int.Parse(parts[0].Trim()) - 1;
                var end = int.Parse(parts[1].Trim()) - 1;
                for (int i = start; i <= end && i < totalPages; i++) pages.Add(i);
            }
            else
            {
                var page = int.Parse(range.Trim()) - 1;
                if (page < totalPages) pages.Add(page);
            }
        }

        return pages.Distinct().OrderBy(p => p).ToList();
    }
}

// ============== MODELS ==============
public class PrintJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString();
    public string FileUrl { get; set; } = string.Empty;
    public PrintOptions Options { get; set; } = new();
}

public class PrintOptions
{
    public int Copies { get; set; } = 1;
    public string? ColorMode { get; set; } = "bw";
    public string? Duplex { get; set; } = "simplex";
    public string? PaperSize { get; set; } = "A4";
    public string? PageRange { get; set; } = "all";
    public string? Orientation { get; set; } = "portrait";
}

// ============== WIN32 API ==============
static class Win32
{
    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int DocumentProperties(IntPtr hwnd, IntPtr hPrinter, string pDeviceName,
        IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

    public const int DM_OUT_BUFFER = 2;
    public const int DM_IN_BUFFER = 8;
    public const int DM_COPIES = 0x00000100;
    public const int DM_DUPLEX = 0x00001000;
    public const int DM_COLOR = 0x00000800;
    public const int DM_ORIENTATION = 0x00000001;

    public const short DMDUP_SIMPLEX = 1;
    public const short DMDUP_VERTICAL = 2;
    public const short DMDUP_HORIZONTAL = 3;

    public const short DMCOLOR_MONOCHROME = 1;
    public const short DMCOLOR_COLOR = 2;

    public const short DMORIENT_PORTRAIT = 1;
    public const short DMORIENT_LANDSCAPE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public short dmOrientation;
        public short dmPaperSize;
        public short dmPaperLength;
        public short dmPaperWidth;
        public short dmScale;
        public short dmCopies;
        public short dmDefaultSource;
        public short dmPrintQuality;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}