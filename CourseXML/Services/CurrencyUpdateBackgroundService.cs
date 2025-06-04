// CurrencyBackgroundService.cs
public class CurrencyBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CurrencyBackgroundService> _logger;

    public CurrencyBackgroundService(
        IServiceProvider services,
        ILogger<CurrencyBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Currency Background Service is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var currencyService = scope.ServiceProvider.GetRequiredService<CurrencyService>();
                await currencyService.CheckAndUpdateRates();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in currency background service");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }

        _logger.LogInformation("Currency Background Service is stopping.");
    }
}