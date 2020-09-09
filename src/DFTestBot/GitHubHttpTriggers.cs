// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace DFTestBot
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Http.Extensions;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.DurableTask;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public static class GitHubHttpTriggers
    {
        const string CommandPrefix = "/DFTest ";

        static readonly string TestAppSubscriptionId = Environment.GetEnvironmentVariable("TEST_APP_SUBSCRIPTION_ID");
        static readonly string TestAppResourceGroup = Environment.GetEnvironmentVariable("TEST_APP_RESOURCE_GROUP");

        [FunctionName("GitHubComment")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "POST")] HttpRequest req,
            [DurableClient] IDurableClient durableClient,
            ILogger log)
        {
            log.LogInformation($"Received a webhook: {req.GetDisplayUrl()}");

            if (!GitHubClient.IsConfigured)
            {
                throw new InvalidOperationException("The GitHub client has not been properly configured!");
            }

            if (!req.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return new BadRequestObjectResult("Expected application/json");
            }

            dynamic json;
            using (var reader = new StreamReader(req.Body, Encoding.UTF8))
            {
                string content = await reader.ReadToEndAsync();
                try
                {
                    json = JObject.Parse(content);
                }
                catch (JsonReaderException e)
                {
                    return new BadRequestObjectResult($"Invalid JSON: {e.Message}");
                }
            }

            if (json.issue.pull_request == null)
            {
                return new BadRequestObjectResult("Not a pull request comment");
            }
            else if (json.action != "created")
            {
                return new BadRequestObjectResult($"Not a new comment (action = '{json.action}')");
            }

            string commentBody = json.comment.body;
            log.LogInformation($"Comment: {commentBody}");

            Uri commentApiUrl = new Uri((string)json.issue.comments_url);

            // TODO: We need to be more careful about how we parse this. For example, let's require that /DFTest must
            //       be at the beginning of a line and not somewhere in the middle.
            // TODO: We should support multiple tests runs in a single comment at some point.
            int commandStartIndex = commentBody.IndexOf(CommandPrefix, StringComparison.OrdinalIgnoreCase);
            if (commandStartIndex < 0 || commentBody.Contains("Durable Functions Test Bot"))
            {
                // Ignore unrelated comments or comments that come from the bot (like the help message)
                return new OkObjectResult($"No commands detected");
            }

            string command = commentBody.Substring(commandStartIndex + CommandPrefix.Length);
            if (!TryParseCommand(command, out string friendlyTestName, out TestDescription testInfo, out string testParameters, out string errorMessage))
            {
                await GitHubClient.PostCommentAsync(commentApiUrl, errorMessage, log);
                return new OkObjectResult($"Replied with instructions");
            }

            var sb = new StringBuilder();
            sb.Append("Hi! I have received your command: ");
            sb.AppendLine($"`{command}`");
            sb.AppendLine();

            if (json.issue.author_association != "COLLABORATOR" &&
                json.issue.author_association != "OWNER")
            {
                sb.AppendLine($"Unfortunately, only collaborators are allowed to run these commands.");

                string internalMessage = $"Command {command} rejected because author_association = {json.issue.author_association}.";
                log.LogWarning(internalMessage);
                return new OkObjectResult(internalMessage);
            }

            Uri pullRequestUrl = new Uri((string)json.issue.pull_request.url);
            dynamic pullRequestJson = await GitHubClient.GetPullRequestInfoAsync(pullRequestUrl);
            string branchName = pullRequestJson.head.@ref;

            // NOTE: site names must be 60 characters or less, leaving ~24 characters for test names
            string issueId = json.issue.number;
            string appName = $"dftest-{friendlyTestName}-pr{issueId}-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 4)}";

            var parameters = new TestParameters
            {
                SubscriptionId = TestAppSubscriptionId,
                ResourceGroup = TestAppResourceGroup,
                GitHubCommentApiUrl = commentApiUrl,
                GitHubBranch = branchName,
                AppName = appName,
                TestName = testInfo.StarterFunctionName,
                Parameters = testParameters,
                DetectorName = testInfo.AppLensDetector,
            };

            string instanceId = $"DFTestBot-PR{issueId}-{DateTime.UtcNow:yyyyMMddhhmmss}";
            await durableClient.StartNewAsync(nameof(BotOrchestration.BotOrchestrator), instanceId, parameters);
            sb.Append($"I've started a new deployment orchestration with ID **{instanceId}** that will validate the changes in this PR. ");
            sb.AppendLine($"If the build succeeds, the orchestration will create an app named **{appName}** and run the _{friendlyTestName}_ test using your custom build.");
            sb.AppendLine();
            sb.AppendLine("I'll report back when I have status updates.");

            await GitHubClient.PostCommentAsync(commentApiUrl, sb.ToString(), log);
            log.LogInformation("Test scheduled successfully!");
            return new OkObjectResult("Test scheduled successfully!");
        }

        static bool TryParseCommand(
            string input,
            out string testName,
            out TestDescription testInfo,
            out string testParameters,
            out string errorMessage)
        {
            string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                testName = null;
                testInfo = null;
                testParameters = null;
                errorMessage = GetSyntaxHelp() + Environment.NewLine + Environment.NewLine + GetTestNameHelp();
                return false;
            }

            if (parts.Length < 2 || !parts[0].Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                testName = null;
                testInfo = null;
                testParameters = null;
                errorMessage = GetSyntaxHelp();
                return false;
            }

            testName = parts[1];
            testParameters = string.Join('&', parts[2..]);
            if (SupportedTests.TryGetTestInfo(testName, out testInfo))
            {
                errorMessage = null;
                return true;
            }
            else
            {
                errorMessage = GetTestNameHelp();
                return false;
            }
        }

        static string GetSyntaxHelp()
        {
            return $"The syntax for commands is: `{CommandPrefix.Trim()} run <TestName> <Param1> <Param2> ...`";
        }

        static string GetTestNameHelp()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Here are the supported `<TestName>` values:").AppendLine();
            foreach ((string name, TestDescription description) in SupportedTests.GetAll())
            {
                sb.Append($"* `{name}`: {description.Description}");
                if (!description.IsEnabled)
                {
                    sb.Append(" **(DISABLED)**");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}