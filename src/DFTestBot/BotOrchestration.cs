// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;

namespace DFTestBot
{
    public static class BotOrchestration
    {
        // NOTE: Using environment variables in orchestrator functions is not safe since environment variables are non-deterministic.
        //       I'm ignoring this advice for now for the sake of expediency
        static readonly Uri DeploymentServiceBaseUrl = new Uri(Environment.GetEnvironmentVariable("DEPLOYMENT_SERVICE_BASE_URL"));
        static readonly string DeploymentServiceKey = Environment.GetEnvironmentVariable("DEPLOYMENT_SERVICE_API_KEY");

        [FunctionName(nameof(BotOrchestrator))]
        public static async Task BotOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context,
            ILogger log)
        {
            log = context.CreateReplaySafeLogger(log);

            DateTime startTimeUtc = context.CurrentUtcDateTime;
            TestParameters testParameters = context.GetInput<TestParameters>();

            // Create a new function app
            if (!await TryCallDeploymentServiceHttpApiAsync("api/CreateNewFunctionApp", context, log, testParameters))
            {
                string message = $"Failed to create a new function app! 💣 Check the internal deployment service logs for more details.";
                await context.CallActivityAsync(nameof(PostGitHubComment), (testParameters.GitHubCommentApiUrl, message));
                throw new Exception(message);
            }

            try
            {
                // Deploy and start the test app
                HttpManagementPayload managementUrls = null;
                if (!await TryCallDeploymentServiceHttpApiAsync(
                    "api/DeployToFunctionApp",
                    context,
                    log,
                    testParameters,
                    (responseJson) => managementUrls = JsonConvert.DeserializeObject<HttpManagementPayload>(responseJson)))
                {
                    string message = $"Failed to deploy the test app! 💣 Check the internal deployment service logs for more details.";
                    await context.CallActivityAsync(nameof(PostGitHubComment), (testParameters.GitHubCommentApiUrl, message));
                    throw new Exception(message);
                }

                // Get the URL for doing status queries
                if (managementUrls == null || string.IsNullOrEmpty(managementUrls.StatusQueryGetUri))
                {
                    string message = $"The deployment service API call succeeded but returned an unexpected response. Check the logs for details.";
                    await context.CallActivityAsync(nameof(PostGitHubComment), (testParameters.GitHubCommentApiUrl, message));
                    throw new Exception(message);
                }

                DurableOrchestrationStatus status = await WaitForStartAsync(context, log, managementUrls);
                if (status == null)
                {
                    string message = $"The test was scheduled but still hasn't started! Giving up. 😞";
                    await context.CallActivityAsync(nameof(PostGitHubComment), (testParameters.GitHubCommentApiUrl, message));
                    throw new Exception(message);
                }

                string previousCustomStatus = string.Empty;
                while (true)
                {
                    // The test orchestration reports back using a string message in the CustomStatus field
                    string currentCustomStatus = (string)status.CustomStatus;
                    if (currentCustomStatus != previousCustomStatus)
                    {
                        // There is a new status update - post it back to the PR thread.
                        log.LogInformation($"Current test status: {currentCustomStatus}");
                        await context.CallActivityAsync(nameof(PostGitHubComment), (testParameters.GitHubCommentApiUrl, currentCustomStatus));
                        previousCustomStatus = currentCustomStatus;
                    }

                    if (status.RuntimeStatus != OrchestrationRuntimeStatus.Running)
                    {
                        // The test orchestration completed.
                        break;
                    }

                    // Check every minute for an update - we don't want to poll too frequently or else the
                    // history will build up too much.
                    await SleepAsync(context, TimeSpan.FromMinutes(1));

                    // Refesh the status
                    status = await GetStatusAsync(context, managementUrls);
                }

                // The test orchestration has completed
                string finalMessage = status.RuntimeStatus switch
                {
                    OrchestrationRuntimeStatus.Completed => "The test completed successfully! ✅",
                    OrchestrationRuntimeStatus.Failed => "The test failed! 💣",
                    OrchestrationRuntimeStatus.Terminated => "The test was terminated or timed out. ⚠",
                    _ => $"The test stopped unexpectedly. Runtime status = **{status.RuntimeStatus}**. 🤔"
                };

                // Generate the AppLens link
                // Example: https://applens.azurewebsites.net/subscriptions/92d757cd-ef0d-4710-967d-2efa3c952358/resourceGroups/perf-testing/providers/Microsoft.Web/sites/dfperf-dedicated2/detectors/DurableFunctions_ManySequencesTest?startTime=2020-08-22T00:00&endTime=2020-08-22T01:00
                string startTime = startTimeUtc.ToString("yyyy-MM-ddThh:mm");
                string endTime = context.CurrentUtcDateTime.ToString("yyyy-MM-ddThh:mm");
                string subscriptionId = testParameters.SubscriptionId;
                string resourceGroup = testParameters.ResourceGroup;
                string appName = testParameters.AppName;
                string detectorName = testParameters.DetectorName;
                string link = $"https://applens.azurewebsites.net/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Web/sites/{appName}/detectors/{detectorName}?startTime={startTime}&endTime={endTime}";

                finalMessage += Environment.NewLine + Environment.NewLine + $"You can view more detailed results in [AppLens]({link}) (Microsoft internal). 🔗📈";

                TimeSpan cleanupInterval = TimeSpan.FromHours(1);
                DateTime cleanupTime = context.CurrentUtcDateTime.Add(cleanupInterval);
                finalMessage += Environment.NewLine + Environment.NewLine + $"The test app **{appName}** will be deleted at {cleanupTime:yyyy-MM-dd hh:mm:ss} UTC.";

                await context.CallActivityAsync(nameof(PostGitHubComment), (testParameters.GitHubCommentApiUrl, finalMessage));

                log.LogInformation($"Sleeping until {cleanupTime:yyyy-MM-dd hh:mm:ss} UTC to delete the test function app.");
                await context.CreateTimer(cleanupTime, CancellationToken.None);
            }
            catch (Exception)
            {
                string message = $"An unexpected failure occurred! 💣 Unfortunately, we can't continue the test run and the test app will be deleted. 😞";
                await context.CallActivityAsync(nameof(PostGitHubComment), (testParameters.GitHubCommentApiUrl, message));
                throw;
            }
            finally
            {
                // TODO: Consider delaying the deletion to allow for investigation

                // Any intermediate failures should result in an automatic cleanup
                log.LogInformation($"Deleting the test function app, {testParameters.AppName}.");
                if (!await TryCallDeploymentServiceHttpApiAsync("api/DeleteFunctionApp", context, log, testParameters))
                {
                    string failureMessage = $"Failed to delete the test app! 💣 Check the internal deployment service logs for more details.";
                    await context.CallActivityAsync(nameof(PostGitHubComment), (testParameters.GitHubCommentApiUrl, failureMessage));
                    throw new Exception(failureMessage);
                }

                string cleanupSuccessMessage = $"The test app {testParameters.AppName} has been deleted. Thanks for using the DFTest bot!";
                await context.CallActivityAsync(nameof(PostGitHubComment), (testParameters.GitHubCommentApiUrl, cleanupSuccessMessage));
            }
        }

