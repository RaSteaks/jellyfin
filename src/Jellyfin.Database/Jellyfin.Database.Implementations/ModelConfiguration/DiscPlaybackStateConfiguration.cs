using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>Configures independent title resume state.</summary>
public sealed class DiscPlaybackStateConfiguration : IEntityTypeConfiguration<DiscPlaybackState>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DiscPlaybackState> builder)
    {
        builder.HasKey(s => new { s.UserId, s.ItemId, s.Title });
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<BaseItemEntity>().WithMany().HasForeignKey(s => s.ItemId).OnDelete(DeleteBehavior.Cascade);
    }
}
