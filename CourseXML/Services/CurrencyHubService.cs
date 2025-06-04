using Microsoft.AspNetCore.SignalR;

public class CurrencyHubService : Hub
{
    public async Task JoinGroup(string officeId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, officeId);
    }
}