        static async Task<bool> TryCallDeploymentServiceHttpApiAsync(
            string httpApiPath,
            IDurableOrchestrationContext context,
            ILogger log,
            TestParameters testParameters,
            Action<string> handleResponsePayload = null)
        {
            var request = new DurableHttpRequest(
                HttpMethod.Post,
                new Uri(DeploymentServiceBaseUrl, httpApiPath),
                headers: new Dictionary<string, StringValues>
                {
                    { "x-functions-key", DeploymentServiceKey },
                    { "Content-Type", "application/json" },
                },
                content: JsonConvert.SerializeObject(testParameters));

            // Deploy and start the test app
            log.LogInformation($"Calling deployment service: {request.Method} {request.Uri}...");
            DurableHttpResponse response = await context.CallHttpAsync(request);
            log.LogInformation($"Received response from deployment service: {response.StatusCode}: {response.Content}");
            if ((int)response.StatusCode < 300)
            {
                handleResponsePayload?.Invoke(response.Content);
                return true;
            }

            return false;
        }

        [FunctionName(nameof(PostGitHubComment))]
        public static Task PostGitHubComment([ActivityTrigger] (Uri commentApiUrl, string markdownMessage) input, ILogger log)
        {
            return GitHubClient.PostCommentAsync(
                input.commentApiUrl,
                input.markdownMessage,
                log);
        }

        static async Task<DurableOrchestrationStatus> WaitForStartAsync(
            IDurableOrchestrationContext context,
            ILogger log,
            HttpManagementPayload managementUrls)
        {
            log.LogInformation($"Waiting for {managementUrls.Id} to start.");
            DateTime timeoutTime = context.CurrentUtcDateTime.AddMinutes(5);
            while (true)
            {
                DurableOrchestrationStatus status = await GetStatusAsync(context, managementUrls);
                if (status != null && status.RuntimeStatus != OrchestrationRuntimeStatus.Pending)
                {
                    // It started - break out of the loop
                    log.LogInformation($"Instance {managementUrls.Id} started successfully.");
                    return status;
                }

                if (context.CurrentUtcDateTime >= timeoutTime)
                {
                    // Timeout - return null to signal that it never started
                    log.LogWarning($"Instance {managementUrls.Id} did not start in {timeoutTime}. Giving up.");
                    return null;
                }

                // Retry every 10 seconds
                await SleepAsync(context, TimeSpan.FromSeconds(10));
            }
        }

        static async Task<DurableOrchestrationStatus> GetStatusAsync(
            IDurableOrchestrationContext context,
            HttpManagementPayload managementUrls)
        {
            DurableHttpResponse res = await context.CallHttpAsync(
                HttpMethod.Get,
                new Uri(managementUrls.StatusQueryGetUri));

            return JsonConvert.DeserializeObject<DurableOrchestrationStatus>(res.Content);
        }

        static Task SleepAsync(IDurableOrchestrationContext context, TimeSpan sleepTime)
        {
            return context.CreateTimer(
                context.CurrentUtcDateTime.Add(sleepTime),
                CancellationToken.None);
        }
    }
}