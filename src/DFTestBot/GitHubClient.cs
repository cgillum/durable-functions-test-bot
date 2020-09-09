// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DFTestBot
{
    class GitHubClient
    {
        static readonly string GitHubOAuthToken;
        static readonly HttpClient HttpClient;
        static readonly bool GitHubCommentsDisabled = false;

        static GitHubClient()
        {
            GitHubOAuthToken = Environment.GetEnvironmentVariable("GITHUB_OAUTH_TOKEN");

            string gitHubCommentsEnabled = Environment.GetEnvironmentVariable("DISABLE_GITHUB_COMMENTS");
            if (!string.IsNullOrEmpty(gitHubCommentsEnabled))
            {
                bool.TryParse(gitHubCommentsEnabled, out GitHubCommentsDisabled);
            }

            HttpClient = new HttpClient();
            HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DFTestBot", "0.1.0"));
            HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", GitHubOAuthToken);
        }

        internal static bool IsConfigured => !string.IsNullOrEmpty(GitHubOAuthToken);

        public static async Task PostCommentAsync(
            Uri commentApiUrl,
            string markdownComment,
            ILogger log)
        {
            string message = "🤖**Durable Functions Test Bot**🤖" + Environment.NewLine + Environment.NewLine + markdownComment;
            log.LogInformation($"Sending GitHub comment: {message}");

            if (!GitHubCommentsDisabled)
            {
                var newCommentPayload = new { body = message };
                using var request = new HttpRequestMessage(HttpMethod.Post, commentApiUrl)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(newCommentPayload), Encoding.UTF8, "application/json"),
                };

                using HttpResponseMessage response = await HttpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    int statusCode = (int)response.StatusCode;
                    string details = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Failed to post comment: {statusCode}: {details}");
                }
            }
        }

        public static async Task<JObject> GetPullRequestInfoAsync(Uri pullRequestApiUrl)
        {
            using HttpResponseMessage response = await HttpClient.GetAsync(pullRequestApiUrl);
            string content = await GetResponseContentOrThrowAsync(response);
            return JObject.Parse(content);
        }

        static async Task<string> GetResponseContentOrThrowAsync(HttpResponseMessage response)
        {
            string content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;
                HttpRequestMessage request = response.RequestMessage;
                throw new Exception($"HTTP request {request.Method} {request.RequestUri} failed: {statusCode}: {content}");
            }

            return content;
        }
    }
}
