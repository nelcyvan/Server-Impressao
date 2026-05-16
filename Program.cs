using System.Management;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Diagnostics;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Windows.Forms;

sealed class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var hasUrlsArg = args.Any(a => string.Equals(a, "--urls", StringComparison.OrdinalIgnoreCase));
        if (!hasUrlsArg)
        {
            var serverConfig = ServerConfigStore.Load();
            if (serverConfig is not null)
            {
                builder.WebHost.UseUrls(serverConfig.ToUrl());
            }
        }

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
                policy
                    .SetIsOriginAllowed(_ => true)
                    .AllowAnyHeader()
                    .AllowAnyMethod());
        });

        builder.Services.AddSingleton<PrintLogStore>();

        var app = builder.Build();

        var apiKey = builder.Configuration["PrintServer:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    var header = context.Request.Headers.TryGetValue("X-Api-Key", out var value) ? value.ToString() : null;
                    if (!string.Equals(header, apiKey, StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                        return;
                    }
                }

                await next();
            });
        }

        app.UseCors();

        app.MapGet("/", () =>
            Results.Ok(new
            {
                name = "PrintServer",
                endpoints = AppMetadata.RootEndpoints
            }));

        app.MapGet("/ui", () => Results.Text(UiPages.Index, "text/html; charset=utf-8"));

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/api/printers", () =>
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, Default FROM Win32_Printer");

            var printers = searcher
                .Get()
                .Cast<ManagementBaseObject>()
                .Select(printer => new
                {
                    name = printer["Name"]?.ToString(),
                    isDefault = printer["Default"] is bool b && b
                })
                .Where(p => !string.IsNullOrWhiteSpace(p.name))
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Ok(printers);
        });

        app.MapGet("/api/printers/status", () =>
        {
            using var searcher = new ManagementObjectSearcher("""
                SELECT Name, Default, WorkOffline, PrinterStatus, Status, ExtendedPrinterStatus, DetectedErrorState
                FROM Win32_Printer
                """);

            var printers = searcher
                .Get()
                .Cast<ManagementBaseObject>()
                .Select(printer => new PrinterStatusDto(
                    Name: printer["Name"]?.ToString() ?? "",
                    IsDefault: printer["Default"] is bool b && b,
                    WorkOffline: printer["WorkOffline"] is bool w && w,
                    PrinterStatus: printer["PrinterStatus"] is ushort ps ? ps : null,
                    Status: printer["Status"]?.ToString(),
                    ExtendedPrinterStatus: printer["ExtendedPrinterStatus"] is uint eps ? eps : null,
                    DetectedErrorState: printer["DetectedErrorState"] is uint des ? des : null))
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Ok(printers);
        });

        app.MapGet("/api/jobs/active", () =>
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PrintJob");

            var jobs = searcher
                .Get()
                .Cast<ManagementBaseObject>()
                .Select(job =>
                {
                    var name = job["Name"]?.ToString() ?? "";
                    var (printerName, jobId) = PrintJobNameParser.TryParse(name);

                    DateTimeOffset? timeSubmitted = null;
                    var timeSubmittedRaw = job["TimeSubmitted"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(timeSubmittedRaw))
                    {
                        try
                        {
                            var dt = ManagementDateTimeConverter.ToDateTime(timeSubmittedRaw);
                            timeSubmitted = new DateTimeOffset(dt);
                        }
                        catch
                        {
                        }
                    }

                    return new PrintJobDto(
                        PrinterName: printerName,
                        JobId: jobId,
                        Document: job["Document"]?.ToString(),
                        Owner: job["Owner"]?.ToString(),
                        Status: job["Status"]?.ToString(),
                        JobStatus: job["JobStatus"]?.ToString(),
                        TotalPages: job["TotalPages"] is uint tp ? tp : null,
                        PagesPrinted: job["PagesPrinted"] is uint pp ? pp : null,
                        SizeBytes: job["Size"] is uint s ? s : null,
                        TimeSubmitted: timeSubmitted);
                })
                .OrderByDescending(j => j.TimeSubmitted)
                .ThenBy(j => j.PrinterName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Ok(jobs);
        });

        app.MapGet("/api/logs", (int? take, PrintLogStore logStore) =>
        {
            var boundedTake = Math.Clamp(take ?? 200, 1, 5000);
            return Results.Ok(logStore.GetLatest(boundedTake));
        });

app.MapGet("/api/config", () =>
{
    var config = ServerConfigStore.Load() ?? new ServerConfigDto(ListenAll: true, Host: "localhost", Port: 5000);
    var effectiveUrls = app.Urls.ToArray();

    var (scheme, port) = NetworkInfo.GetPrimarySchemeAndPort(effectiveUrls, fallbackPort: config.Port);
    var localUrl = NetworkInfo.BuildLocalUrl(scheme, port);
    var lanUrls = NetworkInfo.BuildLanUrls(scheme, port);

    return Results.Ok(new
    {
        config = new { listenAll = config.ListenAll, host = config.Host, port = config.Port },
        effectiveUrls,
        localUrl,
        lanUrls
    });
});

