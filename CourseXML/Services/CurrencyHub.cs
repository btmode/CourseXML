using Microsoft.AspNetCore.SignalR;

public class CurrencyHub : Hub
{
    private readonly ILogger<CurrencyHub> _logger;
    private static readonly Dictionary<string, string> _userGroups = new();

    public CurrencyHub(ILogger<CurrencyHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var connectionId = Context.ConnectionId;
        _logger.LogInformation($"✅ Клиент подключен: {connectionId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;
        _logger.LogWarning($"❌ Клиент отключен: {connectionId}");

        // Удаляем из групп при отключении
        if (_userGroups.ContainsKey(connectionId))
        {
            var groupName = _userGroups[connectionId];
            await Groups.RemoveFromGroupAsync(connectionId, groupName);
            _userGroups.Remove(connectionId);
            _logger.LogInformation($"Удален из группы {groupName}: {connectionId}");
        }

        await base.OnDisconnectedAsync(exception);
    }

    // Подключиться к группе офиса
    public async Task JoinOfficeGroup(string officeId)
    {
        try
        {
            officeId = officeId.ToLower();
            var connectionId = Context.ConnectionId;

            // Если уже в какой-то группе - выходим из нее
            if (_userGroups.ContainsKey(connectionId))
            {
                var oldGroup = _userGroups[connectionId];
                if (oldGroup != officeId)
                {
                    await Groups.RemoveFromGroupAsync(connectionId, oldGroup);
                }
            }

            // Входим в новую группу
            await Groups.AddToGroupAsync(connectionId, officeId);
            _userGroups[connectionId] = officeId;

            _logger.LogInformation($"👥 Клиент {connectionId} присоединился к группе {officeId}");

            // Отправляем подтверждение
            await Clients.Caller.SendAsync("JoinedGroup", officeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при присоединении к группе");
            throw;
        }
    }

    // Метод для поддержания соединения (ping-pong)
    public async Task<string> Ping()
    {
        var connectionId = Context.ConnectionId;
        _logger.LogDebug($"🏓 Ping от {connectionId}");
        return "pong";
    }

    // Получить список подключенных групп
    public Dictionary<string, string> GetConnectedGroups()
    {
        return _userGroups;
    }
}