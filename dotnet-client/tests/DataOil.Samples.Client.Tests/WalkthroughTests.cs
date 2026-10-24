using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using DataOil.Client;
using Xunit;

namespace DataOil.Samples.Client.Tests;

/// <summary>
/// The sample, run against the stub: no server, no credential, nothing sent anywhere.
/// </summary>
public sealed class WalkthroughTests
{
    private static DataOilClient Client(StubEngine stub) => new(
        new DataOilClientOptions { BaseUrl = "https://engine.invalid", Database = "sample_notes", Token = "dot_placeholder", JobId = "sample-tests" },
        new HttpClient(stub));

    [Fact]
    public async Task Every_step_runs_and_answers_what_was_written()
    {
        StubEngine stub = new();
        using DataOilClient client = Client(stub);
        using StringWriter output = new();

        WalkthroughResult result = await Walkthrough.RunAsync(client, createDatabase: true, output, "SampleNote_test");

        Assert.True(result.CreatedDatabase);
        Assert.Equal(4, result.Written);
        Assert.Equal(["Casing failure on well 7", "Permit to work audit"], result.HighlyRated);
        Assert.Equal(4, result.Streamed);
        Assert.Equal("Casing failure on well 7", result.KeywordHit);
        Assert.Equal("Casing failure on well 7", result.NearestHit);
        Assert.True(stub.EveryRequestCarriedTheHeaders);
    }

    [Fact]
    public async Task The_collection_and_a_database_the_run_created_are_dropped()
    {
        StubEngine stub = new();
        using DataOilClient client = Client(stub);

        _ = await Walkthrough.RunAsync(client, createDatabase: true, TextWriter.Null, "SampleNote_test");

        Assert.Equal("POST /api/v2/databases", stub.Requests[1]);
        Assert.Equal("DELETE /api/v2/databases/sample_notes", stub.Requests[^1]);
    }

    [Fact]
    public async Task A_database_that_was_already_there_is_never_dropped()
    {
        StubEngine stub = new(databaseExists: true);
        using DataOilClient client = Client(stub);

        WalkthroughResult result = await Walkthrough.RunAsync(client, createDatabase: true, TextWriter.Null, "SampleNote_test");

        Assert.False(result.CreatedDatabase);
        Assert.DoesNotContain(stub.Requests, request => request.StartsWith("DELETE /api/v2/databases", StringComparison.Ordinal));
        Assert.DoesNotContain("POST /api/v2/databases", stub.Requests);
    }

    [Fact]
    public async Task A_missing_database_is_not_created_unless_asked()
    {
        StubEngine stub = new();
        using DataOilClient client = Client(stub);

        _ = await Assert.ThrowsAsync<DataOilNotFoundException>(() => Walkthrough.RunAsync(client, createDatabase: false, TextWriter.Null, "SampleNote_test"));
        Assert.Equal(["GET /api/v2/databases/sample_notes/schema/version"], stub.Requests);
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
    public void The_sandbox_keeps_the_job_id_and_ignores_the_create_switch()
    {
        Dictionary<string, string> environment = new() { ["DATAOIL_JOB_ID"] = "my-job", ["DATAOIL_CREATE_DATABASE"] = "true" };

        SampleSettings? settings = SampleSettings.FromEnvironment(name => environment.GetValueOrDefault(name), out _);

        Assert.NotNull(settings);
        Assert.True(settings.Sandbox);
        Assert.Equal("my-job", settings.JobId);
        Assert.False(settings.CreateDatabase);
    }

    [Fact]
    public void Naming_any_connection_variable_leaves_the_sandbox_and_names_the_rest()
    {
        Dictionary<string, string> environment = new() { ["DATAOIL_URL"] = "https://acme.dataoil.tech" };

        SampleSettings? none = SampleSettings.FromEnvironment(name => environment.GetValueOrDefault(name), out IReadOnlyList<string> problems);

        Assert.Null(none);
        Assert.Equal(2, problems.Count);
        Assert.StartsWith("DATAOIL_DATABASE", problems[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://acme.dataoil.tech", false)]
    [InlineData("https://SANDBOX.dataoil.tech/", true)]
    public void Only_the_sandbox_runs_the_read_only_walkthrough(string url, bool sandbox)
    {
        Dictionary<string, string> environment = new()
        {
            ["DATAOIL_URL"] = url,
            ["DATAOIL_DATABASE"] = "Sandbox",
            ["DATAOIL_USERNAME"] = "suser",
            ["DATAOIL_PASSWORD"] = "placeholder",
        };

        SampleSettings? settings = SampleSettings.FromEnvironment(name => environment.GetValueOrDefault(name), out _);

        Assert.NotNull(settings);
        Assert.Equal(sandbox, settings.Sandbox);
    }

    [Fact]
    public async Task The_sandbox_walkthrough_reads_and_the_write_is_refused()
    {
        SandboxStub stub = new();
        using DataOilClient client = new(
            new DataOilClientOptions { BaseUrl = "https://sandbox.invalid", Database = "Sandbox", Username = "suser", Password = "placeholder", JobId = "sample-tests" },
            new HttpClient(stub));
        using StringWriter output = new();

        SandboxResult result = await SandboxWalkthrough.RunAsync(client, output);

        Assert.Equal(["Sandbox"], result.Databases);
        Assert.Contains("WorldBank_rows", result.Collections);
        Assert.DoesNotContain("V", result.Collections);
        Assert.Contains("GDP (USD)", result.Columns);
        Assert.Equal(["Italy", "Nigeria", "India", "Japan"], result.TopGdp);
        Assert.Equal(2, result.JapanRows);
        Assert.Equal("Japan", result.LongestLived[0]);
        Assert.Equal(["<2es>", "<2fr>"], result.Languages);
        Assert.Equal("403 Security", result.WriteRefused);
        Assert.Contains("5. Write refused as expected: 403 Security", output.ToString(), StringComparison.Ordinal);
        Assert.True(stub.EveryRequestCarriedTheHeaders);
        Assert.Equal(7, stub.Requests.Count);
    }

    [Fact]
    public void Settings_take_a_name_and_password_and_the_create_switch()
    {
        Dictionary<string, string> environment = new()
        {
            ["DATAOIL_URL"] = "https://acme.dataoil.tech",
            ["DATAOIL_DATABASE"] = "sample_notes",
            ["DATAOIL_USERNAME"] = "someone",
            ["DATAOIL_PASSWORD"] = "placeholder",
            ["DATAOIL_CREATE_DATABASE"] = "TRUE",
        };

        SampleSettings? settings = SampleSettings.FromEnvironment(name => environment.GetValueOrDefault(name), out IReadOnlyList<string> problems);

        Assert.Empty(problems);
        Assert.NotNull(settings);
        Assert.True(settings.CreateDatabase);
        Assert.Null(settings.Token);
        Assert.Equal("sample-dotnet-client", settings.JobId);
    }

    [Fact]
    public void A_token_and_a_name_together_are_refused()
    {
        Dictionary<string, string> environment = new()
        {
            ["DATAOIL_URL"] = "https://acme.dataoil.tech",
            ["DATAOIL_DATABASE"] = "sample_notes",
            ["DATAOIL_TOKEN"] = "dot_placeholder",
            ["DATAOIL_USERNAME"] = "someone",
        };

        Assert.Null(SampleSettings.FromEnvironment(name => environment.GetValueOrDefault(name), out IReadOnlyList<string> problems));
        Assert.Single(problems);
        Assert.True(problems.All(p => p.Contains("not both", StringComparison.Ordinal)));
    }
}