app.MapPost("/api/config", (ServerConfigUpdateRequest body) =>
{
    var next = ServerConfigStore.Normalize(body);
    if (next is null)
    {
        return Results.BadRequest(new { error = "invalid_config" });
    }

    ServerConfigStore.Save(next);
    return Results.Ok(new { status = "saved", url = next.ToUrl(), restartRequired = true });
});

        app.MapDelete("/api/logs", async (PrintLogStore logStore) =>
        {
            await logStore.ClearAsync();
            return Results.Ok(new { status = "cleared" });
        });

        app.MapPost("/api/print/raw", async (HttpRequest request, PrintLogStore logStore, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PrintServer");

            var body = await request.ReadFromJsonAsync<RawPrintRequest>();
            if (body is null)
            {
                return Results.BadRequest(new { error = "invalid_body" });
            }

            if (string.IsNullOrWhiteSpace(body.PrinterName))
            {
                return Results.BadRequest(new { error = "printerName_required" });
            }

            if (!PrinterDiscovery.Exists(body.PrinterName))
            {
                return Results.NotFound(new { error = "printer_not_found" });
            }

            if (string.IsNullOrWhiteSpace(body.DataBase64))
            {
                return Results.BadRequest(new { error = "dataBase64_required" });
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(body.DataBase64);
            }
            catch
            {
                return Results.BadRequest(new { error = "dataBase64_invalid" });
            }

            var ok = RawPrinter.SendBytesToPrinter(body.PrinterName, bytes, body.DocumentName ?? "raw-print");
            var remoteIp = request.HttpContext.Connection.RemoteIpAddress?.ToString();
            var entry = new PrintLogEntry(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow,
                Kind: "raw",
                PrinterName: body.PrinterName,
                DocumentName: body.DocumentName ?? "raw-print",
                BytesLength: bytes.Length,
                Status: ok ? "queued" : "failed",
                Error: ok ? null : "print_failed",
                RemoteIp: remoteIp);

            await logStore.AppendAsync(entry);
            if (ok)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Queued RAW print: printer={PrinterName} doc={DocumentName} bytes={BytesLength} remoteIp={RemoteIp}", entry.PrinterName, entry.DocumentName, entry.BytesLength, entry.RemoteIp);
                }
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("RAW print failed: printer={PrinterName} doc={DocumentName} bytes={BytesLength} remoteIp={RemoteIp}", entry.PrinterName, entry.DocumentName, entry.BytesLength, entry.RemoteIp);
                }
            }

            return ok
                ? Results.Ok(new { status = "queued" })
                : Results.Problem(title: "print_failed", statusCode: StatusCodes.Status500InternalServerError);
        });

        app.MapPost("/api/print/tspl", async (HttpRequest request, PrintLogStore logStore, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("PrintServer");

            var body = await request.ReadFromJsonAsync<TsplPrintRequest>();
            if (body is null)
            {
                return Results.BadRequest(new { error = "invalid_body" });
            }

            if (string.IsNullOrWhiteSpace(body.PrinterName))
            {
                return Results.BadRequest(new { error = "printerName_required" });
            }

            if (!PrinterDiscovery.Exists(body.PrinterName))
            {
                return Results.NotFound(new { error = "printer_not_found" });
            }

            if (string.IsNullOrWhiteSpace(body.Tspl))
            {
                return Results.BadRequest(new { error = "tspl_required" });
            }

            var encoding = body.Encoding?.Trim().ToLowerInvariant() switch
            {
                null or "" or "ascii" => Encoding.ASCII,
                "utf8" or "utf-8" => Encoding.UTF8,
                _ => Encoding.ASCII
            };

            var payload = body.Tspl;
            if (!payload.EndsWith('\n'))
            {
                payload += "\r\n";
            }

            var bytes = encoding.GetBytes(payload);
            var ok = RawPrinter.SendBytesToPrinter(body.PrinterName, bytes, body.DocumentName ?? "tspl");

            var remoteIp = request.HttpContext.Connection.RemoteIpAddress?.ToString();
            var entry = new PrintLogEntry(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow,
                Kind: "tspl",
                PrinterName: body.PrinterName,
                DocumentName: body.DocumentName ?? "tspl",
                BytesLength: bytes.Length,
                Status: ok ? "queued" : "failed",
                Error: ok ? null : "print_failed",
                RemoteIp: remoteIp);

            await logStore.AppendAsync(entry);
            if (ok)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Queued TSPL print: printer={PrinterName} doc={DocumentName} bytes={BytesLength} remoteIp={RemoteIp}", entry.PrinterName, entry.DocumentName, entry.BytesLength, entry.RemoteIp);
                }
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("TSPL print failed: printer={PrinterName} doc={DocumentName} bytes={BytesLength} remoteIp={RemoteIp}", entry.PrinterName, entry.DocumentName, entry.BytesLength, entry.RemoteIp);
                }
            }

            return ok
                ? Results.Ok(new { status = "queued" })
                : Results.Problem(title: "print_failed", statusCode: StatusCodes.Status500InternalServerError);
        });

        app.MapPost("/api/print/image", async (HttpRequest request, PrintLogStore logStore, ILoggerFactory loggerFactory, IConfiguration configuration) =>
        {
            var logger = loggerFactory.CreateLogger("PrintServer");

            var body = await request.ReadFromJsonAsync<ImagePrintRequest>();
            if (body is null)
            {
                return Results.BadRequest(new { error = "invalid_body" });
            }

            if (string.IsNullOrWhiteSpace(body.PrinterName))
            {
                return Results.BadRequest(new { error = "printerName_required" });
            }

            if (!PrinterDiscovery.Exists(body.PrinterName))
            {
                return Results.NotFound(new { error = "printer_not_found" });
            }

            if (!string.Equals(body.MimeType?.Trim(), "image/png", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "mimeType_invalid" });
            }

            if (string.IsNullOrWhiteSpace(body.ImageBase64))
            {
                return Results.BadRequest(new { error = "imageBase64_required" });
            }

            if (body.Label is null)
            {
                return Results.BadRequest(new { error = "label_required" });
            }

            if (body.Label.WidthMm <= 0 || body.Label.HeightMm <= 0 || body.Label.Dpi <= 0 || body.Label.WidthPx <= 0 || body.Label.HeightPx <= 0)
            {
                return Results.BadRequest(new { error = "label_invalid" });
            }

            var ratioMm = body.Label.WidthMm / body.Label.HeightMm;
            var ratioPx = (double)body.Label.WidthPx / body.Label.HeightPx;
            var ratioDiff = Math.Abs(ratioPx - ratioMm) / ratioMm;
            if (ratioDiff > 0.01)
            {
                return Results.BadRequest(new { error = "label_aspect_ratio_mismatch" });
            }

            var maxImageBytes = Math.Clamp(configuration.GetValue("PrintServer:MaxImageBytes", 8_000_000), 50_000, 50_000_000);
            var maxBase64Chars = (int)Math.Ceiling(maxImageBytes / 3d) * 4 + 16;
            if (body.ImageBase64.Length > maxBase64Chars)
            {
                return Results.BadRequest(new { error = "payload_too_large" });
            }

            byte[] pngBytes;
            try
            {
                pngBytes = Convert.FromBase64String(body.ImageBase64);
            }
            catch
            {
                return Results.BadRequest(new { error = "imageBase64_invalid" });
            }

            if (pngBytes.Length > maxImageBytes)
            {
                return Results.BadRequest(new { error = "payload_too_large" });
            }

            if (!PngValidation.LooksLikePng(pngBytes))
            {
                return Results.BadRequest(new { error = "png_invalid" });
            }

            int actualWidthPx;
            int actualHeightPx;
            try
            {
                using var ms = new MemoryStream(pngBytes, writable: false);
                using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
                if (!img.RawFormat.Equals(ImageFormat.Png))
                {
                    return Results.BadRequest(new { error = "png_invalid" });
                }

                actualWidthPx = img.Width;
                actualHeightPx = img.Height;
            }
            catch
            {
                return Results.BadRequest(new { error = "png_invalid" });
            }

            if (actualWidthPx != body.Label.WidthPx || actualHeightPx != body.Label.HeightPx)
            {
                return Results.BadRequest(new { error = "label_pixels_mismatch" });
            }

            var documentName = string.IsNullOrWhiteSpace(body.DocumentName) ? "image" : body.DocumentName.Trim();
            bool ok;
            try
            {
                ok = BitmapPrinter.PrintPngToPrinter(body.PrinterName, pngBytes, body.Label.WidthMm, body.Label.HeightMm, documentName);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning(ex, "Bitmap print failed: printer={PrinterName} doc={DocumentName}", body.PrinterName, documentName);
                }
                ok = false;
            }

            var remoteIp = request.HttpContext.Connection.RemoteIpAddress?.ToString();
            var entry = new PrintLogEntry(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow,
                Kind: "image",
                PrinterName: body.PrinterName,
                DocumentName: documentName,
                BytesLength: pngBytes.Length,
                Status: ok ? "queued" : "failed",
                Error: ok ? null : "print_failed",
                RemoteIp: remoteIp);

            await logStore.AppendAsync(entry);
            if (ok)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Queued image print: printer={PrinterName} doc={DocumentName} bytes={BytesLength} remoteIp={RemoteIp}", entry.PrinterName, entry.DocumentName, entry.BytesLength, entry.RemoteIp);
                }
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("Image print failed: printer={PrinterName} doc={DocumentName} bytes={BytesLength} remoteIp={RemoteIp}", entry.PrinterName, entry.DocumentName, entry.BytesLength, entry.RemoteIp);
                }
            }

            return ok
                ? Results.Ok(new { status = "queued" })
                : Results.Problem(title: "print_failed", statusCode: StatusCodes.Status500InternalServerError);
        });

        var runConsole = args.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase));
        var silent = args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
        if (Environment.UserInteractive && !runConsole)
        {
            await RunInTrayAsync(app, silent);
            return;
        }

        await app.RunAsync();
    }

    static async Task RunInTrayAsync(WebApplication app, bool silent)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var restartRequested = false;
        Icon? trayIcon = null;
        app.MapPost("/api/restart", () =>
        {
            if (!Environment.UserInteractive)
            {
                return Results.BadRequest(new { error = "not_interactive" });
            }

            restartRequested = true;
            Application.Exit();
            return Results.Ok(new { status = "restarting" });
        });

        await app.StartAsync();

        var (scheme, port) = NetworkInfo.GetPrimarySchemeAndPort(app.Urls.ToArray(), fallbackPort: 5000);
        var localUrl = NetworkInfo.BuildLocalUrl(scheme, port);
        var lanUrls = NetworkInfo.BuildLanUrls(scheme, port);
        var lanUrl = lanUrls.FirstOrDefault();
        var preferredUrl = lanUrl ?? localUrl;

        var uiUrl = $"{preferredUrl.TrimEnd('/')}/ui";
        var configUrl = $"{uiUrl}?tab=config";
        var autoStart = AutoStart.IsEnabled();

        using var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir UI", null, (_, _) => OpenUrl(uiUrl));
        menu.Items.Add("Configuração", null, (_, _) => OpenUrl(configUrl));
        menu.Items.Add("Copiar URL", null, (_, _) => Clipboard.SetText(preferredUrl));
        menu.Items.Add("Copiar URL (LAN)", null, (_, _) =>
        {
            Clipboard.SetText(lanUrl ?? preferredUrl);
        });
        menu.Items.Add("Copiar URL (Local)", null, (_, _) => Clipboard.SetText(localUrl));
        var autoStartItem = new ToolStripMenuItem("Iniciar com o Windows")
        {
            Checked = autoStart,
            CheckOnClick = false
        };
        autoStartItem.Click += (_, _) =>
        {
            var enabled = AutoStart.IsEnabled();
            var next = !enabled;
            AutoStart.SetEnabled(next);
            autoStartItem.Checked = next;
        };
        menu.Items.Add(autoStartItem);
        menu.Items.Add("Reiniciar", null, (_, _) =>
        {
            restartRequested = true;
            Application.Exit();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Application.Exit());

        trayIcon = TryCreateTrayIcon() ?? SystemIcons.Application;
        using var tray = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "PrintServer",
            Visible = true,
            ContextMenuStrip = menu
        };

        tray.DoubleClick += (_, _) => OpenUrl(uiUrl);
        if (!silent)
        {
            var shownUrl = preferredUrl;
            tray.ShowBalloonTip(2000, "PrintServer", $"Rodando em {shownUrl}", ToolTipIcon.Info);
        }

        try
        {
            Application.Run();
        }
        finally
        {
            tray.Visible = false;
            await app.StopAsync();
            await app.DisposeAsync();
            trayIcon?.Dispose();

            if (restartRequested)
            {
                RestartSelf();
            }
        }
    }

    static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    static void RestartSelf()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            exePath = Application.ExecutablePath;
        }

        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var argText = string.Join(" ", args.Select(QuoteArg));

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = argText,
                UseShellExecute = false
            });
        }
        catch
        {
        }
    }

    static string QuoteArg(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        var mustQuote = arg.Any(char.IsWhiteSpace) || arg.Contains('"');
        if (!mustQuote)
        {
            return arg;
        }

        var escaped = arg.Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    static Icon? TryCreateTrayIcon()
    {
        var fromLogo = TryCreateTrayIconFromEmbeddedLogo();
        if (fromLogo is not null)
        {
            return fromLogo;
        }

        try
        {
            using var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var bg = new SolidBrush(Color.FromArgb(255, 36, 108, 224)))
            {
                g.FillEllipse(bg, 2, 2, 60, 60);
            }

            using (var white = new SolidBrush(Color.White))
            using (var font = new Font("Segoe UI", 28, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString("P", font, white, new RectangleF(0, -2, 64, 64), sf);
            }

            var hIcon = bmp.GetHicon();
            try
            {
                using var tmp = Icon.FromHandle(hIcon);
                return (Icon)tmp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    static Icon? TryCreateTrayIconFromEmbeddedLogo()
    {
        Stream? stream;
        try
        {
            stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PrintServer.TrayIcon.png");
        }
        catch
        {
            stream = null;
        }

        if (stream is null)
        {
            return null;
        }

        try
        {
            using var _ = stream;
            using var src = new Bitmap(stream);

            using var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(src, new Rectangle(0, 0, 64, 64));

            var hIcon = bmp.GetHicon();
            try
            {
                using var tmp = Icon.FromHandle(hIcon);
                return (Icon)tmp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr hIcon);
}

static class AutoStart
{
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "PrintServer";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(ValueName)?.ToString();
        return !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            exePath = Application.ExecutablePath;
        }

        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        var command = $"\"{exePath}\" --silent";
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}

sealed record ServerConfigDto(bool ListenAll, string Host, int Port)
{
    public string ToUrl()
    {
        var port = Math.Clamp(Port, 1, 65535);
        if (ListenAll)
        {
            return $"http://0.0.0.0:{port}";
        }

        var host = string.IsNullOrWhiteSpace(Host) ? "localhost" : Host.Trim();
        return $"http://{host}:{port}";
    }
}

sealed record ServerConfigUpdateRequest(bool? ListenAll, string? Host, int? Port);

static class ServerConfigStore
{
    const string FileName = "server-config.json";

    public static ServerConfigDto? Load()
    {
        var path = GetPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var dto = JsonSerializer.Deserialize<ServerConfigDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Normalize(dto);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(ServerConfigDto config)
    {
        var path = GetPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    public static ServerConfigDto? Normalize(ServerConfigUpdateRequest update)
    {
        var current = Load() ?? new ServerConfigDto(ListenAll: true, Host: "localhost", Port: 5000);
        var next = new ServerConfigDto(
            ListenAll: update.ListenAll ?? current.ListenAll,
            Host: update.Host ?? current.Host,
            Port: update.Port ?? current.Port);
        return Normalize(next);
    }

    static ServerConfigDto? Normalize(ServerConfigDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        var port = dto.Port;
        if (port < 1 || port > 65535)
        {
            return null;
        }

        var host = (dto.Host ?? "").Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            host = "localhost";
        }

        if (!dto.ListenAll)
        {
            if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "0:0:0:0:0:0:0:0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "::", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (host.Contains(' '))
            {
                return null;
            }
        }

        return new ServerConfigDto(dto.ListenAll, host, port);
    }

    static string GetPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrintServer");
        return Path.Combine(dir, FileName);
    }
}

static class NetworkInfo
{
    public static (string Scheme, int Port) GetPrimarySchemeAndPort(string[] urls, int fallbackPort)
    {
        foreach (var url in urls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var scheme = string.IsNullOrWhiteSpace(uri.Scheme) ? "http" : uri.Scheme;
                var port = uri.IsDefaultPort ? fallbackPort : uri.Port;
                if (port >= 1 && port <= 65535)
                {
                    return (scheme, port);
                }
            }
        }

        return ("http", Math.Clamp(fallbackPort, 1, 65535));
    }

    public static string[] BuildLanUrls(string scheme, int port)
    {
        var ips = GetLanIpv4Addresses();
        return ips.Select(ip => $"{scheme}://{ip}:{port}").ToArray();
    }

    public static string BuildLocalUrl(string scheme, int port)
    {
        var safeScheme = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        return $"{safeScheme}://localhost:{port}";
    }

    static string[] GetLanIpv4Addresses()
    {
        var list = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            foreach (var uni in props.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var ip = uni.Address;
                if (IPAddress.IsLoopback(ip))
                {
                    continue;
                }

                var bytes = ip.GetAddressBytes();
                if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
                {
                    continue;
                }

                list.Add(ip.ToString());
            }
        }

        return list
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

static class AppMetadata
{
    internal static readonly string[] RootEndpoints =
    [
        "/ui",
        "/health",
        "/api/printers",
        "/api/printers/status",
        "/api/jobs/active",
        "/api/logs",
        "/api/config",
        "/api/restart",
        "/api/print/raw",
        "/api/print/tspl",
        "/api/print/image"
    ];
}

sealed record RawPrintRequest(string PrinterName, string DataBase64, string? DocumentName);

sealed record TsplPrintRequest(string PrinterName, string Tspl, string? Encoding, string? DocumentName);

sealed record ImagePrintRequest(string PrinterName, string MimeType, string ImageBase64, LabelSpec Label, string? DocumentName);

sealed record LabelSpec(double WidthMm, double HeightMm, int Dpi, int WidthPx, int HeightPx);

sealed record PrintLogEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    string Kind,
    string PrinterName,
    string DocumentName,
    int BytesLength,
    string Status,
    string? Error,
    string? RemoteIp);

sealed class PrintLogStore
{
    static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    readonly System.Threading.Lock gate = new();
    readonly List<PrintLogEntry> entries = [];
    readonly SemaphoreSlim fileGate = new(1, 1);

    readonly ILogger<PrintLogStore> logger;
    readonly int maxEntries;
    readonly string filePath;

    public PrintLogStore(IConfiguration configuration, ILogger<PrintLogStore> logger, IHostEnvironment env)
    {
        this.logger = logger;
        maxEntries = Math.Clamp(configuration.GetValue("PrintServer:MaxLogEntries", 5000), 100, 100_000);
        filePath = ResolveLogPath(configuration["PrintServer:LogPath"], env.ContentRootPath);
        LoadFromFile();
    }

    public IReadOnlyList<PrintLogEntry> GetLatest(int take)
    {
        using (gate.EnterScope())
        {
            var start = Math.Max(entries.Count - take, 0);
            return [.. entries.Skip(start)];
        }
    }

    public async ValueTask AppendAsync(PrintLogEntry entry)
    {
        using (gate.EnterScope())
        {
            entries.Add(entry);
            TrimUnsafe();
        }

        var json = JsonSerializer.Serialize(entry, SerializerOptions);
        await AppendLineAsync(json);
    }

    public async ValueTask ClearAsync()
    {
        using (gate.EnterScope())
        {
            entries.Clear();
        }

        await fileGate.WaitAsync();
        try
        {
            if (File.Exists(filePath))
            {
                File.WriteAllText(filePath, "");
            }
        }
        finally
        {
            fileGate.Release();
        }
    }

    void TrimUnsafe()
    {
        var overflow = entries.Count - maxEntries;
        if (overflow <= 0)
        {
            return;
        }

        entries.RemoveRange(0, overflow);
    }

    async ValueTask AppendLineAsync(string line)
    {
        await fileGate.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(filePath, line + Environment.NewLine, Encoding.UTF8);
        }
        finally
        {
            fileGate.Release();
        }
    }

    void LoadFromFile()
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                PrintLogEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<PrintLogEntry>(line, SerializerOptions);
                }
                catch
                {
                    entry = null;
                }

                if (entry is null)
                {
                    continue;
                }

                entries.Add(entry);
            }

            TrimUnsafe();
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(ex, "Failed to load print logs from {LogPath}", filePath);
            }
        }
    }

    static string ResolveLogPath(string? configuredPath, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.Combine(contentRootPath, "print-logs.jsonl");
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
    }
}

