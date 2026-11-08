using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Database filters that apply an <see cref="EmailAccessScope"/>, the query
/// counterparts of <see cref="EmailAccessScope.CanAccess"/> and
/// <see cref="EmailAccessScope.Lists"/>.
/// </summary>
internal static class EmailAccessScopeQueries
{
    /// <summary>Devices the scope may read or change by id.</summary>
    public static IQueryable<EreaderDeviceEntity> AccessibleTo(
        this IQueryable<EreaderDeviceEntity> devices,
        EmailAccessScope scope)
    {
        if (scope.IsAdministrator)
        {
            return devices;
        }

        var userId = scope.UserId;
        return devices.Where(d => d.OwnerUserId != null && d.OwnerUserId == userId);
    }

    /// <summary>Devices the scope lists.</summary>
    public static IQueryable<EreaderDeviceEntity> ListedFor(
        this IQueryable<EreaderDeviceEntity> devices,
        EmailAccessScope scope)
    {
        if (scope.ListsEveryone)
        {
            return devices;
        }

        var userId = scope.UserId;
        return scope.IsAdministrator
            ? devices.Where(d => d.OwnerUserId == null || d.OwnerUserId == userId)
            : devices.Where(d => d.OwnerUserId != null && d.OwnerUserId == userId);
    }

    /// <summary>Deliveries the scope may read or cancel by id.</summary>
    public static IQueryable<ComicEmailDeliveryEntity> AccessibleTo(
        this IQueryable<ComicEmailDeliveryEntity> deliveries,
        EmailAccessScope scope)
    {
        if (scope.IsAdministrator)
        {
            return deliveries;
        }

        var userId = scope.UserId;
        return deliveries.Where(d => d.OwnerUserId != null && d.OwnerUserId == userId);
    }

    /// <summary>Deliveries the scope lists and cancels in bulk.</summary>
    public static IQueryable<ComicEmailDeliveryEntity> ListedFor(
        this IQueryable<ComicEmailDeliveryEntity> deliveries,
        EmailAccessScope scope)
    {
        if (scope.ListsEveryone)
        {
            return deliveries;
        }

        var userId = scope.UserId;
        return scope.IsAdministrator
            ? deliveries.Where(d => d.OwnerUserId == null || d.OwnerUserId == userId)
            : deliveries.Where(d => d.OwnerUserId != null && d.OwnerUserId == userId);
    }
}
