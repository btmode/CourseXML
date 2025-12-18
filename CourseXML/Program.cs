using CourseXML.Models;
using CourseXML_main.CourseXML.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);
})
.AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.PropertyNamingPolicy = null;
});

builder.Services.AddControllersWithViews();

// ---------- КОНФИГУРАЦИЯ И РЕГИСТРАЦИЯ СЕРВИСА ----------
// 1. Регистрируем конфигурацию
builder.Services.Configure<CurrencyServiceConfig>(
    builder.Configuration.GetSection("CurrencyService"));

// 2. Регистрируем CurrencyService как HostedService
builder.Services.AddHostedService<CurrencyService>();

// 3. Добавляем синглтон для доступа из контроллеров
builder.Services.AddSingleton<CurrencyService>(provider =>
{
    // Получаем все IHostedService и находим CurrencyService
    var hostedServices = provider.GetServices<IHostedService>();
    var currencyService = hostedServices.OfType<CurrencyService>().FirstOrDefault();

    if (currencyService == null)
    {
        // Если не нашли в IHostedService, создаем новый экземпляр
        var logger = provider.GetRequiredService<ILogger<CurrencyService>>();
        var hubContext = provider.GetRequiredService<IHubContext<CurrencyHub>>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var config = provider.GetRequiredService<IOptions<CurrencyServiceConfig>>();

        currencyService = new CurrencyService(logger, hubContext, configuration, config);
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

// app.UseHttpsRedirection(); // если нет HTTPS
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

// SignalR
app.MapHub<CurrencyHub>("/currencyHub");

// MVC
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Course}/{action=Index}/{id?}");

// Добавь endpoint для проверки работы CurrencyService
app.MapGet("/debug/currency", async (CurrencyService service) =>
{
    // Ждем инициализации сервиса
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
            CurrenciesCount = office.Currencies.Count,
            Currencies = office.Currencies.Select(c => new
            {
                c.Name,
                c.Purchase,
                c.Sale
            })
        } : null,
        AllOffices = allOffices.Select(o => new
        {
            Id = o.Id,
            Location = o.Location,
            CurrenciesCount = o.Currencies.Count
        })
    });
});

app.MapGet("/debug/force-update", async (CurrencyService service) =>
{
    var result = await service.ForceUpdateFromSourceAsync();
    return Results.Json(new
    {
        Success = result,
        Message = result ? "Update successful" : "No changes detected"
    });
});

app.MapGet("/health", () => "OK");
app.MapGet("/version", () => "1.0.0");

// Endpoint для просмотра конфигурации
app.MapGet("/debug/config", (IOptions<CurrencyServiceConfig> config) =>
{
    return Results.Json(config.Value);
});

app.Run();