sealed record PrinterStatusDto(
    string Name,
    bool IsDefault,
    bool WorkOffline,
    ushort? PrinterStatus,
    string? Status,
    uint? ExtendedPrinterStatus,
    uint? DetectedErrorState);

sealed record PrintJobDto(
    string PrinterName,
    int? JobId,
    string? Document,
    string? Owner,
    string? Status,
    string? JobStatus,
    uint? TotalPages,
    uint? PagesPrinted,
    uint? SizeBytes,
    DateTimeOffset? TimeSubmitted);

static class PrintJobNameParser
{
    public static (string PrinterName, int? JobId) TryParse(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ("", null);
        }

        var comma = name.LastIndexOf(',');
        if (comma <= 0 || comma >= name.Length - 1)
        {
            return (name, null);
        }

        var printerName = name[..comma];
        var jobIdText = name[(comma + 1)..];
        return int.TryParse(jobIdText, out var jobId)
            ? (printerName, jobId)
            : (printerName, null);
    }
}

static class UiPages
{
    public static string Index { get; } =
        """
        <!doctype html>
        <html lang="pt-br">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>PrintServer - UI</title>
          <style>
            :root { color-scheme: light dark; }
            body { font-family: system-ui, Segoe UI, Roboto, Arial, sans-serif; margin: 0; padding: 16px; }
            header { display: flex; gap: 12px; align-items: baseline; flex-wrap: wrap; }
            h1 { margin: 0; font-size: 18px; }
            .muted { opacity: .7; }
            .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
            input, select { padding: 6px 8px; min-width: 260px; }
            button { padding: 6px 10px; cursor: pointer; }
            table { width: 100%; border-collapse: collapse; margin-top: 12px; }
            th, td { border-bottom: 1px solid rgba(127,127,127,.35); text-align: left; padding: 8px; vertical-align: top; }
            th { position: sticky; top: 0; background: rgba(127,127,127,.12); backdrop-filter: blur(4px); }
            .badge { display: inline-block; padding: 2px 8px; border-radius: 999px; font-size: 12px; border: 1px solid rgba(127,127,127,.35); }
            .ok { color: #0a0; }
            .fail { color: #c00; }
            .grid { display: grid; gap: 12px; grid-template-columns: 1fr; }
            @media (min-width: 900px) { .grid { grid-template-columns: 1fr 1fr; } }
            textarea { width: 100%; min-height: 520px; padding: 8px; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace; font-size: 12px; line-height: 1.35; resize: vertical; }
            .card { border: 1px solid rgba(127,127,127,.35); border-radius: 10px; padding: 12px; }
            .previewWrap { overflow: auto; border: 1px solid rgba(127,127,127,.35); border-radius: 10px; padding: 12px; background: rgba(127,127,127,.08); }
            .previewSurface { background: #fff; color: #000; display: inline-block; }
          </style>
        </head>
        <body>
          <header>
            <h1>PrintServer</h1>
            <span class="muted">Monitoramento e teste de TSPL</span>
          </header>

          <div class="row" style="margin-top: 12px;">
            <label>API Key:</label>
            <input id="apiKey" type="password" placeholder="(opcional) X-Api-Key" />
            <button id="saveKey">Salvar</button>
            <button id="clearKey">Limpar</button>
            <span id="authStatus" class="muted"></span>
          </div>

          <div class="row" style="margin-top: 10px;">
            <button data-tab="logs">Logs</button>
            <button data-tab="jobs">Fila</button>
            <button data-tab="printers">Impressoras</button>
            <button data-tab="tspl">TSPL</button>
            <button data-tab="config">Config</button>
            <span class="muted" id="lastRefresh"></span>
          </div>

          <div id="content" class="grid"></div>

          <script>
            const state = {
              tab: "logs",
              apiKey: localStorage.getItem("printserver_api_key") || "",
              intervalMs: 2000,
              tspl: localStorage.getItem("printserver_tspl") || "",
              tsplPrinter: localStorage.getItem("printserver_tspl_printer") || "",
              tsplDocName: localStorage.getItem("printserver_tspl_doc") || "tspl",
              tsplEncoding: localStorage.getItem("printserver_tspl_encoding") || "ascii",
              tsplMounted: false,
              configMounted: false
            };

            const elApiKey = document.getElementById("apiKey");
            const elAuthStatus = document.getElementById("authStatus");
            const elLastRefresh = document.getElementById("lastRefresh");
            const elContent = document.getElementById("content");

            elApiKey.value = state.apiKey;

            {
              const qs = new URLSearchParams(location.search || "");
              const hash = (location.hash || "").replace("#", "").trim().toLowerCase();
              const tab = (qs.get("tab") || hash || "").trim().toLowerCase();
              const allowed = ["logs", "jobs", "printers", "tspl", "config"];
              if (allowed.includes(tab)) state.tab = tab;
            }

            const defaultTsplSample = [
              'SIZE 100 mm,150 mm',
              'GAP 2 mm,0 mm',
              'DENSITY 8',
              'SPEED 4',
              'DIRECTION 0',
              'REFERENCE 0,0',
              'CODEPAGE 1252',
              'CLS',
              'TEXT 16,16,"0",0,2,2,"ORANGE PEPPER"',
              'LINE 16,80,784,80,2',
              'BOX 16,88,256,328,2,20',
              'QRCODE 24,96,L,5,A,0,"COD:541"',
              'TEXT 272,88,"0",0,1,1,"GTIN:"',
              'TEXT 272,122,"0",0,1,1,"Código: 541"',
              'TEXT 272,156,"0",0,1,1,"Lote:"',
              'TEXT 272,190,"0",0,1,1,"Série:"',
              'TEXT 272,224,"0",0,1,1,"Data Pesagem:"',
              'TEXT 272,258,"0",0,1,1,"Validade:"',
              'TEXT 272,292,"0",0,1,1,"Tara:"',
              'TEXT 272,326,"0",0,1,1,"Peso:"',
              'TEXT 272,360,"0",0,1,1,"R$/kg:"',
              'BOX 344,300,784,412,2,20',
              'TEXT 360,308,"0",0,1,1,"TOTAL R$"',
              'TEXT 360,340,"0",0,3,3,"0,00"',
              'BOX 27,438,67,478,2,8',
              'LINE 67,478,85,496,2',
              'LINE 78,489,88,499,2',
              'TEXT 84,442,"0",0,1,1,"ALTO"',
              'TEXT 92,468,"0",0,1,1,"EM"',
              'REVERSE 16,428,224,80',
              'BOX 16,428,240,508,4,40',
              'TEXT 322,442,"0",0,1,1,"AÇÚCAR"',
              'TEXT 296,468,"0",0,1,1,"ADICIONADO"',
              'REVERSE 248,428,264,80',
              'BOX 248,428,512,508,4,40',
              'TEXT 602,442,"0",0,1,1,"GORDURA"',
              'TEXT 598,468,"0",0,1,1,"SATURADA"',
              'REVERSE 520,428,264,80',
              'BOX 520,428,784,508,4,40',
              'TEXT 16,520,"0",0,1,1,"INFORMAÇÃO NUTRICIONAL"',
              'TEXT 16,544,"0",0,1,1,"PORÇÃO 5g (5g)"',
              'BOX 16,576,784,910,2,20',
              'LINE 16,620,784,620,2',
              'LINE 385,576,385,910,2',
              'LINE 539,576,539,910,2',
              'LINE 693,576,693,910,2',
              'TEXT 393,586,"0",0,1,1,"PORCAO"',
              'TEXT 547,586,"0",0,1,1,"EM 100g"',
              'TEXT 701,586,"0",0,1,1,"%VD*"',
              'LINE 16,620,784,620,1',
              'TEXT 24,628,"0",0,1,1,"Valor Energético (kcal)"',
              'TEXT 393,628,"0",0,1,1,"4"',
              'TEXT 547,628,"0",0,1,1,"88,5"',
              'TEXT 701,628,"0",0,1,1,"0"',
              'LINE 16,649,784,649,1',
              'TEXT 24,657,"0",0,1,1,"Carboidratos (g)"',
              'TEXT 393,657,"0",0,1,1,"1"',
              'TEXT 547,657,"0",0,1,1,"16"',
              'TEXT 701,657,"0",0,1,1,"0"',
              'LINE 16,678,784,678,1',
              'TEXT 24,686,"0",0,1,1,"Açúcares Totais (g)"',
              'TEXT 393,686,"0",0,1,1,"-"',
              'TEXT 547,686,"0",0,1,1,"1,4"',
              'TEXT 701,686,"0",0,1,1,"-"',
              'LINE 16,707,784,707,1',
              'TEXT 24,715,"0",0,1,1,"Açúcares Adicionados (g)"',
              'TEXT 393,715,"0",0,1,1,"-"',
              'TEXT 547,715,"0",0,1,1,"1,4"',
              'TEXT 701,715,"0",0,1,1,"0"',
              'LINE 16,736,784,736,1',
              'TEXT 24,744,"0",0,1,1,"Proteínas (g)"',
              'TEXT 393,744,"0",0,1,1,"-"',
              'TEXT 547,744,"0",0,1,1,"1,4"',
              'TEXT 701,744,"0",0,1,1,"0"',
              'LINE 16,765,784,765,1',
              'TEXT 24,773,"0",0,1,1,"Gorduras Totais (g)"',
              'TEXT 393,773,"0",0,1,1,"-"',
              'TEXT 547,773,"0",0,1,1,"2,1"',
              'TEXT 701,773,"0",0,1,1,"0"',
              'LINE 16,794,784,794,1',
              'TEXT 24,802,"0",0,1,1,"Gorduras Saturadas (g)"',
              'TEXT 393,802,"0",0,1,1,"-"',
              'TEXT 547,802,"0",0,1,1,"0,3"',
              'TEXT 701,802,"0",0,1,1,"0"',
              'LINE 16,823,784,823,1',
              'TEXT 24,831,"0",0,1,1,"Gorduras Trans (g)"',
              'TEXT 393,831,"0",0,1,1,"-"',
              'TEXT 547,831,"0",0,1,1,"0"',
              'TEXT 701,831,"0",0,1,1,"**"',
              'LINE 16,852,784,852,1',
              'TEXT 24,860,"0",0,1,1,"Fibra Alimentar (g)"',
              'TEXT 393,860,"0",0,1,1,"-"',
              'TEXT 547,860,"0",0,1,1,"2,9"',
              'TEXT 701,860,"0",0,1,1,"1"',
              'LINE 16,881,784,881,1',
              'TEXT 24,889,"0",0,1,1,"Sódio (mg)"',
              'TEXT 393,889,"0",0,1,1,"697"',
              'TEXT 547,889,"0",0,1,1,"13.939"',
              'TEXT 701,889,"0",0,1,1,"29"',
              'TEXT 16,918,"0",0,1,1,"*% Valores diários fornecidos pela porção."',
              'TEXT 16,946,"0",0,1,1,"** Valor diário não estabelecido."',
              'PRINT 1,1'
            ].join("\n");

            if (!state.tspl) state.tspl = defaultTsplSample;

            document.getElementById("saveKey").addEventListener("click", () => {
              state.apiKey = elApiKey.value || "";
              localStorage.setItem("printserver_api_key", state.apiKey);
              refresh();
            });

            document.getElementById("clearKey").addEventListener("click", () => {
              state.apiKey = "";
              elApiKey.value = "";
              localStorage.removeItem("printserver_api_key");
              refresh();
            });

            document.querySelectorAll("button[data-tab]").forEach(btn => {
              btn.addEventListener("click", () => {
                state.tab = btn.getAttribute("data-tab");
                refresh();
              });
            });

            function headers() {
              const h = {};
              if (state.apiKey) h["X-Api-Key"] = state.apiKey;
              return h;
            }

            async function getJson(path) {
              const res = await fetch(path, { headers: headers() });
              if (res.status === 401) {
                elAuthStatus.textContent = "Não autorizado (401). Configure a API Key.";
                throw new Error("unauthorized");
              }
              elAuthStatus.textContent = state.apiKey ? "Autenticado" : "";
              if (!res.ok) throw new Error(await res.text());
              return res.json();
            }

            async function postJson(path, payload) {
              const res = await fetch(path, {
                method: "POST",
                headers: { ...headers(), "Content-Type": "application/json" },
                body: JSON.stringify(payload)
              });
              if (res.status === 401) {
                elAuthStatus.textContent = "Não autorizado (401). Configure a API Key.";
                throw new Error("unauthorized");
              }
              elAuthStatus.textContent = state.apiKey ? "Autenticado" : "";
              if (!res.ok) throw new Error(await res.text());
              return res.json();
            }

            function formatTs(iso) {
              try { return new Date(iso).toLocaleString(); } catch { return iso; }
            }

            function renderTable(title, rowsHtml) {
              return `
                <section style="grid-column: 1 / -1;">
                  <h2 style="font-size: 14px; margin: 12px 0 6px 0;">${title}</h2>
                  <div style="overflow:auto;">
                    <table>
                      ${rowsHtml}
                    </table>
                  </div>
                </section>
              `;
            }

            async function refreshLogs() {
              const logs = await getJson("/api/logs?take=500");
              const head = `
                <thead><tr>
                  <th>Quando</th><th>Tipo</th><th>Impressora</th><th>Documento</th><th>Bytes</th><th>Status</th><th>IP</th><th>Erro</th>
                </tr></thead>`;
              const body = `<tbody>${logs.map(l => `
                <tr>
                  <td>${formatTs(l.timestamp)}</td>
                  <td><span class="badge">${l.kind}</span></td>
                  <td>${l.printerName}</td>
                  <td>${l.documentName}</td>
                  <td>${l.bytesLength}</td>
                  <td class="${l.status === "queued" ? "ok" : "fail"}">${l.status}</td>
                  <td>${l.remoteIp || ""}</td>
                  <td class="fail">${l.error || ""}</td>
                </tr>`).join("")}</tbody>`;
              elContent.innerHTML = renderTable("Logs de impressão", head + body);
            }

            async function refreshJobs() {
              const jobs = await getJson("/api/jobs/active");
              const head = `
                <thead><tr>
                  <th>Quando</th><th>Impressora</th><th>JobId</th><th>Documento</th><th>Dono</th><th>Status</th><th>Páginas</th><th>Tamanho</th>
                </tr></thead>`;
              const body = `<tbody>${jobs.map(j => `
                <tr>
                  <td>${j.timeSubmitted ? formatTs(j.timeSubmitted) : ""}</td>
                  <td>${j.printerName || ""}</td>
                  <td>${j.jobId ?? ""}</td>
                  <td>${j.document || ""}</td>
                  <td>${j.owner || ""}</td>
                  <td>${j.jobStatus || j.status || ""}</td>
                  <td>${(j.pagesPrinted ?? "")}${(j.totalPages != null ? " / " + j.totalPages : "")}</td>
                  <td>${j.sizeBytes ?? ""}</td>
                </tr>`).join("")}</tbody>`;
              elContent.innerHTML = renderTable("Fila de impressão (Win32_PrintJob)", head + body);
            }

            async function refreshPrinters() {
              const printers = await getJson("/api/printers/status");
              const head = `
                <thead><tr>
                  <th>Nome</th><th>Padrão</th><th>Offline</th><th>Status</th><th>PrinterStatus</th><th>Extended</th><th>ErrorState</th>
                </tr></thead>`;
              const body = `<tbody>${printers.map(p => `
                <tr>
                  <td>${p.name}</td>
                  <td>${p.isDefault ? "sim" : ""}</td>
                  <td class="${p.workOffline ? "fail" : "ok"}">${p.workOffline ? "sim" : "não"}</td>
                  <td>${p.status || ""}</td>
                  <td>${p.printerStatus ?? ""}</td>
                  <td>${p.extendedPrinterStatus ?? ""}</td>
                  <td>${p.detectedErrorState ?? ""}</td>
                </tr>`).join("")}</tbody>`;
              elContent.innerHTML = renderTable("Impressoras (Win32_Printer)", head + body);
            }

            async function refreshConfig() {
              if (!state.configMounted) {
                elContent.innerHTML = `
                  <section class="card" style="grid-column: 1 / -1;">
                    <h2 style="font-size: 14px; margin: 0 0 10px 0;">Configuração do servidor</h2>

                    <div class="row" style="margin-top: 6px;">
                      <label style="min-width: 120px;">Escutar em</label>
                      <label style="display:flex; gap:8px; align-items:center;">
                        <input id="cfg_listenAll" type="checkbox" />
                        Todas interfaces (0.0.0.0)
                      </label>
                    </div>

                    <div class="row" style="margin-top: 6px;">
                      <label style="min-width: 120px;">Host/IP</label>
                      <input id="cfg_host" placeholder="Ex: localhost ou 192.168.0.10" />
                    </div>

                    <div class="row" style="margin-top: 6px;">
                      <label style="min-width: 120px;">Porta</label>
                      <input id="cfg_port" type="number" min="1" max="65535" />
                    </div>

                    <div class="row" style="margin-top: 10px;">
                      <button id="cfg_load">Carregar</button>
                      <button id="cfg_save">Salvar</button>
                      <button id="cfg_restart">Salvar e reiniciar</button>
                      <span id="cfg_status" class="muted"></span>
                    </div>

                    <div style="margin-top: 10px;">
                      <div class="muted" style="margin-bottom: 6px;">URLs efetivas</div>
                      <pre id="cfg_urls" style="margin:0;"></pre>
                    </div>
                    <div style="margin-top: 10px;">
                      <div class="muted" style="margin-bottom: 6px;">URL local</div>
                      <pre id="cfg_local" style="margin:0;"></pre>
                    </div>
                    <div style="margin-top: 10px;">
                      <div class="muted" style="margin-bottom: 6px;">URLs na rede (DHCP)</div>
                      <pre id="cfg_lan" style="margin:0;"></pre>
                    </div>
                  </section>
                `;

                const elAll = document.getElementById("cfg_listenAll");
                const elHost = document.getElementById("cfg_host");
                const elPort = document.getElementById("cfg_port");
                const elStatus = document.getElementById("cfg_status");
                const elUrls = document.getElementById("cfg_urls");
                const elLocal = document.getElementById("cfg_local");
                const elLan = document.getElementById("cfg_lan");

                function setBusy(text) { elStatus.textContent = text || ""; }

                async function load() {
                  setBusy("Carregando...");
                  try {
                    const data = await getJson("/api/config");
                    elAll.checked = !!(data.config && data.config.listenAll);
                    elHost.value = (data.config && data.config.host) ? data.config.host : "localhost";
                    elPort.value = (data.config && data.config.port) ? String(data.config.port) : "5000";
                    elUrls.textContent = (data.effectiveUrls || []).join("\n");
                    elLocal.textContent = data.localUrl || "";
                    elLan.textContent = (data.lanUrls || []).join("\n");
                    elHost.disabled = elAll.checked;
                    setBusy("");
                  } catch (e) {
                    if (String(e).includes("unauthorized")) return;
                    setBusy(String(e));
                  }
                }

                async function save(restart) {
                  setBusy("Salvando...");
                  try {
                    const listenAll = !!elAll.checked;
                    const host = (elHost.value || "").trim();
                    const port = parseInt(String(elPort.value || ""), 10);
                    await postJson("/api/config", { listenAll, host, port });
                    setBusy(restart ? "Reiniciando..." : "Salvo. Reinicie para aplicar.");
                    if (restart) {
                      await postJson("/api/restart", {});
                    }
                  } catch (e) {
                    if (String(e).includes("unauthorized")) return;
                    setBusy(String(e));
                  }
                }

                elAll.addEventListener("change", () => {
                  elHost.disabled = elAll.checked;
                });

                document.getElementById("cfg_load").addEventListener("click", load);
                document.getElementById("cfg_save").addEventListener("click", () => save(false));
                document.getElementById("cfg_restart").addEventListener("click", () => save(true));

                state.configMounted = true;
                await load();
                return;
              }
            }

            function splitArgs(text) {
              const out = [];
              let cur = "";
              let inQuotes = false;
              for (let i = 0; i < text.length; i++) {
                const ch = text[i];
                if (ch === '"') {
                  inQuotes = !inQuotes;
                  cur += ch;
                  continue;
                }
                if (ch === "," && !inQuotes) {
                  out.push(cur.trim());
                  cur = "";
                  continue;
                }
                cur += ch;
              }
              if (cur.trim()) out.push(cur.trim());
              return out;
            }

            function unquote(text) {
              const t = (text ?? "").trim();
              if (t.length >= 2 && t.startsWith('"') && t.endsWith('"')) return t.slice(1, -1);
              return t;
            }

            function parseMmPart(text) {
              const t = (text ?? "").trim();
              const m = /^([0-9]+(?:\.[0-9]+)?)\s*(mm|dot|dots)?$/i.exec(t);
              if (!m) return null;
              return { value: Number(m[1]), unit: (m[2] || "mm").toLowerCase() };
            }

            function parseTspl(tsplText) {
              const lines = String(tsplText || "").replaceAll("\r\n", "\n").replaceAll("\r", "\n").split("\n");
              const elements = [];
              const warnings = [];
              let sizeMm = null;
              let reference = { x: 0, y: 0 };
              let maxX = 0;
              let maxY = 0;

              function updateMax(x, y) {
                if (Number.isFinite(x)) maxX = Math.max(maxX, x);
                if (Number.isFinite(y)) maxY = Math.max(maxY, y);
              }

              for (let i = 0; i < lines.length; i++) {
                const raw = lines[i];
                const line = raw.trim();
                if (!line) continue;

                const firstSpace = line.indexOf(" ");
                const cmd = (firstSpace === -1 ? line : line.slice(0, firstSpace)).trim().toUpperCase();
                const argsText = firstSpace === -1 ? "" : line.slice(firstSpace + 1).trim();

                if (cmd === "SIZE") {
                  const parts = splitArgs(argsText);
                  if (parts.length >= 2) {
                    const a = parseMmPart(parts[0]);
                    const b = parseMmPart(parts[1]);
                    if (a && b) {
                      sizeMm = { w: a, h: b };
                      continue;
                    }
                  }
                  warnings.push(`SIZE inválido na linha ${i + 1}: ${raw}`);
                  continue;
                }

                if (cmd === "REFERENCE") {
                  const parts = splitArgs(argsText);
                  if (parts.length >= 2) {
                    const x = Number(parts[0]);
                    const y = Number(parts[1]);
                    if (Number.isFinite(x) && Number.isFinite(y)) {
                      reference = { x, y };
                      continue;
                    }
                  }
                  warnings.push(`REFERENCE inválido na linha ${i + 1}: ${raw}`);
                  continue;
                }

                if (cmd === "CLS") {
                  elements.length = 0;
                  continue;
                }

                if (cmd === "TEXT") {
                  const parts = splitArgs(argsText);
                  if (parts.length >= 7) {
                    const x = Number(parts[0]) + reference.x;
                    const y = Number(parts[1]) + reference.y;
                    const font = unquote(parts[2]);
                    const rotationRaw = Number(parts[3]);
                    const xMul = Number(parts[4]);
                    const yMul = Number(parts[5]);
                    const text = unquote(parts.slice(6).join(","));
                    const rotation = rotationRaw <= 3 ? rotationRaw * 90 : rotationRaw;
                    elements.push({ kind: "text", x, y, font, rotation, xMul, yMul, text });
                    updateMax(x, y);
                    continue;
                  }
                  warnings.push(`TEXT inválido na linha ${i + 1}: ${raw}`);
                  continue;
                }

                if (cmd === "LINE") {
                  const parts = splitArgs(argsText);
                  if (parts.length >= 5) {
                    const x1 = Number(parts[0]) + reference.x;
                    const y1 = Number(parts[1]) + reference.y;
                    const x2 = Number(parts[2]) + reference.x;
                    const y2 = Number(parts[3]) + reference.y;
                    const thickness = Math.max(1, Number(parts[4]) || 1);
                    elements.push({ kind: "line", x1, y1, x2, y2, thickness });
                    updateMax(x1, y1);
                    updateMax(x2, y2);
                    continue;
                  }
                  warnings.push(`LINE inválido na linha ${i + 1}: ${raw}`);
                  continue;
                }

                if (cmd === "BAR") {
                  const parts = splitArgs(argsText);
                  if (parts.length >= 4) {
                    const x = Number(parts[0]) + reference.x;
                    const y = Number(parts[1]) + reference.y;
                    const w = Math.max(0, Number(parts[2]) || 0);
                    const h = Math.max(0, Number(parts[3]) || 0);
                    elements.push({ kind: "bar", x, y, w, h });
                    updateMax(x, y);
                    updateMax(x + w, y + h);
                    continue;
                  }
                  warnings.push(`BAR inválido na linha ${i + 1}: ${raw}`);
                  continue;
                }

                if (cmd === "BOX") {
                  const parts = splitArgs(argsText);
                  if (parts.length >= 5) {
                    const x1 = Number(parts[0]) + reference.x;
                    const y1 = Number(parts[1]) + reference.y;
                    const x2 = Number(parts[2]) + reference.x;
                    const y2 = Number(parts[3]) + reference.y;
                    const thickness = Math.max(1, Number(parts[4]) || 1);
                    const radius = parts.length >= 6 ? Math.max(0, Number(parts[5]) || 0) : 0;
                    elements.push({ kind: "box", x1, y1, x2, y2, thickness, radius });
                    updateMax(x1, y1);
                    updateMax(x2, y2);
                    continue;
                  }
                  warnings.push(`BOX inválido na linha ${i + 1}: ${raw}`);
                  continue;
                }

                if (cmd === "REVERSE") {
                  const parts = splitArgs(argsText);
                  if (parts.length >= 4) {
                    const x = Number(parts[0]) + reference.x;
                    const y = Number(parts[1]) + reference.y;
                    const w = Math.max(0, Number(parts[2]) || 0);
                    const h = Math.max(0, Number(parts[3]) || 0);
                    let radius = 0;
                    for (let j = elements.length - 1; j >= 0; j--) {
                      const el = elements[j];
                      if (el.kind !== "box" || !el.radius) continue;
                      const bx1 = Math.min(el.x1, el.x2);
                      const by1 = Math.min(el.y1, el.y2);
                      const bw = Math.abs(el.x2 - el.x1);
                      const bh = Math.abs(el.y2 - el.y1);
                      if (bx1 === x && by1 === y && bw === w && bh === h) {
                        radius = el.radius;
                        break;
                      }
                    }
                    elements.push({ kind: "reverse", x, y, w, h, radius });
                    updateMax(x, y);
                    updateMax(x + w, y + h);
                    continue;
                  }
                  warnings.push(`REVERSE inválido na linha ${i + 1}: ${raw}`);
                  continue;
                }

                if (cmd === "QRCODE") {
                  const parts = splitArgs(argsText);
                  if (parts.length >= 7) {
                    const x = Number(parts[0]) + reference.x;
                    const y = Number(parts[1]) + reference.y;
                    const ecc = String(parts[2] || "").trim();
                    const cell = Math.max(1, Number(parts[3]) || 1);
                    const mode = String(parts[4] || "").trim();
                    const rotationRaw = Number(parts[5]);
                    const data = unquote(parts.slice(6).join(","));
                    const rotation = rotationRaw <= 3 ? rotationRaw * 90 : rotationRaw;
                    elements.push({ kind: "qrcode", x, y, ecc, cell, mode, rotation, data });
                    updateMax(x, y);
                    continue;
                  }
                  warnings.push(`QRCODE inválido na linha ${i + 1}: ${raw}`);
                  continue;
                }
              }

              for (const el of elements) {
                if (el.kind !== "reverse" || el.radius) continue;
                for (const candidate of elements) {
                  if (candidate.kind !== "box" || !candidate.radius) continue;
                  const bx1 = Math.min(candidate.x1, candidate.x2);
                  const by1 = Math.min(candidate.y1, candidate.y2);
                  const bw = Math.abs(candidate.x2 - candidate.x1);
                  const bh = Math.abs(candidate.y2 - candidate.y1);
                  if (bx1 === el.x && by1 === el.y && bw === el.w && bh === el.h) {
                    el.radius = candidate.radius;
                    break;
                  }
                }
              }

              let dotsPerMm = 8;
              if (sizeMm?.w?.unit === "mm" && sizeMm.w.value > 0 && maxX > 0) {
                dotsPerMm = Math.max(1, Math.round(maxX / sizeMm.w.value));
              }

              let widthDots = Math.max(800, maxX + 32);
              let heightDots = Math.max(600, maxY + 32);
              if (sizeMm?.w?.unit === "mm" && sizeMm?.h?.unit === "mm") {
                widthDots = Math.max(widthDots, Math.round(sizeMm.w.value * dotsPerMm));
                heightDots = Math.max(heightDots, Math.round(sizeMm.h.value * dotsPerMm));
              }

              return { elements, warnings, widthDots, heightDots, dotsPerMm, sizeMm };
            }

            function renderTsplPreview(container, parsed) {
              container.textContent = "";
              const canvas = document.createElement("canvas");
              canvas.width = parsed.widthDots;
              canvas.height = parsed.heightDots;
              canvas.style.maxWidth = "100%";
              canvas.style.height = "auto";
              canvas.style.imageRendering = "auto";
              const ctx = canvas.getContext("2d");
              if (!ctx) return;

              function invertRect(x, y, w, h) {
                const radius = arguments.length >= 5 ? Number(arguments[4]) || 0 : 0;
                const ix = Math.max(0, Math.floor(x));
                const iy = Math.max(0, Math.floor(y));
                const iw = Math.max(0, Math.min(canvas.width - ix, Math.floor(w)));
                const ih = Math.max(0, Math.min(canvas.height - iy, Math.floor(h)));
                if (!iw || !ih) return;
                const img = ctx.getImageData(ix, iy, iw, ih);
                const d = img.data;
                const r = Math.max(0, Math.min(radius, iw / 2, ih / 2));
                for (let i = 0; i < d.length; i += 4) {
                  if (r) {
                    const p = i / 4;
                    const px = p % iw;
                    const py = (p - px) / iw;
                    const insideCore = (px >= r && px <= iw - r) || (py >= r && py <= ih - r);
                    if (!insideCore) {
                      const cx = px < r ? r : iw - r;
                      const cy = py < r ? r : ih - r;
                      const dx = px - cx;
                      const dy = py - cy;
                      if (dx * dx + dy * dy > r * r) continue;
                    }
                  }
                  d[i] = 255 - d[i];
                  d[i + 1] = 255 - d[i + 1];
                  d[i + 2] = 255 - d[i + 2];
                }
                ctx.putImageData(img, ix, iy);
              }

              function roundedRectPath(x, y, w, h, r) {
                const rr = Math.max(0, Math.min(r || 0, w / 2, h / 2));
                ctx.beginPath();
                if (!rr) {
                  ctx.rect(x, y, w, h);
                  return;
                }
                ctx.moveTo(x + rr, y);
                ctx.lineTo(x + w - rr, y);
                ctx.quadraticCurveTo(x + w, y, x + w, y + rr);
                ctx.lineTo(x + w, y + h - rr);
                ctx.quadraticCurveTo(x + w, y + h, x + w - rr, y + h);
                ctx.lineTo(x + rr, y + h);
                ctx.quadraticCurveTo(x, y + h, x, y + h - rr);
                ctx.lineTo(x, y + rr);
                ctx.quadraticCurveTo(x, y, x + rr, y);
                ctx.closePath();
              }

              ctx.fillStyle = "#fff";
              ctx.fillRect(0, 0, canvas.width, canvas.height);
              ctx.fillStyle = "#000";
              ctx.strokeStyle = "#000";

              for (const el of parsed.elements) {
                if (el.kind === "bar") {
                  ctx.fillStyle = "#000";
                  ctx.fillRect(el.x, el.y, el.w, el.h);
                  continue;
                }
                if (el.kind === "line") {
                  ctx.strokeStyle = "#000";
                  ctx.lineWidth = el.thickness;
                  ctx.beginPath();
                  ctx.moveTo(el.x1, el.y1);
                  ctx.lineTo(el.x2, el.y2);
                  ctx.stroke();
                  continue;
                }
                if (el.kind === "box") {
                  const x = Math.min(el.x1, el.x2);
                  const y = Math.min(el.y1, el.y2);
                  const w = Math.abs(el.x2 - el.x1);
                  const h = Math.abs(el.y2 - el.y1);
                  const filled = el.thickness >= Math.min(w, h) / 2;
                  if (filled) {
                    ctx.fillStyle = "#000";
                    roundedRectPath(x, y, w, h, el.radius || 0);
                    ctx.fill();
                  } else {
                    ctx.strokeStyle = "#000";
                    ctx.lineWidth = el.thickness;
                    roundedRectPath(x, y, w, h, el.radius || 0);
                    ctx.stroke();
                  }
                  continue;
                }
                if (el.kind === "text") {
                  const baseFontHeight = 24;
                  const fontSize = baseFontHeight * (Number.isFinite(el.yMul) ? el.yMul : 1);
                  ctx.fillStyle = "#000";
                  ctx.font = `${Math.max(8, Math.round(fontSize))}px ui-monospace, SFMono-Regular, Menlo, Consolas, monospace`;
                  ctx.textBaseline = "top";
                  ctx.save();
                  ctx.translate(el.x, el.y);
                  if (el.rotation) ctx.rotate((el.rotation * Math.PI) / 180);
                  ctx.fillText(String(el.text ?? ""), 0, 0);
                  ctx.restore();
                  continue;
                }
                if (el.kind === "qrcode") {
                  const approxModules = 29;
                  const size = Math.max(21, el.cell * approxModules);
                  ctx.save();
                  ctx.translate(el.x, el.y);
                  if (el.rotation) ctx.rotate((el.rotation * Math.PI) / 180);
                  ctx.strokeStyle = "#000";
                  ctx.lineWidth = 2;
                  ctx.strokeRect(0, 0, size, size);
                  ctx.fillStyle = "#000";
                  ctx.font = "14px ui-monospace, SFMono-Regular, Menlo, Consolas, monospace";
                  ctx.textBaseline = "top";
                  ctx.fillText("QR", 6, 6);
                  ctx.restore();
                  continue;
                }
                if (el.kind === "reverse") {
                  invertRect(el.x, el.y, el.w, el.h, el.radius || 0);
                  continue;
                }
              }

              container.appendChild(canvas);
            }

            async function refreshTspl() {
              if (!state.tsplMounted) {
                elContent.className = "grid";
                elContent.innerHTML = `
                  <section class="card">
                    <h2 style="font-size: 14px; margin: 0 0 8px 0;">TSPL</h2>
                    <div class="row" style="margin: 6px 0 10px 0;">
                      <label>Impressora:</label>
                      <select id="tspl_printer"></select>
                      <label>Doc:</label>
                      <input id="tspl_doc" type="text" style="min-width:160px" />
                      <label>Encoding:</label>
                      <select id="tspl_encoding" style="min-width:140px">
                        <option value="ascii">ascii</option>
                        <option value="utf-8">utf-8</option>
                      </select>
                      <button id="tspl_render">Atualizar prévia</button>
                      <button id="tspl_print">Imprimir</button>
                      <span class="muted" id="tspl_status"></span>
                    </div>
                    <textarea id="tspl_text" spellcheck="false"></textarea>
                    <div id="tspl_warnings" class="muted" style="margin-top: 10px;"></div>
                  </section>
                  <section class="card">
                    <h2 style="font-size: 14px; margin: 0 0 8px 0;">Prévia</h2>
                    <div class="previewWrap">
                      <div id="tspl_preview" class="previewSurface"></div>
                    </div>
                    <div class="muted" id="tspl_meta" style="margin-top: 10px;"></div>
                  </section>
                `;

                const elText = document.getElementById("tspl_text");
                const elWarnings = document.getElementById("tspl_warnings");
                const elPreview = document.getElementById("tspl_preview");
                const elMeta = document.getElementById("tspl_meta");
                const elPrinter = document.getElementById("tspl_printer");
                const elDoc = document.getElementById("tspl_doc");
                const elEncoding = document.getElementById("tspl_encoding");
                const elStatus = document.getElementById("tspl_status");

                elText.value = state.tspl;
                elDoc.value = state.tsplDocName;
                elEncoding.value = state.tsplEncoding;

                const printers = await getJson("/api/printers");
                elPrinter.innerHTML = `<option value="">(selecione)</option>` + printers.map(p => {
                  const selected = state.tsplPrinter && p.name && String(p.name).toLowerCase() === String(state.tsplPrinter).toLowerCase() ? "selected" : "";
                  const suffix = p.isDefault ? " (padrão)" : "";
                  return `<option ${selected} value="${String(p.name).replaceAll('"', "&quot;")}">${p.name}${suffix}</option>`;
                }).join("");

                function doRender() {
                  state.tspl = elText.value || "";
                  localStorage.setItem("printserver_tspl", state.tspl);
                  const parsed = parseTspl(state.tspl);
                  renderTsplPreview(elPreview, parsed);
                  const lines = state.tspl.replaceAll("\r\n", "\n").replaceAll("\r", "\n").split("\n").length;
                  const size = parsed.sizeMm?.w?.unit === "mm" && parsed.sizeMm?.h?.unit === "mm"
                    ? `${parsed.sizeMm.w.value}mm x ${parsed.sizeMm.h.value}mm`
                    : `${parsed.widthDots} x ${parsed.heightDots} dots`;
                  elMeta.textContent = `Tamanho: ${size} | Escala estimada: ${parsed.dotsPerMm} dots/mm | Linhas: ${lines}`;
                  if (parsed.warnings.length) {
                    elWarnings.innerHTML = `<pre style="white-space:pre-wrap; margin:0;">${parsed.warnings.join("\n")}</pre>`;
                  } else {
                    elWarnings.textContent = "";
                  }
                }

                document.getElementById("tspl_render").addEventListener("click", () => doRender());
                elText.addEventListener("input", () => {
                  state.tspl = elText.value || "";
                  localStorage.setItem("printserver_tspl", state.tspl);
                });
                elPrinter.addEventListener("change", () => {
                  state.tsplPrinter = elPrinter.value || "";
                  localStorage.setItem("printserver_tspl_printer", state.tsplPrinter);
                });
                elDoc.addEventListener("input", () => {
                  state.tsplDocName = elDoc.value || "tspl";
                  localStorage.setItem("printserver_tspl_doc", state.tsplDocName);
                });
                elEncoding.addEventListener("change", () => {
                  state.tsplEncoding = elEncoding.value || "ascii";
                  localStorage.setItem("printserver_tspl_encoding", state.tsplEncoding);
                });

                document.getElementById("tspl_print").addEventListener("click", async () => {
                  elStatus.textContent = "";
                  try {
                    const printerName = (elPrinter.value || "").trim();
                    if (!printerName) {
                      elStatus.textContent = "Selecione uma impressora.";
                      return;
                    }
                    const docName = (elDoc.value || "tspl").trim() || "tspl";
                    const encoding = (elEncoding.value || "ascii").trim() || "ascii";
                    const tspl = elText.value || "";
                    await postJson("/api/print/tspl", { printerName, tspl, documentName: docName, encoding });
                    elStatus.textContent = "Enviado para impressão.";
                  } catch (e) {
                    if (String(e).includes("unauthorized")) return;
                    elStatus.textContent = String(e);
                  }
                });

                doRender();
                state.tsplMounted = true;
              }
            }

            async function refresh() {
              const now = new Date();
              elLastRefresh.textContent = "Atualizado em " + now.toLocaleTimeString();

              try {
                if (state.tab === "logs") return await refreshLogs();
                if (state.tab === "jobs") return await refreshJobs();
                if (state.tab === "printers") return await refreshPrinters();
                if (state.tab === "tspl") return await refreshTspl();
                if (state.tab === "config") return await refreshConfig();
              } catch (e) {
                if (String(e).includes("unauthorized")) return;
                elContent.innerHTML = `<pre>${String(e)}</pre>`;
              }
            }

            refresh();
            setInterval(refresh, state.intervalMs);
          </script>
        </body>
        </html>
        """;
}

