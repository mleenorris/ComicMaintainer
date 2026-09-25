using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Manages saved ereader devices and per-series automatic-delivery subscriptions.
/// </summary>
public class EreaderDeviceService : IEreaderDeviceService
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;
    private readonly ISeriesMetadataCacheService _seriesCache;
    private readonly ILogger<EreaderDeviceService> _logger;

    public EreaderDeviceService(
        IDbContextFactory<ComicMaintainerDbContext> dbFactory,
        ISeriesMetadataCacheService seriesCache,
        ILogger<EreaderDeviceService> logger)
    {
        _dbFactory = dbFactory;
        _seriesCache = seriesCache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EreaderDeviceDto>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var devices = await db.EreaderDevices
            .AsNoTracking()
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);

        return devices.Select(ToDto).ToList();
    }

    public async Task<EreaderDeviceDto?> GetDeviceAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.EreaderDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        return device is null ? null : ToDto(device);
    }

    public async Task<EreaderDeviceDto> CreateDeviceAsync(
        string name,
        string emailAddress,
        string? deliveryFormat,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        var normalizedEmail = EmailAddressUtils.ValidateOrThrow(emailAddress, nameof(emailAddress));
        var format = EmailDeliveryFormat.NormalizeOrThrow(deliveryFormat, EmailDeliveryFormat.Original, nameof(deliveryFormat));

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var exists = await db.EreaderDevices
            .AnyAsync(d => d.EmailAddress == normalizedEmail, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException($"A device with address '{normalizedEmail}' already exists.");
        }

        var entity = new EreaderDeviceEntity
        {
            Name = normalizedName,
            EmailAddress = normalizedEmail,
            DeliveryFormat = format,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.EreaderDevices.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created ereader device {DeviceName}", LoggingHelper.SanitizeForLog(normalizedName));
        return ToDto(entity);
    }

    public async Task<EreaderDeviceDto?> UpdateDeviceAsync(
        int deviceId,
        string? name,
        string? emailAddress,
        string? deliveryFormat,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.EreaderDevices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        if (name is not null)
        {
            entity.Name = NormalizeName(name);
        }

        if (emailAddress is not null)
        {
            var normalizedEmail = EmailAddressUtils.ValidateOrThrow(emailAddress, nameof(emailAddress));
            var conflict = await db.EreaderDevices
                .AnyAsync(d => d.Id != deviceId && d.EmailAddress == normalizedEmail, cancellationToken);
            if (conflict)
            {
                throw new InvalidOperationException($"A device with address '{normalizedEmail}' already exists.");
            }

            entity.EmailAddress = normalizedEmail;
        }

        if (deliveryFormat is not null)
        {
            entity.DeliveryFormat = EmailDeliveryFormat.NormalizeOrThrow(
                deliveryFormat, entity.DeliveryFormat, nameof(deliveryFormat));
        }

        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task<bool> DeleteDeviceAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.EreaderDevices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        // Subscriptions cascade with the device; delivery history is retained on
        // purpose so the user can still see what was sent where.
        db.EreaderDevices.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<SeriesEmailSubscriptionDto>> GetSubscriptionsAsync(
        string? seriesTitle = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var query = from subscription in db.SeriesEmailSubscriptions.AsNoTracking()
                    join device in db.EreaderDevices.AsNoTracking() on subscription.DeviceId equals device.Id
                    select new { subscription, device };

        if (!string.IsNullOrWhiteSpace(seriesTitle))
        {
            var key = _seriesCache.NormalizeKey(seriesTitle);
            query = query.Where(x => x.subscription.NormalizedSeriesKey == key);
        }

        var rows = await query.ToListAsync(cancellationToken);

        return rows
            .OrderBy(x => x.subscription.SeriesTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => ToDto(x.subscription, x.device))
            .ToList();
    }

    public async Task<SeriesEmailSubscriptionDto?> UpsertSubscriptionAsync(
        string seriesTitle,
        int deviceId,
        string? deliveryFormat,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new ArgumentException("Series title is required", nameof(seriesTitle));
        }

        var format = EmailDeliveryFormat.NormalizeSubscriptionOrThrow(deliveryFormat, nameof(deliveryFormat));
        var key = _seriesCache.NormalizeKey(seriesTitle);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var device = await db.EreaderDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var entity = await db.SeriesEmailSubscriptions
            .FirstOrDefaultAsync(s => s.NormalizedSeriesKey == key && s.DeviceId == deviceId, cancellationToken);

        if (entity is null)
        {
            entity = new SeriesEmailSubscriptionEntity
            {
                NormalizedSeriesKey = key,
                SeriesTitle = seriesTitle.Trim(),
                DeviceId = deviceId,
                DeliveryFormat = format,
                Enabled = enabled,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.SeriesEmailSubscriptions.Add(entity);
        }
        else
        {
            entity.SeriesTitle = seriesTitle.Trim();
            entity.DeliveryFormat = format;
            entity.Enabled = enabled;
            entity.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Auto-send for series {SeriesKey} to device {DeviceId} is now {State}",
            LoggingHelper.SanitizeForLog(key),
            deviceId,
            enabled ? "enabled" : "disabled");

        return ToDto(entity, device);
    }

    public async Task<bool> DeleteSubscriptionAsync(int subscriptionId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SeriesEmailSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        db.SeriesEmailSubscriptions.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Device name is required", nameof(name));
        }

        var trimmed = name.Trim();
        return trimmed.Length > 128 ? trimmed[..128] : trimmed;
    }

    private static EreaderDeviceDto ToDto(EreaderDeviceEntity entity) => new(
        entity.Id,
        entity.Name,
        entity.EmailAddress,
        entity.DeliveryFormat,
        entity.CreatedAt,
        entity.UpdatedAt);

    private static SeriesEmailSubscriptionDto ToDto(SeriesEmailSubscriptionEntity subscription, EreaderDeviceEntity device) => new(
        subscription.Id,
        subscription.NormalizedSeriesKey,
        subscription.SeriesTitle,
        device.Id,
        device.Name,
        device.EmailAddress,
        subscription.DeliveryFormat,
        subscription.Enabled,
        subscription.LastSentAt);
}
