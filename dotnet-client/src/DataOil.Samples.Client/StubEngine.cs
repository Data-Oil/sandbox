using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataOil.Samples.Client;

/// <summary>
/// A stand-in for the engine, for the dry run and the tests: it answers the requests this sample
/// sends, in the shapes the engine answers them, and sends nothing anywhere.
/// </summary>
/// <remarks>
/// <para>
/// It is not an engine. It keeps the notes the sample writes and works out the sample's own
/// questions from them - a rating filter, a word match for the keyword search, cosine distance for
/// the vector search - so a dry run prints answers that follow from what was written rather than a
/// script. Any statement it does not recognise is a 400, so a sample that drifts from what the stub
/// knows fails its test instead of passing on a guess.
/// </para>
/// <para>
/// The shapes are the engine's: NDJSON rows with a <c>{"returned", "more", "cursor"}</c> trailer, a
/// 404 problem document for a database that does not exist, 201 for a created database or record.
/// </para>
/// </remarks>
internal sealed class StubEngine : HttpMessageHandler
{
    private readonly List<Dictionary<string, JsonElement>> notes = [];
    private readonly Dictionary<string, int> cursors = new(StringComparer.Ordinal);
    private bool databaseExists;
    private int schemaVersion;

    /// <summary>Initialises a stub, with or without the database already there.</summary>
    /// <param name="databaseExists">Whether the database exists before the sample runs.</param>
    public StubEngine(bool databaseExists = false) => this.databaseExists = databaseExists;

    /// <summary>Gets every request the sample sent, as <c>METHOD path</c>, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Gets whether every request carried the two tenancy headers and a credential.</summary>
    public bool EveryRequestCarriedTheHeaders { get; private set; } = true;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        string method = request.Method.Method;
        Requests.Add($"{method} {path}");

        EveryRequestCarriedTheHeaders &= request.Headers.Contains("X-Job-ID")
            && request.Headers.Contains("X-Database")
            && request.Headers.Authorization is not null;