static class PrinterDiscovery
{
    public static bool Exists(string printerName)
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Printer");
        return searcher
            .Get()
            .Cast<ManagementBaseObject>()
            .Select(printer => printer["Name"]?.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Any(name => string.Equals(name, printerName, StringComparison.OrdinalIgnoreCase));
    }
}

static partial class RawPrinter
{
    public static bool SendBytesToPrinter(string printerName, byte[] bytes, string documentName)
    {
        if (!OpenPrinter(printerName, out var printerHandle, IntPtr.Zero))
        {
            return false;
        }

        try
        {
            IntPtr docNamePtr = IntPtr.Zero;
            IntPtr dataTypePtr = IntPtr.Zero;
            IntPtr docInfoPtr = IntPtr.Zero;
            try
            {
                docNamePtr = Marshal.StringToCoTaskMemUni(documentName);
                dataTypePtr = Marshal.StringToCoTaskMemUni("RAW");

                var docInfo = new DOC_INFO_1W
                {
                    pDocName = docNamePtr,
                    pOutputFile = IntPtr.Zero,
                    pDatatype = dataTypePtr
                };

                docInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<DOC_INFO_1W>());
                Marshal.StructureToPtr(docInfo, docInfoPtr, fDeleteOld: false);
            }
            catch
            {
                if (docInfoPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(docInfoPtr);
                }

                if (docNamePtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(docNamePtr);
                }

                if (dataTypePtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(dataTypePtr);
                }

                throw;
            }

            try
            {
                if (StartDocPrinter(printerHandle, 1, docInfoPtr) == 0)
                {
                    return false;
                }
            }
            finally
            {
                if (docInfoPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(docInfoPtr);
                }

                if (docNamePtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(docNamePtr);
                }

                if (dataTypePtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(dataTypePtr);
                }
            }

            try
            {
                if (!StartPagePrinter(printerHandle))
                {
                    return false;
                }

                try
                {
                    var unmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
                    try
                    {
                        Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);
                        var success = WritePrinter(printerHandle, unmanagedBytes, bytes.Length, out var bytesWritten);
                        return success && bytesWritten == bytes.Length;
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(unmanagedBytes);
                    }
                }
                finally
                {
                    EndPagePrinter(printerHandle);
                }
            }
            finally
            {
                EndDocPrinter(printerHandle);
            }
        }
        finally
        {
            ClosePrinter(printerHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DOC_INFO_1W
    {
        public IntPtr pDocName;
        public IntPtr pOutputFile;
        public IntPtr pDatatype;
    }

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(IntPtr hPrinter);

    [LibraryImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true)]
    private static partial int StartDocPrinter(IntPtr hPrinter, int level, IntPtr pDocInfo);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndDocPrinter(IntPtr hPrinter);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartPagePrinter(IntPtr hPrinter);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndPagePrinter(IntPtr hPrinter);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);
}

