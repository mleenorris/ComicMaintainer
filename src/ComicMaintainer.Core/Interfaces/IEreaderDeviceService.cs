using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// CRUD for saved ereader devices and the per-series automatic-delivery
/// subscriptions that target them.
/// </summary>
public interface IEreaderDeviceService
{
    Task<IReadOnlyList<EreaderDeviceDto>> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<EreaderDeviceDto?> GetDeviceAsync(int deviceId, CancellationToken cancellationToken = default);

    /// <summary>Creates a device. Throws <see cref="ArgumentException"/> for invalid input and <see cref="InvalidOperationException"/> when the address already exists.</summary>
    Task<EreaderDeviceDto> CreateDeviceAsync(string name, string emailAddress, string? deliveryFormat, CancellationToken cancellationToken = default);

    /// <summary>Updates a device. Null arguments leave the corresponding field untouched. Returns null when the device does not exist.</summary>
    Task<EreaderDeviceDto?> UpdateDeviceAsync(int deviceId, string? name, string? emailAddress, string? deliveryFormat, CancellationToken cancellationToken = default);

    /// <summary>Deletes a device and its series subscriptions. Returns false when it does not exist.</summary>
    Task<bool> DeleteDeviceAsync(int deviceId, CancellationToken cancellationToken = default);

    /// <summary>Lists all subscriptions, optionally filtered to one series.</summary>
    Task<IReadOnlyList<SeriesEmailSubscriptionDto>> GetSubscriptionsAsync(string? seriesTitle = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the subscription for (series, device). Returns null
    /// when the device does not exist.
    /// </summary>
    Task<SeriesEmailSubscriptionDto?> UpsertSubscriptionAsync(
        string seriesTitle,
        int deviceId,
        string? deliveryFormat,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a subscription by id. Returns false when it does not exist.</summary>
    Task<bool> DeleteSubscriptionAsync(int subscriptionId, CancellationToken cancellationToken = default);
}
