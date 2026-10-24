using Microsoft.EntityFrameworkCore;

namespace DataOil.Samples.EntityFrameworkCore;

/// <summary>One note, as the application sees it.</summary>
/// <remarks>
/// The collection also declares <c>embedding</c>, which this class does not map: a declared property
/// the model leaves out is data the application did not ask for, and is never read. The vector is
/// written and searched through <c>context.DataOil()</c>, the engine's own dialect over the
/// context's connection.
/// </remarks>
public sealed class SampleNote
{
    /// <summary>Gets or sets the record's identity, <c>@rid</c>: null until the engine assigns it.</summary>
    public string? Rid { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the text the keyword search reads.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Gets or sets a rating from 1 to 5.</summary>
    public int Rating { get; set; }
}

/// <summary>An ordinary strongly typed context over the <c>SampleNote</c> collection.</summary>
/// <param name="options">The options, from <c>UseDataOil</c>.</param>
public sealed class NotesContext(DbContextOptions<NotesContext> options) : DbContext(options)
{
    /// <summary>The collection the sample makes, writes and drops.</summary>
    public const string Collection = "SampleNote";

    /// <summary>Gets the notes.</summary>
    public DbSet<SampleNote> Notes => Set<SampleNote>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<SampleNote>(note =>
        {
            // The record's identity is the key; @version, the record's version, is the provider's
            // concurrency token on every update and delete without being mapped here.
            _ = note.ToTable(Collection);
            _ = note.Property(held => held.Rid).HasColumnName("@rid");
            _ = note.HasKey(held => held.Rid);
        });
}
