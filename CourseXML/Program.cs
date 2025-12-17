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

// Сервисы
builder.Services.AddSingleton<CurrencyService>();

// Kestrel
builder.WebHost.UseUrls("http://0.0.0.0:5051");

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

app.MapGet("/health", () => "OK");
app.MapGet("/version", () => "1.0.0");

app.Run();
