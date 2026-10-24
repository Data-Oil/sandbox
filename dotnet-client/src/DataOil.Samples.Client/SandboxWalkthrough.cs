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

namespace DataOil.Samples.Client;

/// <summary>What the sandbox walk-through found, for the program to print and a test to check.</summary>
/// <param name="Databases">The databases the account sees.</param>
/// <param name="Collections">The collections it may read, without the base types <c>V</c> and <c>E</c>.</param>
/// <param name="Columns">The properties of <c>WorldBank_rows</c>.</param>
/// <param name="TopGdp">The countries the top-GDP query answered, in its order.</param>
/// <param name="JapanRows">How many rows the streamed series read.</param>
/// <param name="LongestLived">The countries the aggregate answered, in its order.</param>
/// <param name="Languages">The language tags of the passage's translations.</param>
/// <param name="WriteRefused">The status and code the refused write carried, or null when it was accepted.</param>
internal sealed record SandboxResult(
    IReadOnlyList<string> Databases,
    IReadOnlyList<string> Collections,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> TopGdp,
    int JapanRows,
    IReadOnlyList<string> LongestLived,
    IReadOnlyList<string> Languages,
    string? WriteRefused);

/// <summary>
/// The read-only walk-through of the public sandbox: what its account can do, and the write it cannot.
/// </summary>
/// <remarks>
/// Seven requests: the databases, the schema, three queries over the World Bank sample table, one
/// passage with its translations, and a write the engine refuses with 403 <c>Security</c>. The
/// sandbox has fair-use limits, so the walk-through stays light.
/// </remarks>
internal static class SandboxWalkthrough
{
    /// <summary>Runs every step.</summary>
    /// <param name="client">The client, signed in to the sandbox.</param>
    /// <param name="output">Where the steps are reported.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the run found.</returns>
    public static async Task<SandboxResult> RunAsync(DataOilClient client, TextWriter output, CancellationToken cancellationToken = default)
    {
        // 1. The databases this account can see. RequestAsync reaches any route the client has no
        //    method for, with the headers added and a refusal turned into an exception.
        JsonElement listed = await client.RequestAsync(HttpMethod.Get, "api/v2/databases", cancellationToken: cancellationToken).ConfigureAwait(false);
        List<string> databases = [.. listed.GetProperty("databases").EnumerateArray().Select(d => d.GetString()!)];
        await output.WriteLineAsync($"1. Databases: {string.Join(", ", databases)}").ConfigureAwait(false);

        // 2. The schema, typed: the collections (types) this account may read, with their properties.
        DataOilSchema schema = (await client.GetSchemaAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Schema!;
        List<DataOilSchemaType> types = [.. schema.Types.Where(type => type.Name is not "V" and not "E")];
        DataOilSchemaType rowsType = types.Single(type => type.Name == "WorldBank_rows");
        await output.WriteLineAsync($"2. Collections: {string.Join(", ", types.Select(type => type.Name))}").ConfigureAwait(false);
        await output.WriteLineAsync($"   WorldBank_rows: {string.Join(", ", rowsType.Properties.Select(p => $"{p.Name} {p.Type}"))}").ConfigureAwait(false);

        // 3a. Top GDP in one year. Column names with spaces or brackets are quoted with backticks;
        //     values are passed as :name parameters, never pasted into the statement.
        await output.WriteLineAsync("3a. Top 5 GDP in 2015:").ConfigureAwait(false);
        List<string> topGdp = [];

        foreach (JsonElement row in await client.StatementAsync(
            "SELECT Country, `GDP (USD)` AS gdp FROM WorldBank_rows WHERE Year = :year ORDER BY `GDP (USD)` DESC LIMIT 5",
            new { year = 2015 },
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            topGdp.Add(row.GetProperty("Country").GetString()!);
            await output.WriteLineAsync($"    {row.GetProperty("Country").GetString(),-15} {Money(row.GetProperty("gdp"))}").ConfigureAwait(false);
        }

        // 3b. One country's series. QueryAsync streams the rows as they arrive and follows the
        //     cursor across pages, so it suits a large result as well as a small one.
        await output.WriteLineAsync("3b. Japan, by year:").ConfigureAwait(false);
        int japanRows = 0;

        await foreach (JsonElement row in client.QueryAsync(
            "SELECT Year, `GDP (USD)` AS gdp, `Life Expectancy` AS life FROM WorldBank_rows WHERE Country = :country ORDER BY Year",
            new { country = "Japan" },
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            japanRows++;
            await output.WriteLineAsync($"    {row.GetProperty("Year")}  GDP {Money(row.GetProperty("gdp"))}  life expectancy {row.GetProperty("life")}").ConfigureAwait(false);
        }

        // 3c. An aggregate: average life expectancy per country.
        await output.WriteLineAsync("3c. Average life expectancy per country (top 5):").ConfigureAwait(false);
        List<string> longestLived = [];

        foreach (JsonElement row in await client.StatementAsync(
            "SELECT Country, avg(`Life Expectancy`) AS life, count(*) AS n FROM WorldBank_rows GROUP BY Country ORDER BY life DESC LIMIT 5",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            longestLived.Add(row.GetProperty("Country").GetString()!);
            string life = row.GetProperty("life").GetDouble().ToString("F1", CultureInfo.InvariantCulture);
            await output.WriteLineAsync($"    {row.GetProperty("Country").GetString(),-15} {life}  ({row.GetProperty("n")} rows)").ConfigureAwait(false);
        }

        // 4. One passage of the table's description, with the translations made when it was loaded.
        //    Its second line lists the column names; show it in each language.
        JsonElement passage = (await client.StatementAsync(
            "SELECT chunk_index, content, translations FROM WorldBank_chunks ORDER BY chunk_index LIMIT 1",
            cancellationToken: cancellationToken).ConfigureAwait(false))[0];
        await output.WriteLineAsync($"4. Passage {passage.GetProperty("chunk_index")}: {SecondLine(passage.GetProperty("content"))}").ConfigureAwait(false);
        List<string> languages = [];

        if (passage.TryGetProperty("translations", out JsonElement translations) && translations.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement translation in translations.EnumerateArray())
            {
                // code is the language tag: <2es>, <2fr>, <2it>, <2ja>, <2zh>.
                languages.Add(translation.GetProperty("code").GetString()!);
                await output.WriteLineAsync($"    {translation.GetProperty("code").GetString()}  {SecondLine(translation.GetProperty("content"))}").ConfigureAwait(false);
            }
        }

        // 5. A write is refused: the account is read only. The client throws a typed exception
        //    carrying the engine's status, stable error code and correlation id.
        string? refusedWith = null;

        try
        {
            _ = await client.StatementAsync("INSERT INTO WorldBank_rows SET Country = 'Atlantis', Year = 2020", cancellationToken: cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync("5. The write was accepted - that should not happen on the sandbox.").ConfigureAwait(false);
        }
        catch (DataOilAuthorizationException refused)
        {
            refusedWith = $"{(int)refused.Status} {refused.ErrorCode}";
            await output.WriteLineAsync($"5. Write refused as expected: {(int)refused.Status} {refused.ErrorCode} - {refused.Detail}").ConfigureAwait(false);
        }

        return new SandboxResult(
            databases,
            [.. types.Select(type => type.Name)],
            [.. rowsType.Properties.Select(p => p.Name)],
            topGdp,
            japanRows,
            longestLived,
            languages,
            refusedWith);
    }

    private static string Money(JsonElement value) => value.GetDouble().ToString("N0", CultureInfo.InvariantCulture).PadLeft(22);

    private static string SecondLine(JsonElement text)
    {
        string[] lines = text.GetString()!.Split('\n');
        string line = lines.Length > 1 ? lines[1] : lines[0];

        return line[..Math.Min(80, line.Length)];
    }
}
