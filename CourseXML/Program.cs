using CourseXML.Models;
using CourseXML_main.CourseXML.Services;

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

// ---------- ИСПРАВЬ ЭТУ ЧАСТЬ ----------
// Было:
// builder.Services.AddSingleton<CurrencyService>();

// Стало:
builder.Services.AddHostedService<CurrencyService>();
builder.Services.AddSingleton<CurrencyService>(provider =>
    provider.GetServices<IHostedService>().OfType<CurrencyService>().First());
// ---------------------------------------

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
app.MapGet("/debug/currency", (CurrencyService service) =>
{
    var office = service.GetOffice("tlt");
    return new
    {
        Office = office?.Location,
        Currencies = office?.Currencies,
        TotalOffices = service.GetType()
            .GetField("_offices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .GetValue(service) is List<CityOffice> offices ? offices.Count : 0
    };
});



app.MapGet("/health", () => "OK");
app.MapGet("/version", () => "1.0.0");

app.Run();