using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <inheritdoc />
public sealed class UserVocabularyEntryConfiguration : IEntityTypeConfiguration<UserVocabularyEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserVocabularyEntry> builder)
    {
        builder.HasKey(entry => entry.Id);
        builder.HasIndex(entry => new { entry.UserId, entry.DateCreated });
        builder.HasOne<User>().WithMany().HasForeignKey(entry => entry.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
