using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataOil.Samples.Client;

/// <summary>
/// A stand-in for the public sandbox, for the tests: a few World Bank rows and one passage with its
/// translations, for a read-only account - every write is refused with 403 <c>Security</c>, as the
/// sandbox refuses it. Nothing is sent anywhere.
/// </summary>
/// <remarks>
/// Like <see cref="StubEngine"/> it recognises only the statements the sample sends; anything else
/// is a 400, so a sample that drifts from what the stub knows fails its test rather than passing on
/// a guess.
/// </remarks>
internal sealed partial class SandboxStub : HttpMessageHandler
{
    private static readonly (string Country, int Year, double Gdp, double Life)[] Rows =
    [
        ("Italy", 2015, 19900750000000, 71.2),
        ("Nigeria", 2015, 15033350000000, 55.0),
        ("India", 2015, 13691740000000, 69.5),
        ("Japan", 2010, 8351720000000, 83.5),
        ("Japan", 2015, 8922760000000, 68.7),
        ("Turkey", 2012, 5120000000000, 70.6),
    ];

    private static readonly (string Name, string Type)[] Columns =
    [
        ("row_index", "LONG"), ("Country", "STRING"), ("Year", "LONG"), ("GDP (USD)", "DOUBLE"), ("Population", "DOUBLE"),
        ("Life Expectancy", "DOUBLE"), ("Unemployment Rate (%)", "DOUBLE"), ("CO2 Emissions (metric tons per capita)", "DOUBLE"),
        ("Access to Electricity (%)", "DOUBLE"),
    ];

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

        if (method == "GET" && path == "/api/v2/databases")
        {
            return Json(new { databases = new[] { "Sandbox" } });
        }

        if (method == "GET" && path.EndsWith("/schema", StringComparison.Ordinal))
        {
            string[] names = ["V", "E", "Glossary", "WorldBank", "WorldBank_rows", "WorldBank_chunks"];

            return Json(new
            {
                version = 1,
                types = names.Select(name => new
                {
                    name,
                    kind = "document",
                    properties = Columns.Where(_ => name == "WorldBank_rows").Select(column => new { name = column.Name, type = column.Type }).ToArray(),
                }),
            });
        }

        if (method == "POST" && path.EndsWith("/query", StringComparison.Ordinal) && request.Content is not null)
        {
            JsonElement body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).RootElement.Clone();

            return Read(body);
        }

        return Refused();
    }

    private static HttpResponseMessage Read(JsonElement body)
    {
        string sql = body.GetProperty("query").GetString()!;
        JsonElement parameters = body.TryGetProperty("params", out JsonElement p) ? p : default;

        if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return Refused();
        }

        Match limited = Limit().Match(sql);
        int limit = limited.Success ? int.Parse(limited.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 500;

        if (sql.Contains("WHERE Year = :year", StringComparison.Ordinal))
        {
            int year = parameters.GetProperty("year").GetInt32();

            return Ndjson(Rows.Where(r => r.Year == year).OrderByDescending(r => r.Gdp).Take(limit)
                .Select(r => (object)new { r.Country, gdp = r.Gdp }));
        }

        if (sql.Contains("WHERE Country = :country", StringComparison.Ordinal))
        {
            string country = parameters.GetProperty("country").GetString()!;

            return Ndjson(Rows.Where(r => r.Country == country).OrderBy(r => r.Year)
                .Select(r => (object)new { r.Year, gdp = r.Gdp, life = r.Life }));
        }

        if (sql.Contains("GROUP BY Country", StringComparison.Ordinal))
        {
            return Ndjson(Rows.GroupBy(r => r.Country)
                .Select(g => new { Country = g.Key, life = g.Average(r => r.Life), n = g.Count() })
                .OrderByDescending(g => g.life)
                .Take(limit)
                .Select(g => (object)g));
        }

        if (sql.Contains("FROM WorldBank_chunks", StringComparison.Ordinal))
        {
            return Ndjson(
            [
                new
                {
                    chunk_index = 0,
                    content = "World Bank indicators\ncolumns: Country, Year, GDP (USD), Population, Life Expectancy",
                    translations = new[]
                    {
                        new { code = "<2es>", content = "Indicadores del Banco Mundial\ncolumnas: País, Año, PIB (USD), Población" },
                        new { code = "<2fr>", content = "Indicateurs de la Banque mondiale\ncolonnes: Pays, Année, PIB (USD), Population" },
                    },
                },
            ]);
        }

        return Problem(HttpStatusCode.BadRequest, "StatementNotRecognised", $"The stub does not know the statement: {sql}");
    }

    private static HttpResponseMessage Refused() =>
        Problem(HttpStatusCode.Forbidden, "Security", "'suser' may not do that in 'Sandbox'");

    private static HttpResponseMessage Ndjson(IEnumerable<object> rows)
    {
        StringBuilder text = new();
        int count = 0;

        foreach (object row in rows)
        {
            _ = text.Append(JsonSerializer.Serialize(row)).Append('\n');
            count++;
        }

        _ = text.Append(JsonSerializer.Serialize(new { returned = count, more = false })).Append('\n');

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text.ToString(), Encoding.UTF8, "application/x-ndjson") };
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Problem(HttpStatusCode status, string code, string detail) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { type = "about:blank", title = code, status = (int)status, detail, errorCode = code }),
                Encoding.UTF8,
                "application/problem+json"),
        };

    [GeneratedRegex(@"LIMIT (\d+)")]
    private static partial Regex Limit();
}
