using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataOil.Client;
using DataOil.Data;
using DataOil.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DataOil.Samples.EntityFrameworkCore;

/// <summary>What the sandbox walk-through found, for the program to print.</summary>
internal sealed record SandboxResult(
    IReadOnlyList<string> Databases,
    IReadOnlyList<string> Collections,
    IReadOnlyList<string> TopGdp,
    int JapanRows,
    IReadOnlyList<string> LongestLived,
    IReadOnlyList<string> Languages,
    string? SaveRefused);

/// <summary>
/// The read-only walk-through of the public sandbox through Entity Framework: LINQ over a typed
/// <c>WorldBankRow</c>, the engine's own statement for what LINQ cannot say, and a
/// <c>SaveChanges</c> the engine refuses because the account is read only.
/// </summary>
/// <remarks>
/// About eight requests: the databases and the schema through <see cref="DataOilClient"/>, which
/// the provider brings with it; the provider's own schema check; three LINQ queries; one passage;
/// the row to change; and the refused update. The sandbox has fair-use limits, so it stays light.
/// </remarks>
internal static class SandboxWalkthrough
{
    /// <summary>Builds the context's options over a connection that sends through <paramref name="http"/>.</summary>
    /// <param name="settings">Where and as whom.</param>
    /// <param name="http">The HTTP client, which carries the trust settings.</param>
    /// <returns>The options.</returns>
    public static DbContextOptions<WorldBankContext> Options(SampleSettings settings, HttpClient http)
    {
        // The connection string names the endpoint, database and account. A value holding ';', '='
        // or a quote must be quoted in a connection string; the builder does that, so the password
        // is given as it is. For the sandbox it reads:
        //   Endpoint=https://sandbox.dataoil.tech;Database=Sandbox;Username=suser;Password='hFn3dh#2%hn8A';JobId=sample-efcore
        DataOilConnectionStringBuilder connection = new()
        {
            Endpoint = settings.Url,
            Database = settings.Database,
            JobId = settings.JobId,
        };

        if (settings.Token is not null)
        {
            connection.Token = settings.Token;
        }
        else
        {
            connection.Username = settings.Username!;
            connection.Password = settings.Password!;
        }

        DataOilConnection opened = new(connection.ConnectionString) { HttpClient = http };

        DbContextOptionsBuilder<WorldBankContext> options = new();
        _ = options.UseDataOil(opened);

        return options.Options;
    }

    /// <summary>What the top-GDP query becomes in the engine's dialect, without sending anything.</summary>
    /// <param name="context">A context over a connection that is never opened.</param>
    /// <returns>The statement.</returns>
    public static string Translation(WorldBankContext context)
    {
        int year = 2015;

        return context.Rows.Where(r => r.Year == year).OrderByDescending(r => r.Gdp).Take(5)
            .Select(r => new { r.Country, r.Gdp }).ToQueryString();
    }

