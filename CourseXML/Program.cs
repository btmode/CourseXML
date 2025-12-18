using CourseXML.Models;
using CourseXML_main.CourseXML.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.HandshakeTimeout = TimeSpan.FromSeconds(15);
})
.AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.PropertyNamingPolicy = null;
});

builder.Services.AddControllersWithViews();

// КОНФИГУРАЦИЯ
builder.Services.Configure<CurrencyServiceConfig>(
    builder.Configuration.GetSection("CurrencyService"));

// ВОТ ЭТО ВАЖНО - регистрируем RemoteServerSettings!
builder.Services.Configure<RemoteServerSettings>(
    builder.Configuration.GetSection("RemoteServer"));

// Hosted Service
builder.Services.AddHostedService<CurrencyService>();

// Singleton для доступа из контроллеров
builder.Services.AddSingleton<CurrencyService>(provider =>
{
    var hostedServices = provider.GetServices<IHostedService>();
    var currencyService = hostedServices.OfType<CurrencyService>().FirstOrDefault();

    if (currencyService == null)
    {
        var logger = provider.GetRequiredService<ILogger<CurrencyService>>();
        var hubContext = provider.GetRequiredService<IHubContext<CurrencyHub>>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var config = provider.GetRequiredService<IOptions<CurrencyServiceConfig>>();
        var remoteSettings = provider.GetRequiredService<IOptions<RemoteServerSettings>>(); // ДОБАВИЛИ

        currencyService = new CurrencyService(logger, hubContext, configuration, config, remoteSettings);
    }

    return currencyService;
});

// Kestrel
builder.WebHost.UseUrls("http://0.0.0.0:5050");

var app = builder.Build();

// Middleware
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

// CORS
app.UseCors(builder => builder
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

// SignalR
app.MapHub<CurrencyHub>("/currencyHub");

// MVC
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Course}/{action=Index}/{id?}");


app.MapGet("/debug/force-update", async (CurrencyService service) =>
{
    var result = await service.ForceUpdateFromSourceAsync();
    return Results.Json(new
    {
        Success = result,
        Message = result ? "Update successful" : "No changes detected",
        Timestamp = DateTime.UtcNow
    });
});

app.MapGet("/health", () =>
{
    return Results.Json(new
    {
        Status = "OK",
        Timestamp = DateTime.UtcNow,
        Service = "CourseXML"
    });
});

app.MapGet("/debug/simulate-update", async (CurrencyService service, IHubContext<CurrencyHub> hubContext) =>
{
    try
    {
        var office = service.GetOffice("tlt");
        if (office != null)
        {
            var random = new Random();
            var updatedCurrencies = office.Currencies.Select(c => new
            {
                c.Name,
                Purchase = Math.Round(c.Purchase + (decimal)(random.NextDouble() * 0.1 - 0.05), 2),
                Sale = Math.Round(c.Sale + (decimal)(random.NextDouble() * 0.1 - 0.05), 2)
            }).ToList();

            var payload = new
            {
                office.Id,
                office.Location,
                Currencies = updatedCurrencies,
                UpdateTime = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
                Simulated = true
            };

            await hubContext.Clients.Group(office.Id.ToLower()).SendAsync("ReceiveUpdate", payload);

            return Results.Json(new
            {
                Success = true,
                Message = "Simulated update sent",
                Office = office.Id,
                Currencies = updatedCurrencies
            });
        }

        return Results.Json(new
        {
            Success = false,
            Message = "Office not found"
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            Success = false,
            Message = ex.Message
        });
    }
});

// Endpoint для тестирования удаленного подключения
app.MapGet("/debug/test-remote", (IOptions<RemoteServerSettings> remoteSettings) =>
{
    var settings = remoteSettings.Value;
    return Results.Json(new
    {
        Host = settings.Host,
        Username = settings.Username,
        RemoteDirectory = settings.RemoteDirectory,
        FileName = settings.FileName,
        Port = settings.Port,
        UseSshKey = settings.UseSshKey,
        FullRemotePath = settings.FullRemotePath,
        ConnectionString = settings.SshConnectionString
    });
});

// Проверка SSH подключения
app.MapGet("/debug/test-ssh", async (IOptions<RemoteServerSettings> remoteSettings, ILogger<Program> logger) =>
{
    try
    {
        var settings = remoteSettings.Value;

        if (string.IsNullOrEmpty(settings.Host))
        {
            return Results.Json(new { Success = false, Message = "Host not configured" });
        }

        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "ssh";
        process.StartInfo.Arguments = $"-o StrictHostKeyChecking=no -o ConnectTimeout=5 {settings.Username}@{settings.Host} echo 'SSH test successful'";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;

        process.Start();
        await process.WaitForExitAsync();

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        return Results.Json(new
        {
            Success = process.ExitCode == 0,
            ExitCode = process.ExitCode,
            Output = output,
            Error = error
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "SSH test failed");
        return Results.Json(new { Success = false, Message = ex.Message });
    }
});

app.Run();