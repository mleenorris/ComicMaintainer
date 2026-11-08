namespace ComicMaintainer.Core.Data;

/// <summary>
/// Gives the e-reader devices and email deliveries saved before they had
/// owners to the first administrator, run once by the migration that adds the
/// owner columns.
/// </summary>
/// <remarks>
/// Before devices were per user, every device was shared by the whole
/// instance and managed by its administrators, so the first administrator
/// (the earliest created, active ones first) inherits them, and with them
/// their subscriptions. Each delivery goes to the owner of its device, or to
/// the same administrator when the device has since been deleted. When no
/// administrator exists (for example when Authelia assigns no admin group),
/// the rows stay unowned, which only administrators can reach.
/// </remarks>
public static class EmailOwnershipBackfill
{
    private const string FirstAdministrator = """
        (SELECT u."Id"
         FROM "AspNetUsers" AS u
         INNER JOIN "AspNetUserRoles" AS ur ON ur."UserId" = u."Id"
         INNER JOIN "AspNetRoles" AS r ON r."Id" = ur."RoleId"
         WHERE r."NormalizedName" = 'ADMIN'
         ORDER BY u."IsActive" DESC, u."CreatedAt", u."Id"
         LIMIT 1)
        """;

    /// <summary>The SQLite statements that assign the unowned rows.</summary>
    public const string Sql = $"""
        UPDATE "EreaderDevices"
        SET "OwnerUserId" = {FirstAdministrator}
        WHERE "OwnerUserId" IS NULL;

        UPDATE "ComicEmailDeliveries"
        SET "OwnerUserId" = COALESCE(
            (SELECT d."OwnerUserId" FROM "EreaderDevices" AS d WHERE d."Id" = "ComicEmailDeliveries"."DeviceId"),
            {FirstAdministrator})
        WHERE "OwnerUserId" IS NULL;
        """;
}
