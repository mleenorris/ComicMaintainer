using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicMaintainer.Core.Models.Auth;

/// <summary>
/// A session token (JWT) that was signed out before it expired, by its <c>jti</c>. Sign-in tokens are otherwise valid
/// until they expire, so signing out records the token here until then (see <c>AuthSessionValidator</c>).
/// </summary>
public class RevokedTokenEntity
{
    /// <summary>The token's <c>jti</c> claim.</summary>
    public string TokenId { get; set; } = string.Empty;

    /// <summary>The account the token was issued to.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>When the token expires; the row can be deleted after that.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>When the token was revoked.</summary>
    public DateTime RevokedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Maps <see cref="RevokedTokenEntity"/> to the <c>RevokedTokens</c> table.</summary>
public sealed class RevokedTokenEntityConfiguration : IEntityTypeConfiguration<RevokedTokenEntity>
{
    public void Configure(EntityTypeBuilder<RevokedTokenEntity> entity)
    {
        entity.ToTable("RevokedTokens");
        entity.HasKey(e => e.TokenId);
        entity.Property(e => e.TokenId).HasMaxLength(64);
        entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
        // Expired rows are deleted by expiry.
        entity.HasIndex(e => e.ExpiresAt);
    }
}
