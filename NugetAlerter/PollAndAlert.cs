using Microsoft.Azure.WebJobs;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace NugetAlerter
{
    public static class PollAndAlert
    {
        private static readonly string NUGET_PACKAGE_ID = Environment.GetEnvironmentVariable(@"PackageId");
        private static readonly Uri NUGET_QUERY_URI = new Uri($@"https://api-v2v3search-0.nuget.org/query?q={NUGET_PACKAGE_ID}&prerelease=true");

        private static readonly string STORAGE_CONNECTION_STRING = Environment.GetEnvironmentVariable(@"StorageConnectionString");
        private static readonly Uri FLOW_ALERT_ENDPOINT = new Uri(Environment.GetEnvironmentVariable(@"FlowAlertEndpoint"));

[FunctionName("Nuget6HourAlert")]
public static async Task Run([TimerTrigger("0 0 */6 * * *")]TimerInfo myTimer, Microsoft.Azure.WebJobs.Host.TraceWriter log)
{
    dynamic nugetResults;
    using (var client = new HttpClient { BaseAddress = NUGET_QUERY_URI })
    {
        var resultsJson = await client.GetStringAsync(string.Empty);
        nugetResults = JObject.Parse(resultsJson);
    }

    // we only care about packages that *exactly* match the id, not others that might be returned from a search
    dynamic targetPackage = ((IEnumerable<dynamic>)nugetResults.data)
        .SingleOrDefault(i => ((string)i.id).Equals(NUGET_PACKAGE_ID, StringComparison.OrdinalIgnoreCase));
    if (targetPackage != null)
    {
        string version = targetPackage.version;
        log.Info($@"Package found. Latest version: {version}");

        var storageClient = CloudStorageAccount.Parse(STORAGE_CONNECTION_STRING).CreateCloudBlobClient();
        var container = storageClient.GetContainerReference(@"functionvars");
        container.CreateIfNotExists();

        // get the blob that contains the last version we saw for this package. We're naming it the same as our package id
        var blob = container.GetBlockBlobReference(NUGET_PACKAGE_ID);

        if (!blob.Exists())
        {   // if we haven't processed this package before, just set our baseline version
            log.Info(@"First time we've seen this package. Storing version.");
            blob.UploadText(version);
#if DEBUG
            using (var notificationClient = new HttpClient { BaseAddress = FLOW_ALERT_ENDPOINT })
            {
                await notificationClient.PostAsJsonAsync(string.Empty, new { message = $@"New version of {NUGET_PACKAGE_ID} has been published to NuGet. Version {version}" });
            }
#endif
        }
        else
        {
            var lastSeenVersion = new StreamReader(blob.OpenRead()).ReadToEnd();
            log.Info($@"Last version we saw was {lastSeenVersion}");
            if (!lastSeenVersion.Equals(version, StringComparison.OrdinalIgnoreCase))
            {   // if the latest version of the pkg on nuget doesn't match the last one we've seen, it's new!
                log.Info($@"Notifying!");

                using (var notificationClient = new HttpClient { BaseAddress = FLOW_ALERT_ENDPOINT })
                {
                    await notificationClient.PostAsJsonAsync(string.Empty, new { message = $@"New version of {NUGET_PACKAGE_ID} has been published to NuGet. Version {version}" });
                }

                // update the last seen version in the associated blob entry of our Storage account
                blob.UploadText(version);
            }
        }
    }
    else
    {
        log.Info($@"No package found with id {NUGET_PACKAGE_ID}");
    }
}
    }
}