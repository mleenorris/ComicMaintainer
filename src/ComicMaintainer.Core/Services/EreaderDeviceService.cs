using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Manages saved ereader devices and per-series automatic-delivery
/// subscriptions, each visible only within the caller's
/// <see cref="EmailAccessScope"/>.
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

    public async Task<IReadOnlyList<EreaderDeviceDto>> GetDevicesAsync(EmailAccessScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var devices = await db.EreaderDevices
            .AsNoTracking()
            .ListedFor(scope)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);

        return devices.Select(ToDto).ToList();
    }

    public async Task<EreaderDeviceDto?> GetDeviceAsync(EmailAccessScope scope, int deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.EreaderDevices
            .AsNoTracking()
            .AccessibleTo(scope)
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        return device is null ? null : ToDto(device);
    }

    public async Task<EreaderDeviceDto> CreateDeviceAsync(
        EmailAccessScope scope,
        string name,
        string emailAddress,
        string? deliveryFormat,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var ownerUserId = scope.UserId
            ?? throw new ArgumentException("A device can only be saved by a signed-in user.", nameof(scope));

        var normalizedName = NormalizeName(name);
        var normalizedEmail = EmailAddressUtils.ValidateOrThrow(emailAddress, nameof(emailAddress));
        var format = EmailDeliveryFormat.NormalizeOrThrow(deliveryFormat, EmailDeliveryFormat.Original, nameof(deliveryFormat));

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Only the user's own devices: another user's addresses are not theirs to know.
        var exists = await db.EreaderDevices
            .AnyAsync(d => d.OwnerUserId == ownerUserId && d.EmailAddress == normalizedEmail, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException($"A device with address '{normalizedEmail}' already exists.");
        }

        var entity = new EreaderDeviceEntity
        {
            OwnerUserId = ownerUserId,
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
        EmailAccessScope scope,
        int deviceId,
        string? name,
        string? emailAddress,
        string? deliveryFormat,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.EreaderDevices
            .AccessibleTo(scope)
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
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
            var ownerUserId = entity.OwnerUserId;
            var conflict = await db.EreaderDevices
                .AnyAsync(
                    d => d.Id != deviceId && d.OwnerUserId == ownerUserId && d.EmailAddress == normalizedEmail,
                    cancellationToken);
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

    public async Task<bool> DeleteDeviceAsync(EmailAccessScope scope, int deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.EreaderDevices
            .AccessibleTo(scope)
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        // Subscriptions cascade with the device; delivery history is retained on
        // purpose so the user can still see what was sent where. A delivery
        // still pending is cancelled when its turn comes instead of being sent.
        db.EreaderDevices.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<SeriesEmailSubscriptionDto>> GetSubscriptionsAsync(
        EmailAccessScope scope,
        string? seriesTitle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // A subscription belongs to the owner of its device.
        var query = from subscription in db.SeriesEmailSubscriptions.AsNoTracking()
                    join device in db.EreaderDevices.AsNoTracking().ListedFor(scope) on subscription.DeviceId equals device.Id
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
        EmailAccessScope scope,
        string seriesTitle,
        int deviceId,
        string? deliveryFormat,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new ArgumentException("Series title is required", nameof(seriesTitle));
        }

        var format = EmailDeliveryFormat.NormalizeSubscriptionOrThrow(deliveryFormat, nameof(deliveryFormat));
        var key = _seriesCache.NormalizeKey(seriesTitle);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var device = await db.EreaderDevices
            .AsNoTracking()
            .AccessibleTo(scope)
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
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

    public async Task<bool> DeleteSubscriptionAsync(EmailAccessScope scope, int subscriptionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await (
                from subscription in db.SeriesEmailSubscriptions
                join device in db.EreaderDevices.AccessibleTo(scope) on subscription.DeviceId equals device.Id
                where subscription.Id == subscriptionId
                select subscription)
            .FirstOrDefaultAsync(cancellationToken);
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
        entity.UpdatedAt,
        entity.OwnerUserId);

    private static SeriesEmailSubscriptionDto ToDto(SeriesEmailSubscriptionEntity subscription, EreaderDeviceEntity device) => new(
        subscription.Id,
        subscription.NormalizedSeriesKey,
        subscription.SeriesTitle,
        device.Id,
        device.Name,
        device.EmailAddress,
        subscription.DeliveryFormat,
        subscription.Enabled,
        subscription.LastSentAt,
        device.OwnerUserId);
}
