using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using DataOil.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DataOil.Samples.EntityFrameworkCore;

/// <summary>
/// The Data Oil Entity Framework Core sample: <c>dotnet run</c> against the public sandbox, read
/// only, when no <c>DATAOIL_*</c> variable is set, or against your own tenant when they name it; or
/// <c>dotnet run -- --dry-run</c> to see the models and their translations with no server at all.
/// </summary>
internal static class Program
{
    /// <summary>Runs the sample.</summary>
    /// <param name="args"><c>--dry-run</c> to translate without connecting.</param>
    /// <returns>0 when every step ran; 1 for missing settings; 2 when the engine refused something.</returns>
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase))
        {
            DryRun();

            return 0;
        }

        SampleSettings? settings = SampleSettings.FromEnvironment(Environment.GetEnvironmentVariable, out IReadOnlyList<string> problems);

        if (settings is null)
        {
            foreach (string problem in problems)
            {
                Console.Error.WriteLine(problem);
            }

            Console.Error.WriteLine("See README.md, or unset every DATAOIL_* variable to use the public sandbox, or run with --dry-run to see the model and its statements without a server.");

            return 1;
        }

        using HttpClient http = new(Handler(settings.CertificateAuthorityFile));

        try
        {
            if (settings.Sandbox)
            {
                // The translations include Japanese and Chinese.
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine($"The public sandbox: {settings.Url}, database {settings.Database}, as the read-only account {settings.Username}. Set DATAOIL_URL and the rest to use your own tenant.");
                SandboxResult sandboxed = await SandboxWalkthrough.RunAsync(settings, http, Console.Out).ConfigureAwait(false);

                if (sandboxed.SaveRefused is null)
                {
                    return 2;
                }

                Console.WriteLine("Done: a read-only walk-through of the sandbox through the context; nothing was written.");

                return 0;
            }

            WalkthroughResult result = await Walkthrough.RunAsync(settings, http, Console.Out).ConfigureAwait(false);
            Console.WriteLine($"Done: {result.Written} notes saved, queried, searched and cleaned up.");

            return 0;
        }
        catch (DataOilException refused)
        {
            Console.Error.WriteLine($"The engine refused: {(int)refused.Status} {refused.ErrorCode}: {refused.Detail} (correlation {refused.CorrelationId})");

            return 2;
        }
        catch (DbUpdateException failed) when (failed.InnerException is DataOilException refused)
        {
            Console.Error.WriteLine($"The engine refused a save: {(int)refused.Status} {refused.ErrorCode}: {refused.Detail} (correlation {refused.CorrelationId})");

            return 2;
        }
        catch (InvalidOperationException stopped)
        {
            Console.Error.WriteLine(stopped.Message);

            return 2;
        }
    }

    /// <summary>
    /// The model and what its queries become in the engine's dialect, from a context whose connection
    /// is never opened: nothing is sent anywhere.
    /// </summary>
    internal static void DryRun()
    {
        Console.WriteLine("Dry run: the model and its statements, from a connection that is never opened. Nothing is sent anywhere.");

        using HttpClient unused = new();
        using NotesContext context = new(Walkthrough.Options(SampleSettings.DryRun, unused));

        foreach (IEntityType entity in context.Model.GetEntityTypes())
        {
            string columns = string.Join(", ", entity.GetProperties().Select(property => $"{property.Name} -> {property.GetColumnName()}"));
            Console.WriteLine($"{entity.ClrType.Name} -> {entity.GetTableName()}: {columns}; key {entity.FindPrimaryKey()!.Properties[0].GetColumnName()}");
        }

        foreach (string statement in Walkthrough.Translations(context))
        {
            Console.WriteLine(statement);
        }

        // The sandbox's model: column names with spaces and brackets, quoted with backticks.
        using WorldBankContext sandbox = new(SandboxWalkthrough.Options(SampleSettings.FromEnvironment(_ => null, out _)!, unused));
        IEntityType row = sandbox.Model.FindEntityType(typeof(WorldBankRow))!;
        Console.WriteLine($"{row.ClrType.Name} -> {row.GetTableName()}: {string.Join(", ", row.GetProperties().Select(property => $"{property.Name} -> {property.GetColumnName()}"))}");
        Console.WriteLine(SandboxWalkthrough.Translation(sandbox));
    }

    private static HttpClientHandler Handler(string? authorityFile)
    {
        HttpClientHandler handler = new();

        if (authorityFile is null)
        {
            return handler;
        }

        X509Certificate2 authority = X509CertificateLoader.LoadCertificateFromFile(authorityFile);

        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null)
            {
                return false;
            }

            using X509Chain chain = new();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            _ = chain.ChainPolicy.CustomTrustStore.Add(authority);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

            return chain.Build(certificate);
        };

        return handler;
    }
}
