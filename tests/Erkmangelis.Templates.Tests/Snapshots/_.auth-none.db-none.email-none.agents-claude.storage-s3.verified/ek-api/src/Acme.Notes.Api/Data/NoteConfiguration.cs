using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Notes.Api.Data;

internal sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    private const string NotDeleted = "\"DeletedAt\" IS NULL";

    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.HasQueryFilter(SoftDelete.Filter, note => note.DeletedAt == null);
        builder.HasKey(note => note.Id);
        builder.Property(note => note.Id).ValueGeneratedNever();
        builder.Property(note => note.Version).IsRowVersion();
        builder.Property(note => note.Title).HasMaxLength(Note.TitleMaxLength);
        builder.Property(note => note.Content).HasMaxLength(Note.ContentMaxLength);
        // Unique among notes that are not deleted, so a deleted note's title can be used again.
        builder.HasIndex(note => note.Title).IsUnique().HasFilter(NotDeleted);
        builder.HasIndex(note => note.CreatedAt);
    }
}
