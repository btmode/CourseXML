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

// Эндпоинты для отладки
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

// Информация о путях
app.MapGet("/debug/paths", (IConfiguration configuration, CurrencyService service) =>
{
    return Results.Json(new
    {
        SourcePath = configuration["LinuxPaths:SourceXmlPath"],
        CurrentPath = configuration["LinuxPaths:CurrentXmlPath"],
        ArchivePath = configuration["LinuxPaths:ArchiveFolder"],
        Timestamp = DateTime.UtcNow
    });
});

app.Run();