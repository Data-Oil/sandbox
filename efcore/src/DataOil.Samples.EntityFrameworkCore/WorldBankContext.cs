using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataOil.Samples.EntityFrameworkCore;

/// <summary>One row of the sandbox's World Bank sample table, collection <c>WorldBank_rows</c>.</summary>
/// <remarks>
/// The table's column names have spaces and brackets, so each such property names its column with
/// <c>[Column]</c>, and the provider quotes the names with backticks in the statements it writes.
/// The collection also declares properties this class does not map, such as <c>job_number</c>: a
/// property the model leaves out is simply never read.
/// </remarks>
[Table("WorldBank_rows")]
public sealed class WorldBankRow
{
    /// <summary>Gets or sets the record's identity, <c>@rid</c>.</summary>
    public string? Rid { get; set; }

    /// <summary>Gets or sets the row's number in the file it was loaded from.</summary>
    [Column("row_index")]
    public long RowIndex { get; set; }

    /// <summary>Gets or sets the country.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Gets or sets the year.</summary>
    public long Year { get; set; }

    /// <summary>Gets or sets the GDP in US dollars.</summary>
    [Column("GDP (USD)")]
    public double? Gdp { get; set; }

    /// <summary>Gets or sets the population.</summary>
    public double? Population { get; set; }

    /// <summary>Gets or sets the life expectancy, in years.</summary>
    [Column("Life Expectancy")]
    public double? LifeExpectancy { get; set; }

    /// <summary>Gets or sets the unemployment rate, in percent.</summary>
    [Column("Unemployment Rate (%)")]
    public double? UnemploymentRate { get; set; }

    /// <summary>Gets or sets the CO2 emissions, in metric tons per person.</summary>
    [Column("CO2 Emissions (metric tons per capita)")]
    public double? Co2PerCapita { get; set; }

    /// <summary>Gets or sets the share of people with electricity, in percent.</summary>
    [Column("Access to Electricity (%)")]
    public double? AccessToElectricity { get; set; }
}

/// <summary>A strongly typed context over the sandbox's <c>WorldBank_rows</c>, read only.</summary>
/// <param name="options">The options, from <c>UseDataOil</c>.</param>
public sealed class WorldBankContext(DbContextOptions<WorldBankContext> options) : DbContext(options)
{
    /// <summary>Gets the rows.</summary>
    public DbSet<WorldBankRow> Rows => Set<WorldBankRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<WorldBankRow>(row =>
        {
            // The record's identity is the key; @version is the provider's concurrency token on
            // every update and delete without being mapped here.
            _ = row.Property(held => held.Rid).HasColumnName("@rid");
            _ = row.HasKey(held => held.Rid);
        });
}
