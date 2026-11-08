using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// CRUD for saved ereader devices and the per-series automatic-delivery
/// subscriptions that target them.
/// </summary>
/// <remarks>
/// Devices are private to the user who saved them, and a device's
/// subscriptions belong to the same user. Every operation takes the
/// <see cref="EmailAccessScope"/> of the caller: a device or subscription the
/// scope cannot reach is reported exactly like one that does not exist.
/// </remarks>
public interface IEreaderDeviceService
{
    /// <summary>Lists the devices <paramref name="scope"/> lists (see <see cref="EmailAccessScope.Lists"/>).</summary>
    Task<IReadOnlyList<EreaderDeviceDto>> GetDevicesAsync(EmailAccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>Returns the device, or null when it does not exist or <paramref name="scope"/> cannot reach it.</summary>
    Task<EreaderDeviceDto?> GetDeviceAsync(EmailAccessScope scope, int deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a device owned by the user of <paramref name="scope"/>. Throws
    /// <see cref="ArgumentException"/> for invalid input and
    /// <see cref="InvalidOperationException"/> when that user already has a
    /// device with the address.
    /// </summary>
    Task<EreaderDeviceDto> CreateDeviceAsync(EmailAccessScope scope, string name, string emailAddress, string? deliveryFormat, CancellationToken cancellationToken = default);

    /// <summary>Updates a device. Null arguments leave the corresponding field untouched. Returns null when the device does not exist or <paramref name="scope"/> cannot reach it.</summary>
    Task<EreaderDeviceDto?> UpdateDeviceAsync(EmailAccessScope scope, int deviceId, string? name, string? emailAddress, string? deliveryFormat, CancellationToken cancellationToken = default);

    /// <summary>Deletes a device and its series subscriptions. Returns false when it does not exist or <paramref name="scope"/> cannot reach it.</summary>
    Task<bool> DeleteDeviceAsync(EmailAccessScope scope, int deviceId, CancellationToken cancellationToken = default);

    /// <summary>Lists the subscriptions of the devices <paramref name="scope"/> lists, optionally filtered to one series.</summary>
    Task<IReadOnlyList<SeriesEmailSubscriptionDto>> GetSubscriptionsAsync(EmailAccessScope scope, string? seriesTitle = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the subscription for (series, device). Returns null
    /// when the device does not exist or <paramref name="scope"/> cannot reach it.
    /// </summary>
    Task<SeriesEmailSubscriptionDto?> UpsertSubscriptionAsync(
        EmailAccessScope scope,
        string seriesTitle,
        int deviceId,
        string? deliveryFormat,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a subscription by id. Returns false when it does not exist or <paramref name="scope"/> cannot reach its device.</summary>
    Task<bool> DeleteSubscriptionAsync(EmailAccessScope scope, int subscriptionId, CancellationToken cancellationToken = default);
}
