using Microsoft.AspNetCore.SignalR;

public class CurrencyHub : Hub
{
    public async Task JoinGroup(string officeId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, officeId);
    }
}