using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyProject.Api.Data;

internal sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
#if (UseSqlServer)
    private const string NotDeleted = "[DeletedAt] IS NULL";
#else
    private const string NotDeleted = "\"DeletedAt\" IS NULL";
#endif

    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.HasQueryFilter(SoftDelete.Filter, note => note.DeletedAt == null);
        builder.HasKey(note => note.Id);
        builder.Property(note => note.Id).ValueGeneratedNever();
#if (UseSqlite)
        // SQLite has no row version, so EfNoteStore increments this on every save.
        builder.Property(note => note.Version).IsConcurrencyToken();
#else
        builder.Property(note => note.Version).IsRowVersion();
#endif
        builder.Property(note => note.Title).HasMaxLength(Note.TitleMaxLength);
        builder.Property(note => note.Content).HasMaxLength(Note.ContentMaxLength);
#if (UseAuth)
        builder.Property(note => note.OwnerId).HasMaxLength(450);
        builder.Property(note => note.CreatedBy).HasMaxLength(450);
        builder.Property(note => note.UpdatedBy).HasMaxLength(450);
        builder.Property(note => note.DeletedBy).HasMaxLength(450);
        // Unique among notes that are not deleted, so a deleted note's title can be used again.
        builder.HasIndex(note => new { note.OwnerId, note.Title }).IsUnique().HasFilter(NotDeleted);
        builder.HasIndex(note => new { note.OwnerId, note.CreatedAt });
#else
        // Unique among notes that are not deleted, so a deleted note's title can be used again.
        builder.HasIndex(note => note.Title).IsUnique().HasFilter(NotDeleted);
        builder.HasIndex(note => note.CreatedAt);
#endif
    }
}