static class PngValidation
{
    static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool LooksLikePng(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= Signature.Length && bytes[..Signature.Length].SequenceEqual(Signature);
    }
}

static class BitmapPrinter
{
    public static bool PrintPngToPrinter(string printerName, byte[] pngBytes, double widthMm, double heightMm, string documentName)
    {
        using var ms = new MemoryStream(pngBytes, writable: false);
        using var image = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);

        using var printDoc = new PrintDocument();
        printDoc.PrinterSettings.PrinterName = printerName;
        if (!printDoc.PrinterSettings.IsValid)
        {
            return false;
        }

        printDoc.DocumentName = documentName;
        printDoc.PrintController = new StandardPrintController();
        printDoc.OriginAtMargins = false;

        var paperWidthHundredthsInch = (int)Math.Round((widthMm / 25.4) * 100, MidpointRounding.AwayFromZero);
        var paperHeightHundredthsInch = (int)Math.Round((heightMm / 25.4) * 100, MidpointRounding.AwayFromZero);
        if (paperWidthHundredthsInch <= 0 || paperHeightHundredthsInch <= 0)
        {
            return false;
        }

        printDoc.DefaultPageSettings.PaperSize = new PaperSize("Custom", paperWidthHundredthsInch, paperHeightHundredthsInch);
        printDoc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

        printDoc.PrintPage += (_, e) =>
        {
            var g = e.Graphics;
            if (g is null)
            {
                e.HasMorePages = false;
                return;
            }

            var dpiX = g.DpiX;
            var dpiY = g.DpiY;
            var targetWidthPx = (float)((widthMm / 25.4) * dpiX);
            var targetHeightPx = (float)((heightMm / 25.4) * dpiY);

            g.PageUnit = GraphicsUnit.Pixel;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            g.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);
            g.DrawImage(image, new RectangleF(0, 0, targetWidthPx, targetHeightPx));

            e.HasMorePages = false;
        };

        printDoc.Print();
        return true;
    }
}
