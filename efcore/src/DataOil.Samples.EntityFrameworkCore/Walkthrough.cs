using System;
using System.Collections.Generic;
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

/// <summary>What a run found, for the program to print and a test to check.</summary>
internal sealed record WalkthroughResult(
    bool CreatedDatabase,
    int Written,
    IReadOnlyList<string> HighlyRated,
    int AfterUpdate,
    string? KeywordHit,
    string? NearestHit);

/// <summary>
/// The sample: connect, make a database and a collection, write and query through Entity Framework,
/// search through the engine's own dialect, and leave nothing behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two packages, each for what it is for.</b> The provider maps an application's classes onto a
/// collection that exists; it does not make databases or collections. So the database and the
/// collection are made and dropped with <see cref="DataOilClient"/>, which the provider is built on
/// and brings with it, and everything in between goes through the context.
/// </para>
/// <para>
/// <b>The collection has a fixed name</b>, <c>SampleNote</c>, because a context's model is built
/// once per type. When the database already holds one, the sample stops before touching anything:
/// it is somebody's.
/// </para>
/// </remarks>
internal static class Walkthrough
{
    private static readonly (string Title, string Body, int Rating, float[] Vector)[] Notes =
    [
        ("Casing failure on well 7", "The casing failed at 2300 metres during cementing, and the crew stopped drilling.", 5, [0.9f, 0.1f, 0.4f, 0.0f]),
        ("Pipeline pigging schedule", "Pigging the export pipeline every second week keeps wax out of the line.", 3, [0.0f, 0.9f, 0.1f, 0.1f]),
        ("Permit to work audit", "The audit found two permits signed after the work had started.", 4, [0.1f, 0.1f, 0.9f, 0.1f]),
    ];

    /// <summary>Builds the context's options over a connection that sends through <paramref name="http"/>.</summary>
    /// <param name="settings">Where and as whom.</param>
    /// <param name="http">The HTTP client, which carries the trust settings.</param>
    /// <returns>The options.</returns>
    public static DbContextOptions<NotesContext> Options(SampleSettings settings, HttpClient http)
    {
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

        // A connection the application owns, so it sends through the same HttpClient - and the
        // same certificate authority - as the client that made the collection.
        DataOilConnection opened = new(connection.ConnectionString) { HttpClient = http };

        DbContextOptionsBuilder<NotesContext> options = new();
        _ = options.UseDataOil(opened);

        return options.Options;
    }

