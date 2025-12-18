using CourseXML.Models;
using CourseXML_main.CourseXML.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.Configure<CurrencyServiceConfig>(
    builder.Configuration.GetSection("CurrencyService"));

builder.Services.AddHostedService<CurrencyService>();

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

        currencyService = new CurrencyService(logger, hubContext, configuration, config);
    }

    return currencyService;
});

builder.WebHost.UseUrls("http://0.0.0.0:5050");

var app = builder.Build();

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

app.UseCors(builder => builder
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

app.MapHub<CurrencyHub>("/currencyHub");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Course}/{action=Index}/{id?}");

app.MapGet("/debug/currency", async (CurrencyService service) =>
{
    if (!service.IsInitialized)
    {
        return Results.Json(new
        {
            Status = "NotInitialized",
            Message = "Service is initializing..."
        });
    }

    var office = service.GetOffice("tlt");
    var allOffices = service.GetAllOffices();

    return Results.Json(new
    {
        Status = "OK",
        Initialized = service.IsInitialized,
        OfficesCount = allOffices.Count,
        TLTOffice = office != null ? new
        {
            Location = office.Location,
            CurrenciesCount = office.Currencies.Count
        } : null
    });
});

// Принудительное обновление
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

// Health check
app.MapGet("/health", () =>
{
    return Results.Json(new
    {
        Status = "OK",
        Timestamp = DateTime.UtcNow,
        Service = "CourseXML"
    });
});

// Симуляция изменения курсов для тестов
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

app.MapGet("/debug/signalr-info", () =>
{
    return Results.Json(new
    {
        Message = "SignalR доступен по адресу /currencyHub",
        Endpoints = new[]
        {
            "/currencyHub",
            "/debug/simulate-update",
            "/debug/force-update"
        },
        Timestamp = DateTime.UtcNow
    });
});

// Простой ping-pong для теста SignalR
app.MapGet("/debug/ping", async (IHubContext<CurrencyHub> hubContext) =>
{
    try
    {
        // Отправляем ping всем подключенным клиентам
        await hubContext.Clients.All.SendAsync("KeepAlive", DateTime.UtcNow.ToString("o"));

        return Results.Json(new
        {
            Success = true,
            Message = "Ping отправлен всем клиентам",
            Timestamp = DateTime.UtcNow
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

app.Run();