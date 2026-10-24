using System;
using System.Collections.Generic;
using System.Linq;

namespace DataOil.Samples.EntityFrameworkCore;

/// <summary>
/// Where the sample connects and as whom, read from environment variables.
/// </summary>
/// <remarks>
/// <para>
/// The only default that reaches a server is the public sandbox, as its read-only account: with no
/// connection variable set, the sample reads <c>Sandbox</c> at <c>https://sandbox.dataoil.tech</c>.
/// </para>
/// <para>
/// Name any connection variable and the sandbox is out of the picture: nothing else is filled in,
/// and every variable that is missing is a message naming it. A sample that wrote to somebody's
/// engine because a value was left in the code would be the first thing a prospective customer copied.
/// </para>
/// </remarks>
internal sealed record SampleSettings(
    string Url,
    string Database,
    string? Token,
    string? Username,
    string? Password,
    string? CertificateAuthorityFile,
    bool CreateDatabase,
    string JobId,
    bool Sandbox = false)
{
    /// <summary>The public sandbox's address.</summary>
    public const string SandboxUrl = "https://sandbox.dataoil.tech";

    /// <summary>The sandbox's database.</summary>
    public const string SandboxDatabase = "Sandbox";

    /// <summary>The sandbox's read-only account, public by design: it can read <c>Sandbox</c> and nothing else.</summary>
    public const string SandboxUsername = "suser";

    /// <summary>
    /// The sandbox account's password, public by design. It holds <c>#</c> and <c>%</c>; as a C#
    /// string it needs no escaping, because the client puts it into the Basic auth header itself.
    /// </summary>
    public const string SandboxPassword = "hFn3dh#2%hn8A";

    /// <summary>The variables that name a connection. Set none of them and the sample uses the sandbox.</summary>
    public static readonly IReadOnlyList<string> ConnectionVariables =
        ["DATAOIL_URL", "DATAOIL_DATABASE", "DATAOIL_TOKEN", "DATAOIL_USERNAME", "DATAOIL_PASSWORD"];

    /// <summary>The settings a dry run uses: a placeholder address and credential, never sent anywhere.</summary>
    public static SampleSettings DryRun { get; } = new(
        "https://engine.invalid",
        "sample_notes",
        "dot_placeholder",
        null,
        null,
        null,
        CreateDatabase: true,
        "sample-efcore");

    /// <summary>Whether an address is the public sandbox, where the read-only walk-through is the one that runs.</summary>
    /// <param name="url">The address.</param>
    /// <returns>True for the sandbox.</returns>
    public static bool IsSandbox(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? address)
        && string.Equals(address.Host, new Uri(SandboxUrl).Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the settings from the environment.</summary>
    /// <param name="read">Reads one variable; <see cref="Environment.GetEnvironmentVariable(string)"/> in the program.</param>
    /// <param name="problems">Every variable that is missing or wrong, in the order they are read.</param>
    /// <returns>The settings, or null when anything was missing.</returns>
    public static SampleSettings? FromEnvironment(Func<string, string?> read, out IReadOnlyList<string> problems)
    {
        List<string> found = [];

        if (ConnectionVariables.All(name => Value(read, name) is null))
        {
            problems = found;

            return new SampleSettings(
                SandboxUrl,
                SandboxDatabase,
                null,
                SandboxUsername,
                SandboxPassword,
                Value(read, "DATAOIL_CA"),
                CreateDatabase: false,
                Value(read, "DATAOIL_JOB_ID") ?? "sample-efcore",
                Sandbox: true);
        }

        string? url = Value(read, "DATAOIL_URL");
        string? database = Value(read, "DATAOIL_DATABASE");
        string? token = Value(read, "DATAOIL_TOKEN");
        string? username = Value(read, "DATAOIL_USERNAME");
        string? password = Value(read, "DATAOIL_PASSWORD");

        if (url is null)
        {
            found.Add("DATAOIL_URL is not set: the address the engine answers on, such as https://acme.dataoil.tech.");
        }

        if (database is null)
        {
            found.Add("DATAOIL_DATABASE is not set: the database the sample works in.");
        }

        if ((token is null) == (username is null))
        {
            found.Add("Set DATAOIL_TOKEN, or DATAOIL_USERNAME and DATAOIL_PASSWORD, and not both.");
        }
        else if (username is not null && password is null)
        {
            found.Add("DATAOIL_PASSWORD is not set for DATAOIL_USERNAME.");
        }

        problems = found;

        if (found.Count > 0)
        {
            return null;
        }

        return new SampleSettings(
            url!,
            database!,
            token,
            username,
            password,
            Value(read, "DATAOIL_CA"),
            string.Equals(Value(read, "DATAOIL_CREATE_DATABASE"), "true", StringComparison.OrdinalIgnoreCase),
            Value(read, "DATAOIL_JOB_ID") ?? "sample-efcore",
            IsSandbox(url!));
    }

    private static string? Value(Func<string, string?> read, string name) =>
        read(name) is { Length: > 0 } held && !string.IsNullOrWhiteSpace(held) ? held.Trim() : null;
}
