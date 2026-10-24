using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataOil.Client;

namespace DataOil.Samples.Client;

/// <summary>What a run found, for the program to print and a test to check.</summary>
/// <param name="CreatedDatabase">Whether this run created the database (and so dropped it at the end).</param>
/// <param name="Written">How many notes were written.</param>
/// <param name="HighlyRated">The titles the filtered query answered, in its order.</param>
/// <param name="Streamed">How many rows the streamed scan read.</param>
/// <param name="KeywordHit">The title the keyword search ranked first.</param>
/// <param name="NearestHit">The title the vector search ranked nearest.</param>
internal sealed record WalkthroughResult(
    bool CreatedDatabase,
    int Written,
    IReadOnlyList<string> HighlyRated,
    int Streamed,
    string? KeywordHit,
    string? NearestHit);

/// <summary>
/// The sample itself: connect, make a database, write, query, search, and leave nothing behind.
/// </summary>
/// <remarks>
/// <para>
/// Every step is one call on <see cref="DataOilClient"/>, and the statements are the engine's own
/// SQL dialect, so what is read here is what an application would write.
/// </para>
/// <para>
/// <b>The collection is named for this run</b> (<c>SampleNote_</c> and eight hex digits), so the
/// sample never touches a collection somebody already has, and two runs at once do not meet. It is
/// dropped at the end whatever happened in between.
/// </para>
/// <para>
/// <b>The vectors are written by hand</b>, four numbers per note - how much it is about drilling,
/// pipelines, safety and money - so the vector search can be followed by eye and needs no model.
/// A real application stores what its embedding model produced, and searches with the same model's
/// vector for the question.
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

    /// <summary>Runs every step against the database the client names.</summary>
    /// <param name="client">The client, connected to the database the sample works in.</param>
    /// <param name="createDatabase">Whether to create the database when it does not exist.</param>
    /// <param name="output">Where the steps are reported.</param>
    /// <param name="type">The collection to make, or null for one named for this run.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the run found.</returns>
    public static async Task<WalkthroughResult> RunAsync(
        DataOilClient client,
        bool createDatabase,
        TextWriter output,
        string? type = null,
        CancellationToken cancellationToken = default)
    {
        type ??= "SampleNote_" + Guid.NewGuid().ToString("N")[..8];

        // 1. Connect. The schema version is the smallest answer that proves the address, the
        //    credential and the database: a wrong password is 401, a database this caller may not
        //    see is 403 or 404, and either is a typed exception rather than a status code to decode.
        bool created = await ConnectAsync(client, createDatabase, output, cancellationToken).ConfigureAwait(false);

        try
        {
            try
            {
                // 2. A collection with declared properties, a keyword index and a vector index.
                await DefineAsync(client, type, output, cancellationToken).ConfigureAwait(false);

                // 3. Write: three notes in one batch (one transaction: all of them or none), and one
                //    more through the documents route, which answers the record with its identity.
                int written = await WriteAsync(client, type, output, cancellationToken).ConfigureAwait(false);

                // 4. Query: one page into a list, then the same collection streamed two rows a page.
                (List<string> highlyRated, int streamed) = await QueryAsync(client, type, output, cancellationToken).ConfigureAwait(false);

                // 5. Search: by keyword through the full-text index, and by meaning through the vector index.
                (string? keyword, string? nearest) = await SearchAsync(client, type, output, cancellationToken).ConfigureAwait(false);

                return new WalkthroughResult(created, written, highlyRated, streamed, keyword, nearest);
            }
            finally
            {
                // 6. Clean up the collection, even when a step failed.
                await CleanUpAsync(output, $"the collection {type}", async () =>
                {
                    _ = await client.StatementAsync($"DELETE FROM {type}").ConfigureAwait(false);
                    _ = await client.StatementAsync($"DROP TYPE {type}").ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            // ...and the database, but only when this run made it: a database that was there before
            // the sample ran is somebody's, and is never the sample's to drop.
            if (created)
            {
                await CleanUpAsync(output, $"the database {client.Database}, which this run created", async () =>
                    _ = await client.RequestAsync(HttpMethod.Delete, $"api/v2/databases/{client.Database}").ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// One clean-up step, reported rather than thrown when it fails, so a refusal to drop does not
    /// hide the failure that sent the run here.
    /// </summary>
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
            // Creating a database needs the right to: a tenant administrator, or a role holding the
            // tenant's database capability. Anybody else is refused with a 403 that says so.
            _ = await client.RequestAsync(HttpMethod.Post, "api/v2/databases", new { name = client.Database }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"1. Created the database {client.Database}.").ConfigureAwait(false);

            return true;
        }
    }

    private static async Task DefineAsync(DataOilClient client, string type, TextWriter output, CancellationToken cancellationToken)
    {
        string[] statements =
        [
            $"CREATE DOCUMENT TYPE {type}",
            $"CREATE PROPERTY {type}.title STRING",
            $"CREATE PROPERTY {type}.body STRING",
            $"CREATE PROPERTY {type}.rating INTEGER",
            $"CREATE PROPERTY {type}.embedding ARRAY_OF_FLOATS",
            $"CREATE INDEX ON {type} (body) FULL_TEXT",
            $"CREATE INDEX ON {type} (embedding) LSM_VECTOR METADATA {{dimensions: 4, similarity: 'COSINE'}}",
        ];

        foreach (string statement in statements)
        {
            _ = await client.StatementAsync(statement, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await output.WriteLineAsync($"2. Created the collection {type}, with a full-text index on body and a vector index on embedding.").ConfigureAwait(false);
    }

    private static async Task<int> WriteAsync(DataOilClient client, string type, TextWriter output, CancellationToken cancellationToken)
    {
        List<BatchOperation> batch = [];

        foreach ((string title, string body, int rating, float[] vector) in Notes)
        {
            batch.Add(new BatchOperation(
                $"INSERT INTO {type} SET title = :title, body = :body, rating = :rating, embedding = :embedding",
                new { title, body, rating, embedding = vector }));
        }

        _ = await client.BatchAsync(batch, cancellationToken: cancellationToken).ConfigureAwait(false);

        JsonElement record = await client.CreateAsync(
            type,
            new { title = "Rig day rate", body = "The day rate for the rig rises in March.", rating = 2, embedding = new[] { 0.3f, 0.0f, 0.0f, 0.9f } },
            cancellationToken).ConfigureAwait(false);

        await output.WriteLineAsync($"3. Wrote {Notes.Length} notes in one batch, and one more as record {record.GetProperty("rid").GetString()}.").ConfigureAwait(false);

        return Notes.Length + 1;
    }

    private static async Task<(List<string> HighlyRated, int Streamed)> QueryAsync(DataOilClient client, string type, TextWriter output, CancellationToken cancellationToken)
    {
        List<string> highlyRated = [];

        foreach (JsonElement row in await client.StatementAsync(
            $"SELECT title, rating FROM {type} WHERE rating >= :least ORDER BY rating DESC",
            new { least = 4 },
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            highlyRated.Add(row.GetProperty("title").GetString()!);
        }

        // A scan of any size streams: rows are handed over as they arrive, and the cursor in each
        // page's trailer is sent back for the next. Two rows a page here only so the paging shows.
        int streamed = 0;

        await foreach (JsonElement row in client.QueryAsync($"SELECT title FROM {type} ORDER BY title", pageSize: 2, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            _ = row;
            streamed++;
        }

        await output.WriteLineAsync($"4. Rated 4 or more: {string.Join(", ", highlyRated)}. Streamed {streamed} rows two at a time.").ConfigureAwait(false);

        return (highlyRated, streamed);
    }

    private static async Task<(string? Keyword, string? Nearest)> SearchAsync(DataOilClient client, string type, TextWriter output, CancellationToken cancellationToken)
    {
        // Keywords, ranked by BM25: $score is the relevance the index handed back for each record.
        IReadOnlyList<JsonElement> keyword = await client.StatementAsync(
            $"SELECT title, $score AS score FROM {type} WHERE SEARCH_INDEX('{type}[body]', :q) = true ORDER BY score DESC LIMIT 3",
            new { q = "casing cementing" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Meaning: the two notes nearest a question that is mostly about drilling, a little about safety.
        IReadOnlyList<JsonElement> nearest = await client.StatementAsync(
            $"SELECT title, VECTOR_DISTANCE(embedding, :v, 'cosine') AS distance FROM (SELECT expand(vector.neighbors('{type}[embedding]', :v, 2))) ORDER BY distance",
            new { v = new[] { 1.0f, 0.0f, 0.2f, 0.0f } },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string? keywordHit = keyword.Count > 0 ? keyword[0].GetProperty("title").GetString() : null;
        string? nearestHit = nearest.Count > 0 ? nearest[0].GetProperty("title").GetString() : null;

        await output.WriteLineAsync($"5. Keyword search for 'casing cementing' ranked first: {keywordHit ?? "(nothing)"}. Nearest by meaning: {nearestHit ?? "(nothing)"}.").ConfigureAwait(false);

        return (keywordHit, nearestHit);
    }
}
