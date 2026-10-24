using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using DataOil.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace DataOil.Samples.EntityFrameworkCore.Tests;

/// <summary>
/// The model and its translations, from a connection that is never opened: no server, no
/// credential, nothing sent anywhere.
/// </summary>
public sealed class DryRunTests
{
    private static NotesContext Context(HttpClient http) => new(Walkthrough.Options(SampleSettings.DryRun, http));

    [Fact]
    public void The_note_is_keyed_by_the_record_identity_and_versioned()
    {
        using HttpClient http = new();
        using NotesContext context = Context(http);

        IEntityType note = Assert.Single(context.Model.GetEntityTypes());

        Assert.Equal("SampleNote", note.GetTableName());
        Assert.Equal("@rid", Assert.Single(note.FindPrimaryKey()!.Properties).GetColumnName());
        Assert.Contains(note.GetProperties(), property => property.GetColumnName() == "@version" && property.IsConcurrencyToken);
        Assert.DoesNotContain(note.GetProperties(), property => property.GetColumnName() == "embedding");
    }

    [Fact]
    public void The_queries_translate_to_the_engine_dialect_without_connecting()
    {
        using HttpClient http = new();
        using NotesContext context = Context(http);

        IReadOnlyList<string> statements = Walkthrough.Translations(context);

        Assert.Contains("FROM `SampleNote`", statements[0], StringComparison.Ordinal);
        Assert.Contains("`Rating` >= 4", statements[0], StringComparison.Ordinal);
        Assert.Contains("ORDER BY `s`.`Rating` DESC", statements[0], StringComparison.Ordinal);
        Assert.Contains("@version", statements[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dry_run_exits_cleanly()
    {
        Assert.Equal(0, await Program.Main(["--dry-run"]));
    }

    [Fact]
    public void No_variables_mean_the_public_sandbox_read_only()
    {
        SampleSettings? settings = SampleSettings.FromEnvironment(_ => null, out IReadOnlyList<string> problems);

        Assert.Empty(problems);
        Assert.NotNull(settings);
        Assert.True(settings.Sandbox);
        Assert.Equal("https://sandbox.dataoil.tech", settings.Url);
        Assert.Equal("Sandbox", settings.Database);
        Assert.Equal("suser", settings.Username);
        Assert.Null(settings.Token);
        Assert.False(settings.CreateDatabase);
    }

    [Fact]
    public void Naming_any_connection_variable_leaves_the_sandbox_and_names_the_rest()
    {
        Dictionary<string, string> environment = new() { ["DATAOIL_TOKEN"] = "dot_placeholder" };

        Assert.Null(SampleSettings.FromEnvironment(name => environment.GetValueOrDefault(name), out IReadOnlyList<string> problems));
        Assert.Equal(2, problems.Count);
        Assert.StartsWith("DATAOIL_URL", problems[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://acme.dataoil.tech", false)]
    [InlineData("https://SANDBOX.dataoil.tech/", true)]
    public void Only_the_sandbox_runs_the_read_only_walkthrough(string url, bool sandbox)
    {
        Dictionary<string, string> environment = new() { ["DATAOIL_URL"] = url, ["DATAOIL_DATABASE"] = "Sandbox", ["DATAOIL_TOKEN"] = "dot_placeholder" };

        SampleSettings? settings = SampleSettings.FromEnvironment(name => environment.GetValueOrDefault(name), out _);

        Assert.NotNull(settings);
        Assert.Equal(sandbox, settings.Sandbox);
    }

    [Fact]
    public void The_sandbox_row_maps_its_bracketed_columns_and_is_keyed_by_the_record_identity()
    {
        using HttpClient http = new();
        using WorldBankContext context = new(SandboxWalkthrough.Options(SampleSettings.FromEnvironment(_ => null, out _)!, http));

        IEntityType row = Assert.Single(context.Model.GetEntityTypes());

        Assert.Equal("WorldBank_rows", row.GetTableName());
        Assert.Equal("@rid", Assert.Single(row.FindPrimaryKey()!.Properties).GetColumnName());
        Assert.Contains(row.GetProperties(), property => property.GetColumnName() == "GDP (USD)");
        Assert.Contains(row.GetProperties(), property => property.GetColumnName() == "CO2 Emissions (metric tons per capita)");
    }

    [Fact]
    public void The_sandbox_query_translates_with_backtick_quoted_columns_without_connecting()
    {
        using HttpClient http = new();
        using WorldBankContext context = new(SandboxWalkthrough.Options(SampleSettings.FromEnvironment(_ => null, out _)!, http));

        string statement = SandboxWalkthrough.Translation(context);

        Assert.Contains("FROM `WorldBank_rows`", statement, StringComparison.Ordinal);
        Assert.Contains("`GDP (USD)`", statement, StringComparison.Ordinal);
        Assert.Contains("DESC", statement, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_take_a_token_and_the_create_switch()
    {
        Dictionary<string, string> environment = new()
        {
            ["DATAOIL_URL"] = "https://acme.dataoil.tech",
            ["DATAOIL_DATABASE"] = "sample_notes",
            ["DATAOIL_TOKEN"] = "dot_placeholder",
            ["DATAOIL_CREATE_DATABASE"] = "true",
        };

        SampleSettings? settings = SampleSettings.FromEnvironment(name => environment.GetValueOrDefault(name), out IReadOnlyList<string> problems);

        Assert.Empty(problems);
        Assert.NotNull(settings);
        Assert.True(settings.CreateDatabase);
        Assert.Equal("sample-efcore", settings.JobId);
    }

    [Fact]
    public void A_connection_string_carries_the_settings_and_not_a_second_credential()
    {
        using HttpClient http = new();
        using NotesContext context = Context(http);

        string connection = context.Database.GetConnectionString()!;

        Assert.Contains("Endpoint=https://engine.invalid", connection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Database=sample_notes", connection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", connection, StringComparison.OrdinalIgnoreCase);
        Assert.True(context.Database.IsDataOil());
    }
}
