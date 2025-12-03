using Microsoft.AspNetCore.SignalR;

public class CurrencyHub : Hub
{
    private readonly ILogger<CurrencyHub> _logger;

    public CurrencyHub(ILogger<CurrencyHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation($"Клиент подключился: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation($"Клиент отключился: {Context.ConnectionId}");
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinGroup(string officeId)
    {
        if (!string.IsNullOrEmpty(officeId))
        {
            var groupName = officeId.ToLower();
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            _logger.LogInformation($"Клиент {Context.ConnectionId} присоединился к группе: {groupName}");
        }
    }
}