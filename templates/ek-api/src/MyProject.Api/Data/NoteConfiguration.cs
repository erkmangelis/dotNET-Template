using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyProject.Api.Data;

internal sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.HasKey(note => note.Id);
        builder.Property(note => note.Id).ValueGeneratedNever();
        builder.Property(note => note.Title).HasMaxLength(Note.TitleMaxLength);
        builder.Property(note => note.Content).HasMaxLength(Note.ContentMaxLength);
#if (UseIdentity)
        builder.Property(note => note.OwnerId).HasMaxLength(450);
        builder.HasIndex(note => new { note.OwnerId, note.Title }).IsUnique();
        builder.HasIndex(note => new { note.OwnerId, note.CreatedAt });
#else
        builder.HasIndex(note => note.Title).IsUnique();
        builder.HasIndex(note => note.CreatedAt);
#endif
    }
}