        JsonElement body = request.Content is null
            ? default
            : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).RootElement.Clone();

        if (method == "POST" && path == "/api/v2/databases")
        {
            databaseExists = true;

            return Json(HttpStatusCode.Created, new { name = body.GetProperty("name").GetString() });
        }

        if (!databaseExists)
        {
            return Problem(HttpStatusCode.NotFound, "DatabaseNotFound", "There is no database by that name that this caller may see.");
        }

        if (method == "DELETE" && path.StartsWith("/api/v2/databases/", StringComparison.Ordinal) && path.Count(c => c == '/') == 4)
        {
            databaseExists = false;

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (method == "GET" && path.EndsWith("/schema/version", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, new { version = schemaVersion });
        }

        if (method == "POST" && path.EndsWith("/batch", StringComparison.Ordinal))
        {
            List<object> results = [];

            foreach (JsonElement operation in body.GetProperty("operations").EnumerateArray())
            {
                results.Add(Insert(operation.GetProperty("params")));
            }

            return Json(HttpStatusCode.OK, new { results });
        }

        if (method == "POST" && path.EndsWith("/documents", StringComparison.Ordinal))
        {
            string rid = Insert(body.GetProperty("properties"));

            return Json(HttpStatusCode.Created, new { rid, type = body.GetProperty("type").GetString(), properties = body.GetProperty("properties") });
        }

        if (method == "POST" && path.EndsWith("/query", StringComparison.Ordinal))
        {
            return Statement(body);
        }

        return Problem(HttpStatusCode.NotFound, "RouteNotFound", $"The stub does not answer {method} {path}.");
    }

    private HttpResponseMessage Statement(JsonElement body)
    {
        string sql = body.GetProperty("query").GetString()!;
        JsonElement parameters = body.TryGetProperty("params", out JsonElement p) ? p : default;

        if (sql.StartsWith("CREATE ", StringComparison.Ordinal) || sql.StartsWith("DROP ", StringComparison.Ordinal))
        {
            schemaVersion++;

            return Rows([new Dictionary<string, object?> { ["changed"] = true }]);
        }

        if (sql.StartsWith("DELETE FROM ", StringComparison.Ordinal))
        {
            int count = notes.Count;
            notes.Clear();

            return Rows([new Dictionary<string, object?> { ["count"] = count }]);
        }

        if (sql.Contains("SEARCH_INDEX(", StringComparison.Ordinal))
        {
            string[] words = parameters.GetProperty("q").GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return Rows(notes
                .Select(note => (note, score: words.Count(word => Text(note, "body").Contains(word, StringComparison.OrdinalIgnoreCase))))
                .Where(scored => scored.score > 0)
                .OrderByDescending(scored => scored.score)
                .Take(3)
                .Select(scored => new Dictionary<string, object?> { ["title"] = Text(scored.note, "title"), ["score"] = (double)scored.score })
                .ToList());
        }

        if (sql.Contains("vector.neighbors(", StringComparison.Ordinal))
        {
            float[] question = parameters.GetProperty("v").EnumerateArray().Select(n => n.GetSingle()).ToArray();

            return Rows(notes
                .Select(note => (note, distance: 1 - Cosine(question, note["embedding"].EnumerateArray().Select(n => n.GetSingle()).ToArray())))
                .OrderBy(scored => scored.distance)
                .Take(2)
                .Select(scored => new Dictionary<string, object?> { ["title"] = Text(scored.note, "title"), ["distance"] = Math.Round(scored.distance, 6) })
                .ToList());
        }

        if (sql.Contains("WHERE rating >= :least", StringComparison.Ordinal))
        {
            int least = parameters.GetProperty("least").GetInt32();

            return Rows(notes
                .Where(note => note["rating"].GetInt32() >= least)
                .OrderByDescending(note => note["rating"].GetInt32())
                .Select(note => new Dictionary<string, object?> { ["title"] = Text(note, "title"), ["rating"] = note["rating"].GetInt32() })
                .ToList());
        }

        if (sql.Contains("ORDER BY title", StringComparison.Ordinal))
        {
            // Paged as the engine pages: a page of pageSize rows, and a cursor while rows remain.
            int size = body.TryGetProperty("pageSize", out JsonElement s) ? s.GetInt32() : 500;
            int from = body.TryGetProperty("cursor", out JsonElement c) && cursors.TryGetValue(c.GetString()!, out int at) ? at : 0;
            List<Dictionary<string, object?>> page = notes
                .OrderBy(note => Text(note, "title"), StringComparer.Ordinal)
                .Skip(from)
                .Take(size)
                .Select(note => new Dictionary<string, object?> { ["title"] = Text(note, "title") })
                .ToList();
            bool more = from + page.Count < notes.Count;
            string? cursor = null;

            if (more)
            {
                cursor = "stub-" + (from + page.Count).ToString(CultureInfo.InvariantCulture);
                cursors[cursor] = from + page.Count;
            }

            return Rows(page, more, cursor);
        }

        return Problem(HttpStatusCode.BadRequest, "StatementNotRecognised", $"The stub does not know the statement: {sql}");
    }

    private string Insert(JsonElement properties)
    {
        Dictionary<string, JsonElement> note = [];

        foreach (JsonProperty property in properties.EnumerateObject())
        {
            note[property.Name] = property.Value.Clone();
        }

        notes.Add(note);

        return "#12:" + (notes.Count - 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string Text(Dictionary<string, JsonElement> note, string name) => note[name].GetString() ?? string.Empty;

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, left = 0, right = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            left += a[i] * a[i];
            right += b[i] * b[i];
        }

        return dot / (Math.Sqrt(left) * Math.Sqrt(right));
    }

    private static HttpResponseMessage Rows(List<Dictionary<string, object?>> rows, bool more = false, string? cursor = null)
    {
        StringBuilder text = new();

        foreach (Dictionary<string, object?> row in rows)
        {
            _ = text.Append(JsonSerializer.Serialize(row)).Append('\n');
        }

        Dictionary<string, object?> trailer = new() { ["returned"] = rows.Count, ["more"] = more };

        if (cursor is not null)
        {
            trailer["cursor"] = cursor;
        }

        _ = text.Append(JsonSerializer.Serialize(trailer)).Append('\n');

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(text.ToString(), Encoding.UTF8, "application/x-ndjson"),
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Problem(HttpStatusCode status, string code, string detail) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { type = "about:blank", title = code, status = (int)status, detail, errorCode = code }),
                Encoding.UTF8,
                "application/problem+json"),
        };
}
