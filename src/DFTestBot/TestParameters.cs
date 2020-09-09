// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using Newtonsoft.Json;

namespace DFTestBot
{
    class TestParameters
    {
        /// <summary>
        /// Gets or sets the subscription ID to deploy the test app into.
        /// </summary>
        [JsonProperty("subscriptionId")]
        public string SubscriptionId { get; set; }

        /// <summary>
        /// Gets or sets the resource group to deploy the test app into.
        /// </summary>
        [JsonProperty("resourceGroup")]
        public string ResourceGroup { get; set; }

        /// <summary>
        /// Gets or sets the name of the app to deploy.
        /// </summary>
        [JsonProperty("appName")]
        public string AppName { get; set; }

        /// <summary>
        /// Gets or sets the name of the test to run.
        /// </summary>
        [JsonProperty("testName")]
        public string TestName { get; set; }

        /// <summary>
        /// Gets or sets parameters for running the test.
        /// </summary>
        [JsonProperty("testParameters")]
        public string Parameters { get; set; }

        /// <summary>
        /// Gets or sets the name of the AppLens detector to use for analyzing results.
        /// </summary>
        [JsonProperty("detectorName")]
        public string DetectorName { get; set; }

        /// <summary>
        /// Gets or sets the GitHub pull request comment API URL e.g. https://api.github.com/repos/Azure/azure-functions-durable-extension/issues/1464/comments.
        /// </summary>
        [JsonProperty("gitHubCommentApiUrl")]
        public Uri GitHubCommentApiUrl { get; set; }

        /// <summary>
        /// Gets or sets the GitHub branch to build e.g. "cgillum/perf-testing"
        /// </summary>
        [JsonProperty("branchName")]
        public string GitHubBranch { get; set; }
    }
}
