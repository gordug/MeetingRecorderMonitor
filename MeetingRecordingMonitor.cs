using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Azure.Identity;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.WindowsAzure.Storage.Blob;

namespace MeetingRecorderMonitor;

public static class MeetingRecordingMonitor
{
    private static GraphServiceClient _graphClient;
    private static ILogger _log;

    [FunctionName("MonitorMeetingRecordings")]
    public static async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo _, // run every 5 minutes
        [Blob("{containerName}/{blobName}", FileAccess.ReadWrite)] CloudBlockBlob recordingBlob,
        ILogger log)
    {
        _log = log;
        _log.LogInformation($"Checking for new meeting recordings at {DateTime.Now}");

        // Authenticate with Graph API using client credentials
        _graphClient = GetGraphClient();

        // Query for meeting recordings in SharePoint site
        var driveItems = (await _graphClient
                                .Sites["{siteId}"]
                                .Drives["{driveId}"]
                                .GetAsync())?
            .Root?
            .Children;

        // Process each meeting recording
        await ProcessRecordings(recordingBlob, FilterRecordings(driveItems));
    }

    private static async Task ProcessRecordings(
        CloudBlockBlob recordingBlob,
        List<DriveItem> recordings)
    {
        foreach (var recording in recordings)
        {
            _log.LogInformation($"Processing meeting recording {recording.Name}");

            // Download the meeting recording to a local stream
            var drive = await _graphClient.Sites["{siteId}"]
                                          .Drives["{driveId}"]
                                          .GetAsync();
            if (drive is not {Items.Count: < 1})
            {
                continue;
            }

            var item = drive.Items.FirstOrDefault(item => item.Id == recording.Id);
            var content = item?.Content;
            if (content is null)
            {
                continue;
            }

            MemoryStream recordingStream = new(content);

            // Reset stream position to beginning
            recordingStream.Position = 0;

            // Save the meeting recording to blob storage
            await recordingBlob.UploadFromStreamAsync(recordingStream);

            // Process the meeting recording using the ProcessMeetingRecording function
            await ProcessMeetingRecording(recordingBlob);
        }
    }

    /// <summary>
    /// Filters the recordings to only include the ones that were created in the last 10 minutes and have audio
    /// </summary>
    /// <param name="driveItems"></param>
    /// <returns></returns>
    private static List<DriveItem> FilterRecordings(IEnumerable<DriveItem> driveItems)
    {
        // Filter the recordings to only include the ones that were created in the last 10 minutes
        var now = DateTime.UtcNow;
        var tenMinutesAgo = now.AddMinutes(-10);
        var recentRecordings = (List<DriveItem>)driveItems?.Where(driveItem => driveItem.CreatedDateTime > tenMinutesAgo);
        return recentRecordings?.Where(recording => recording.Audio is not null).ToList();
    }

    private static async Task ProcessMeetingRecording(CloudBlob recordingBlob)
    {
        var functionUrl = Environment.GetEnvironmentVariable("ProcessMeetingRecordingFunctionUrl");

        using var httpClient = new HttpClient();
        using var requestContent = new MultipartFormDataContent();
        using var fileContent = new StreamContent(await recordingBlob.OpenReadAsync());

        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        requestContent.Add(fileContent, "file", recordingBlob.Name);

        var response = await httpClient.PostAsync(functionUrl, requestContent);

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Error processing meeting recording: {response.StatusCode}");
        }
    }

    private static GraphServiceClient GetGraphClient()
    {
        // Get the client ID, client secret, and tenant ID from the Azure Secrets
        var clientId = Environment.GetEnvironmentVariable("ClientId");
        var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
        var tenantId = Environment.GetEnvironmentVariable("TenantId");
        var client = new GraphServiceClient(new ClientSecretCredential(tenantId, clientId, clientSecret));
        return client;
    }
}