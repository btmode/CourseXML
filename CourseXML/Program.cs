using CourseXML.Models;
using CourseXML_main.CourseXML.Services;
using Microsoft.AspNetCore.Mvc;
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

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Course}/{action=Index}/{id?}");

app.MapGet("/health", () =>
{
    return Results.Json(new
    {
        Status = Results.StatusCode(200),
        Message = "OK",
        Timestamp = DateTime.UtcNow,
        Service = "CourseXML"
    });
});

// Информация о путях
app.MapGet("/debug/DevPaths", (IConfiguration configuration, CurrencyService service) =>
{
    return Results.Json(new
    {
        SourcePath = configuration["LocalPaths:SourceXmlPath"],
        CurrentPath = configuration["LocalPaths:CurrentXmlPath"],
        ArchivePath = configuration["LocalPaths:ArchiveFolder"],
        Timestamp = DateTime.UtcNow
    });
});

// Информация о путях
app.MapGet("/debug/LinuxPaths", (IConfiguration configuration, CurrencyService service) =>
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