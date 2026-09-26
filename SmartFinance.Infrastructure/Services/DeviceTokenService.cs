using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.DTOs.Notification;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Services;

public class DeviceTokenService : IDeviceTokenService
{
    private readonly SmartFinanceDbContext _context;
    private readonly ICurrentUserService _currentUserService;

    public DeviceTokenService(SmartFinanceDbContext context, ICurrentUserService currentUserService)
    {
        _context = context;
        _currentUserService = currentUserService;
    }

    public async Task RegisterAsync(string token)
    {
        var userId = _currentUserService.UserId;
        var now = DateTime.UtcNow;

        // Token cihaza ait: baska hesapta kayitliysa bu hesaba tasinir.
        var mevcut = await _context.DeviceTokens.FirstOrDefaultAsync(t => t.Token == token);
        if (mevcut == null)
        {
            _context.DeviceTokens.Add(new DeviceToken { UserId = userId, Token = token, LastSeenAt = now });
        }
        else
        {
            mevcut.UserId = userId;
            mevcut.LastSeenAt = now;
        }
        await _context.SaveChangesAsync();

        // Sinir asildiysa en uzun suredir gorulmeyen cihazlar silinir.
        var fazla = await _context.DeviceTokens
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.LastSeenAt)
            .Skip(DeviceTokenLimits.MaxTokensPerUser)
            .ToListAsync();
        if (fazla.Count > 0)
        {
            _context.DeviceTokens.RemoveRange(fazla);
            await _context.SaveChangesAsync();
        }
    }

    public async Task UnregisterAsync(string token)
    {
        var userId = _currentUserService.UserId;
        var kayit = await _context.DeviceTokens.FirstOrDefaultAsync(t => t.Token == token && t.UserId == userId);
        if (kayit == null) return; // yok ya da baskasinin: sessizce gec, var/yok bilgisi sizmasin

        _context.DeviceTokens.Remove(kayit);
        await _context.SaveChangesAsync();
    }
}