    /// <summary>Runs every step.</summary>
    /// <param name="settings">The sandbox's settings.</param>
    /// <param name="http">The HTTP client every request goes through.</param>
    /// <param name="output">Where the steps are reported.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the run found.</returns>
    public static async Task<SandboxResult> RunAsync(SampleSettings settings, HttpClient http, TextWriter output, CancellationToken cancellationToken = default)
    {
        // 1-2. The provider brings the Data Oil client; use it for what is not a table: the
        //      databases this account sees, and the schema.
        List<string> databases;
        List<string> collections;

        using (DataOilClient client = new(
            new DataOilClientOptions { BaseUrl = settings.Url, Database = settings.Database, Token = settings.Token, Username = settings.Username, Password = settings.Password, JobId = settings.JobId },
            http))
        {
            JsonElement listed = await client.RequestAsync(HttpMethod.Get, "api/v2/databases", cancellationToken: cancellationToken).ConfigureAwait(false);
            databases = [.. listed.GetProperty("databases").EnumerateArray().Select(d => d.GetString()!)];
            await output.WriteLineAsync($"1. Databases: {string.Join(", ", databases)}").ConfigureAwait(false);

            DataOilSchema schema = (await client.GetSchemaAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Schema!;
            collections = [.. schema.Types.Where(t => t.Name is not "V" and not "E").Select(t => t.Name)];
            await output.WriteLineAsync($"2. Collections: {string.Join(", ", collections)}").ConfigureAwait(false);
        }

        await using WorldBankContext context = new(Options(settings, http));

        // 3a. Top GDP in one year. The year is a captured variable, so it is sent as a parameter;
        //     the first query also checks the model against the schema this account may read.
        int year = 2015;
        await output.WriteLineAsync($"3a. Top 5 GDP in {year}:").ConfigureAwait(false);
        var top = await context.Rows.AsNoTracking().Where(r => r.Year == year).OrderByDescending(r => r.Gdp).Take(5)
            .Select(r => new { r.Country, r.Gdp }).ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in top)
        {
            await output.WriteLineAsync($"    {row.Country,-15} {Money(row.Gdp)}").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"    LINQ became: {Translation(context).ReplaceLineEndings(" ")}").ConfigureAwait(false);

        // 3b. One country's series, ordered by year.
        string country = "Japan";
        await output.WriteLineAsync($"3b. {country}, by year:").ConfigureAwait(false);
        var japan = await context.Rows.AsNoTracking().Where(r => r.Country == country).OrderBy(r => r.Year)
            .Select(r => new { r.Year, r.Gdp, r.LifeExpectancy }).ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in japan)
        {
            await output.WriteLineAsync($"    {row.Year}  GDP {Money(row.Gdp)}  life expectancy {row.LifeExpectancy?.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        }

        // 3c. An aggregate: average life expectancy per country.
        await output.WriteLineAsync("3c. Average life expectancy per country (top 5):").ConfigureAwait(false);
        var lived = await context.Rows.AsNoTracking().GroupBy(r => r.Country)
            .Select(g => new { Country = g.Key, Life = g.Average(r => r.LifeExpectancy), N = g.Count() })
            .OrderByDescending(g => g.Life).Take(5).ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in lived)
        {
            await output.WriteLineAsync($"    {row.Country,-15} {(row.Life ?? 0).ToString("F1", CultureInfo.InvariantCulture)}  ({row.N} rows)").ConfigureAwait(false);
        }

        // 4. What LINQ cannot say - a list of maps - goes through context.DataOil(), the engine's own
        //    statement over the same connection: one passage and its translations.
        JsonElement passage = (await context.DataOil().StatementAsync(
            "SELECT chunk_index, content, translations FROM WorldBank_chunks ORDER BY chunk_index LIMIT 1",
            cancellationToken: cancellationToken).ConfigureAwait(false))[0];
        await output.WriteLineAsync($"4. Passage {passage.GetProperty("chunk_index")}: {SecondLine(passage.GetProperty("content"))}").ConfigureAwait(false);
        List<string> languages = [];

        foreach (JsonElement translation in passage.GetProperty("translations").EnumerateArray())
        {
            languages.Add(translation.GetProperty("code").GetString()!);
            await output.WriteLineAsync($"    {translation.GetProperty("code").GetString()}  {SecondLine(translation.GetProperty("content"))}").ConfigureAwait(false);
        }

        // 5. SaveChanges is refused: the account is read only. The update is sent, guarded by the
        //    record's @version, and the engine answers 403.
        WorldBankRow first = await context.Rows.OrderBy(r => r.RowIndex).FirstAsync(cancellationToken).ConfigureAwait(false);
        first.Population = (first.Population ?? 0) + 1;
        string? refusedWith = null;

        try
        {
            _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync("5. SaveChanges was accepted - that should not happen on the sandbox.").ConfigureAwait(false);
        }
        catch (Exception failure) when (Find<DataOilAuthorizationException>(failure) is { } denied)
        {
            refusedWith = $"{(int)denied.Status} {denied.ErrorCode}";
            await output.WriteLineAsync($"5. SaveChanges refused as expected: {(int)denied.Status} {denied.ErrorCode} - {denied.Detail}").ConfigureAwait(false);
        }

        return new SandboxResult(
            databases,
            collections,
            [.. top.Select(row => row.Country)],
            japan.Count,
            [.. lived.Select(row => row.Country)],
            languages,
            refusedWith);
    }

    private static string Money(double? value) => (value ?? 0).ToString("N0", CultureInfo.InvariantCulture).PadLeft(22);

    private static string SecondLine(JsonElement text)
    {
        string[] lines = text.GetString()!.Split('\n');
        string line = lines.Length > 1 ? lines[1] : lines[0];

        return line[..Math.Min(80, line.Length)];
    }

    private static T? Find<T>(Exception failure)
        where T : Exception
    {
        for (Exception? at = failure; at is not null; at = at.InnerException)
        {
            if (at is T found)
            {
                return found;
            }
        }

        return null;
    }
}
