using CourseXML_main.CourseXML.Services;

var builder = WebApplication.CreateBuilder(args);

// Читаем порт из переменной окружения или конфига
var port = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(':').LastOrDefault()
    ?? builder.Configuration["Kestrel:Endpoints:Http:Url"]?.Split(':').LastOrDefault()
    ?? "5000";

// Добавляем поддержку SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);
}).AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.PropertyNamingPolicy = null;
});

builder.Services.AddControllersWithViews();

// Регистрируем сервисы
builder.Services.AddSingleton<CurrencyService>();
builder.Services.AddSingleton<CurrencyHub>();

// Настраиваем Kestrel
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(int.Parse(port));
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
});

var app = builder.Build();

// Для продакшена используем исключения вместо страницы ошибок
if (!app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage(); // Оставляем для отладки на сервере
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

// SignalR хаб
app.MapHub<CurrencyHub>("/currencyHub");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Course}/{action=Index}/{id?}");

// Обработчик для проверки здоровья
app.MapGet("/health", () => "OK");
app.MapGet("/version", () => "1.0.0");

app.Run();