    /// <summary>Runs every step.</summary>
    /// <param name="settings">Where and as whom.</param>
    /// <param name="http">The HTTP client every request goes through.</param>
    /// <param name="output">Where the steps are reported.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the run found.</returns>
    public static async Task<WalkthroughResult> RunAsync(SampleSettings settings, HttpClient http, TextWriter output, CancellationToken cancellationToken = default)
    {
        using DataOilClient client = new(
            new DataOilClientOptions
            {
                BaseUrl = settings.Url,
                Database = settings.Database,
                Token = settings.Token,
                Username = settings.Username,
                Password = settings.Password,
                JobId = settings.JobId,
            },
            http);

        // 1. Connect, and create the database when asked to and it is not there.
        bool created = await ConnectAsync(client, settings.CreateDatabase, output, cancellationToken).ConfigureAwait(false);

        try
        {
            // 2. The collection, unless one by that name is already somebody's.
            DataOilSchema schema = (await client.GetSchemaAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Schema!;

            if (schema.Types.Any(type => string.Equals(type.Name, NotesContext.Collection, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"{settings.Database} already has a collection called {NotesContext.Collection}. The sample makes and drops its own, so it will not touch that one.");
            }

            await DefineAsync(client, output, cancellationToken).ConfigureAwait(false);

            try
            {
                await using NotesContext context = new(Options(settings, http));

                // 3. Write through Entity Framework: SaveChanges sends the inserts, and each entity
                //    comes back with the identity the engine gave it.
                List<SampleNote> added = [.. Notes.Select(note => new SampleNote { Title = note.Title, Body = note.Body, Rating = note.Rating })];
                context.Notes.AddRange(added);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                // The vectors go through context.DataOil(): the engine's own statement, over the
                // context's connection, for the property the model does not map. The statement
                // names the record by its identity as the target (UPDATE #12:0 SET ...).
                for (int i = 0; i < added.Count; i++)
                {
                    _ = await context.DataOil().StatementAsync(
                        $"UPDATE {added[i].Rid} SET embedding = :v",
                        new { v = Notes[i].Vector },
                        cancellationToken).ConfigureAwait(false);
                }

                // Each of those updates moved its record's version, so the copies the context is
                // tracking are stale, and saving one would be refused as a concurrency failure -
                // which is the version guard doing its job. Forget them; the next query reads fresh.
                context.ChangeTracker.Clear();

                await output.WriteLineAsync($"3. Saved {added.Count} notes through the context: {string.Join(", ", added.Select(note => note.Rid))}.").ConfigureAwait(false);

                // 4. LINQ, translated to the engine's dialect; then an update, guarded by the record's version.
                List<string> highlyRated = await context.Notes
                    .Where(note => note.Rating >= 4)
                    .OrderByDescending(note => note.Rating)
                    .Select(note => note.Title)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                SampleNote permit = await context.Notes.SingleAsync(note => note.Title == "Permit to work audit", cancellationToken).ConfigureAwait(false);
                permit.Rating = 2;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                int afterUpdate = await context.Notes.CountAsync(note => note.Rating >= 4, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"4. Rated 4 or more: {string.Join(", ", highlyRated)}. After marking the audit down, {afterUpdate} left.").ConfigureAwait(false);

                // 5. Search, which LINQ cannot say, through the same connection.
                (string? keyword, string? nearest) = await SearchAsync(context, output, cancellationToken).ConfigureAwait(false);

                // 6. Delete through the context, each delete guarded by the version it read.
                context.Notes.RemoveRange(await context.Notes.ToListAsync(cancellationToken).ConfigureAwait(false));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync("6. Deleted the notes through the context.").ConfigureAwait(false);

                return new WalkthroughResult(created, added.Count, highlyRated, afterUpdate, keyword, nearest);
            }
            finally
            {
                await CleanUpAsync(output, $"the collection {NotesContext.Collection}", async () =>
                {
                    _ = await client.StatementAsync($"DELETE FROM {NotesContext.Collection}").ConfigureAwait(false);
                    _ = await client.StatementAsync($"DROP TYPE {NotesContext.Collection}").ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            // ...and the database, but only when this run made it: one that was there before is somebody's.
            if (created)
            {
                await CleanUpAsync(output, $"the database {client.Database}, which this run created", async () =>
                    _ = await client.RequestAsync(HttpMethod.Delete, $"api/v2/databases/{client.Database}").ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// What the two LINQ queries become in the engine's dialect, without sending anything: the dry run.
    /// </summary>
    /// <param name="context">A context over a connection that is never opened.</param>
    /// <returns>The statements, one per query.</returns>
    public static IReadOnlyList<string> Translations(NotesContext context) =>
    [
        context.Notes.Where(note => note.Rating >= 4).OrderByDescending(note => note.Rating).Select(note => note.Title).ToQueryString(),
        context.Notes.Where(note => note.Title == "Permit to work audit").ToQueryString(),
    ];

    private static async Task<bool> ConnectAsync(DataOilClient client, bool createDatabase, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            int version = await client.SchemaVersionAsync(cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"1. Connected to {client.Database}: schema version {version}.").ConfigureAwait(false);

            return false;
        }
        catch (DataOilNotFoundException) when (createDatabase)
        {
            // A tenant administrator, or a role holding the tenant's database capability; anybody
            // else is refused with a 403 that says so.
            _ = await client.RequestAsync(HttpMethod.Post, "api/v2/databases", new { name = client.Database }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"1. Created the database {client.Database}.").ConfigureAwait(false);

            return true;
        }
    }

    private static async Task DefineAsync(DataOilClient client, TextWriter output, CancellationToken cancellationToken)
    {
        const string type = NotesContext.Collection;

        // The model's property names are the collection's: Title, Body, Rating. The provider checks
        // the model against these declarations the first time the context reaches the database.
        string[] statements =
        [
            $"CREATE DOCUMENT TYPE {type}",
            $"CREATE PROPERTY {type}.Title STRING",
            $"CREATE PROPERTY {type}.Body STRING",
            $"CREATE PROPERTY {type}.Rating INTEGER",
            $"CREATE PROPERTY {type}.embedding ARRAY_OF_FLOATS",
            $"CREATE INDEX ON {type} (Body) FULL_TEXT",
            $"CREATE INDEX ON {type} (embedding) LSM_VECTOR METADATA {{dimensions: 4, similarity: 'COSINE'}}",
        ];

        foreach (string statement in statements)
        {
            _ = await client.StatementAsync(statement, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await output.WriteLineAsync($"2. Created the collection {type}, with a full-text index on Body and a vector index on embedding.").ConfigureAwait(false);
    }

    private static async Task<(string? Keyword, string? Nearest)> SearchAsync(NotesContext context, TextWriter output, CancellationToken cancellationToken)
    {
        const string type = NotesContext.Collection;

        IReadOnlyList<JsonElement> keyword = await context.DataOil().StatementAsync(
            $"SELECT Title, $score AS score FROM {type} WHERE SEARCH_INDEX('{type}[Body]', :q) = true ORDER BY score DESC LIMIT 3",
            new { q = "casing cementing" },
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<JsonElement> nearest = await context.DataOil().StatementAsync(
            $"SELECT Title, VECTOR_DISTANCE(embedding, :v, 'cosine') AS distance FROM (SELECT expand(vector.neighbors('{type}[embedding]', :v, 2))) ORDER BY distance",
            new { v = new[] { 1.0f, 0.0f, 0.2f, 0.0f } },
            cancellationToken).ConfigureAwait(false);

        string? keywordHit = keyword.Count > 0 ? keyword[0].GetProperty("Title").GetString() : null;
        string? nearestHit = nearest.Count > 0 ? nearest[0].GetProperty("Title").GetString() : null;

        await output.WriteLineAsync($"5. Keyword search for 'casing cementing' ranked first: {keywordHit ?? "(nothing)"}. Nearest by meaning: {nearestHit ?? "(nothing)"}.").ConfigureAwait(false);

        return (keywordHit, nearestHit);
    }

    private static async Task CleanUpAsync(TextWriter output, string what, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
            await output.WriteLineAsync($"6. Dropped {what}.").ConfigureAwait(false);
        }
        catch (DataOilException failure)
        {
            await output.WriteLineAsync($"6. Could not drop {what}: {failure.ErrorCode} {failure.Detail}").ConfigureAwait(false);
        }
    }
}
