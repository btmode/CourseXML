using CourseXML_main.CourseXML.Services;

var builder = WebApplication.CreateBuilder(args);

// Добавляем поддержку SignalR с правильными настройками
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
}).AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.PropertyNamingPolicy = null;
});

builder.Services.AddControllersWithViews();

// Регистрируем сервисы
builder.Services.AddSingleton<CurrencyService>();
builder.Services.AddSingleton<CurrencyHub>();

// Если работаете только локально - CORS не нужен
// builder.Services.AddCors(); // Убрать или закомментировать

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
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

app.Run();