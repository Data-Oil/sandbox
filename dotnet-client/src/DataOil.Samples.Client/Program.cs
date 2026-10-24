using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using DataOil.Client;

namespace DataOil.Samples.Client;

/// <summary>
/// The Data Oil .NET client sample: <c>dotnet run</c> against the public sandbox, read only, when no
/// <c>DATAOIL_*</c> variable is set, or against your own tenant when they name it; or
/// <c>dotnet run -- --dry-run</c> against a stub, with no server at all.
/// </summary>
internal static class Program
{
    /// <summary>Runs the sample.</summary>
    /// <param name="args"><c>--dry-run</c> to run against the stub.</param>
    /// <returns>0 when every step ran; 1 for missing settings; 2 when the engine refused something.</returns>
    public static async Task<int> Main(string[] args)
    {
        bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        SampleSettings? settings;
        HttpClient http;

        if (dryRun)
        {
            settings = SampleSettings.DryRun;
            http = new HttpClient(new StubEngine());
            Console.WriteLine("Dry run: the answers come from a stub in this process, not from an engine. Nothing is sent anywhere.");
        }
        else
        {
            settings = SampleSettings.FromEnvironment(Environment.GetEnvironmentVariable, out IReadOnlyList<string> problems);

            if (settings is null)
            {
                foreach (string problem in problems)
                {
                    Console.Error.WriteLine(problem);
                }

                Console.Error.WriteLine("See README.md, or unset every DATAOIL_* variable to use the public sandbox, or run with --dry-run to see the sample work without a server.");

                return 1;
            }

            http = new HttpClient(Handler(settings.CertificateAuthorityFile));
        }

        DataOilClientOptions options = new()
        {
            BaseUrl = settings.Url,
            Database = settings.Database,
            Token = settings.Token,
            Username = settings.Username,
            Password = settings.Password,
            JobId = settings.JobId,
        };

        using (http)
        using (DataOilClient client = new(options, http))
        {
            try
            {
                if (settings.Sandbox)
                {
                    // The translations include Japanese and Chinese.
                    Console.OutputEncoding = Encoding.UTF8;
                    Console.WriteLine($"The public sandbox: {settings.Url}, database {settings.Database}, as the read-only account {settings.Username}. Set DATAOIL_URL and the rest to use your own tenant.");
                    SandboxResult sandboxed = await SandboxWalkthrough.RunAsync(client, Console.Out).ConfigureAwait(false);

                    if (sandboxed.WriteRefused is null)
                    {
                        return 2;
                    }

                    Console.WriteLine("Done: a read-only walk-through of the sandbox; nothing was written.");

                    return 0;
                }

                WalkthroughResult result = await Walkthrough.RunAsync(client, settings.CreateDatabase, Console.Out).ConfigureAwait(false);
                Console.WriteLine($"Done: {result.Written} notes written, searched and cleaned up.");

                return 0;
            }
            catch (DataOilException refused)
            {
                // Every refusal carries a stable code, the engine's own sentence and a correlation id
                // that the engine's logs and the tenant's activity view are indexed by.
                Console.Error.WriteLine($"The engine refused: {(int)refused.Status} {refused.ErrorCode}: {refused.Detail} (correlation {refused.CorrelationId})");

                return 2;
            }
        }
    }

    /// <summary>
    /// The handler: the machine's own trust, or a deployment's own certificate authority when
    /// <c>DATAOIL_CA</c> names its certificate file.
    /// </summary>
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
