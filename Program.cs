using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
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
        logger.LogInformation("Queued RAW print: printer={PrinterName} doc={DocumentName} bytes={BytesLength} remoteIp={RemoteIp}", entry.PrinterName, entry.DocumentName, entry.BytesLength, entry.RemoteIp);
    }
    else
    {
        logger.LogWarning("RAW print failed: printer={PrinterName} doc={DocumentName} bytes={BytesLength} remoteIp={RemoteIp}", entry.PrinterName, entry.DocumentName, entry.BytesLength, entry.RemoteIp);
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
        logger.LogInformation("Queued TSPL print: printer={PrinterName} doc={DocumentName} bytes={BytesLength} remoteIp={RemoteIp}", entry.PrinterName, entry.DocumentName, entry.BytesLength, entry.RemoteIp);
    }
    else
    {
        logger.LogWarning("TSPL print failed: printer={PrinterName} doc={DocumentName} bytes={BytesLength} remoteIp={RemoteIp}", entry.PrinterName, entry.DocumentName, entry.BytesLength, entry.RemoteIp);
    }

    return ok
        ? Results.Ok(new { status = "queued" })
        : Results.Problem(title: "print_failed", statusCode: StatusCodes.Status500InternalServerError);
});

app.Run();

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
        "/api/print/raw",
        "/api/print/tspl"
    ];
}

sealed record RawPrintRequest(string PrinterName, string DataBase64, string? DocumentName);

sealed record TsplPrintRequest(string PrinterName, string Tspl, string? Encoding, string? DocumentName);

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

    readonly object gate = new();
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
        lock (gate)
        {
            var start = Math.Max(entries.Count - take, 0);
            return entries
                .Skip(start)
                .ToArray();
        }
    }

    public async ValueTask AppendAsync(PrintLogEntry entry)
    {
        lock (gate)
        {
            entries.Add(entry);
            TrimUnsafe();
        }

        var json = JsonSerializer.Serialize(entry, SerializerOptions);
        await AppendLineAsync(json);
    }

    public async ValueTask ClearAsync()
    {
        lock (gate)
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
            logger.LogWarning(ex, "Failed to load print logs from {LogPath}", filePath);
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
          <title>PrintServer - Monitoramento</title>
          <style>
            :root { color-scheme: light dark; }
            body { font-family: system-ui, Segoe UI, Roboto, Arial, sans-serif; margin: 0; padding: 16px; }
            header { display: flex; gap: 12px; align-items: baseline; flex-wrap: wrap; }
            h1 { margin: 0; font-size: 18px; }
            .muted { opacity: .7; }
            .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
            input { padding: 6px 8px; min-width: 260px; }
            button { padding: 6px 10px; cursor: pointer; }
            table { width: 100%; border-collapse: collapse; margin-top: 12px; }
            th, td { border-bottom: 1px solid rgba(127,127,127,.35); text-align: left; padding: 8px; vertical-align: top; }
            th { position: sticky; top: 0; background: rgba(127,127,127,.12); backdrop-filter: blur(4px); }
            .badge { display: inline-block; padding: 2px 8px; border-radius: 999px; font-size: 12px; border: 1px solid rgba(127,127,127,.35); }
            .ok { color: #0a0; }
            .fail { color: #c00; }
            .grid { display: grid; gap: 12px; grid-template-columns: 1fr; }
            @media (min-width: 900px) { .grid { grid-template-columns: 1fr 1fr; } }
          </style>
        </head>
        <body>
          <header>
            <h1>PrintServer</h1>
            <span class="muted">Monitoramento e logs</span>
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
            <span class="muted" id="lastRefresh"></span>
          </div>

          <div id="content" class="grid"></div>

          <script>
            const state = {
              tab: "logs",
              apiKey: localStorage.getItem("printserver_api_key") || "",
              intervalMs: 2000
            };

            const elApiKey = document.getElementById("apiKey");
            const elAuthStatus = document.getElementById("authStatus");
            const elLastRefresh = document.getElementById("lastRefresh");
            const elContent = document.getElementById("content");

            elApiKey.value = state.apiKey;

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

            async function refresh() {
              const now = new Date();
              elLastRefresh.textContent = "Atualizado em " + now.toLocaleTimeString();

              try {
                if (state.tab === "logs") return await refreshLogs();
                if (state.tab === "jobs") return await refreshJobs();
                if (state.tab === "printers") return await refreshPrinters();
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
