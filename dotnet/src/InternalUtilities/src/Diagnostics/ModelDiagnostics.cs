﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Microsoft.SemanticKernel.Diagnostics;

/// <summary>
/// Model diagnostics helper class that provides a set of methods to trace model activities with the OTel semantic conventions.
/// This class contains experimental features and may change in the future.
/// To enable these features, set one of the following switches to true:
///     `Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics`
///     `Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive`
/// Or set the following environment variables to true:
///    `SEMANTICKERNEL_EXPERIMENTAL_GENAI_ENABLE_OTEL_DIAGNOSTICS`
///    `SEMANTICKERNEL_EXPERIMENTAL_GENAI_ENABLE_OTEL_DIAGNOSTICS_SENSITIVE`
/// </summary>
[ExcludeFromCodeCoverage]
internal static class ModelDiagnostics
{
    private static readonly string s_namespace = typeof(ModelDiagnostics).Namespace!;
    private static readonly ActivitySource s_activitySource = new(s_namespace);

    private const string EnableDiagnosticsSwitch = "Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics";
    private const string EnableSensitiveEventsSwitch = "Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive";
    private const string EnableDiagnosticsEnvVar = "SEMANTICKERNEL_EXPERIMENTAL_GENAI_ENABLE_OTEL_DIAGNOSTICS";
    private const string EnableSensitiveEventsEnvVar = "SEMANTICKERNEL_EXPERIMENTAL_GENAI_ENABLE_OTEL_DIAGNOSTICS_SENSITIVE";

    private static readonly bool s_enableDiagnostics = AppContextSwitchHelper.GetConfigValue(EnableDiagnosticsSwitch, EnableDiagnosticsEnvVar);
    private static readonly bool s_enableSensitiveEvents = AppContextSwitchHelper.GetConfigValue(EnableSensitiveEventsSwitch, EnableSensitiveEventsEnvVar);

    /// <summary>
    /// Start a text completion activity for a given model.
    /// The activity will be tagged with the a set of attributes specified by the semantic conventions.
    /// </summary>
    public static Activity? StartCompletionActivity(Uri? endpoint, string modelName, string modelProvider, string prompt, PromptExecutionSettings? executionSettings)
        => StartCompletionActivity(endpoint, modelName, modelProvider, prompt, executionSettings, prompt => prompt);

    /// <summary>
    /// Start a chat completion activity for a given model.
    /// The activity will be tagged with the a set of attributes specified by the semantic conventions.
    /// </summary>
    public static Activity? StartCompletionActivity(Uri? endpoint, string modelName, string modelProvider, ChatHistory chatHistory, PromptExecutionSettings? executionSettings)
        => StartCompletionActivity(endpoint, modelName, modelProvider, chatHistory, executionSettings, ToOpenAIFormat);

    /// <summary>
    /// Set the text completion response for a given activity.
    /// The activity will be enriched with the response attributes specified by the semantic conventions.
    /// </summary>
    public static void SetCompletionResponse(this Activity activity, IEnumerable<TextContent> completions, int? promptTokens = null, int? completionTokens = null)
        => SetCompletionResponse(activity, completions, promptTokens, completionTokens, completions => $"[{string.Join(", ", completions)}]");

    /// <summary>
    /// Set the chat completion response for a given activity.
    /// The activity will be enriched with the response attributes specified by the semantic conventions.
    /// </summary>
    public static void SetCompletionResponse(this Activity activity, IEnumerable<ChatMessageContent> completions, int? promptTokens = null, int? completionTokens = null)
        => SetCompletionResponse(activity, completions, promptTokens, completionTokens, ToOpenAIFormat);

    /// <summary>
    /// Set the response id for a given activity.
    /// </summary>
    /// <param name="activity">The activity to set the response id</param>
    /// <param name="responseId">The response id</param>
    /// <returns>The activity with the response id set for chaining</returns>
    public static Activity SetResponseId(this Activity activity, string responseId) => activity.SetTag(ModelDiagnosticsTags.ResponseId, responseId);

    /// <summary>
    /// Set the prompt token usage for a given activity.
    /// </summary>
    /// <param name="activity">The activity to set the prompt token usage</param>
    /// <param name="promptTokens">The number of prompt tokens used</param>
    /// <returns>The activity with the prompt token usage set for chaining</returns>
    public static Activity SetPromptTokenUsage(this Activity activity, int promptTokens) => activity.SetTag(ModelDiagnosticsTags.PromptToken, promptTokens);

    /// <summary>
    /// Set the completion token usage for a given activity.
    /// </summary>
    /// <param name="activity">The activity to set the completion token usage</param>
    /// <param name="completionTokens">The number of completion tokens used</param>
    /// <returns>The activity with the completion token usage set for chaining</returns>
    public static Activity SetCompletionTokenUsage(this Activity activity, int completionTokens) => activity.SetTag(ModelDiagnosticsTags.CompletionToken, completionTokens);

    # region Private
    /// <summary>
    /// Check if model diagnostics is enabled
    /// Model diagnostics is enabled if either EnableModelDiagnostics or EnableSensitiveEvents is set to true and there are listeners.
    /// </summary>
    private static bool IsModelDiagnosticsEnabled()
    {
        return (s_enableDiagnostics || s_enableSensitiveEvents) && s_activitySource.HasListeners();
    }

    private static void AddOptionalTags(Activity? activity, PromptExecutionSettings? executionSettings)
    {
        if (activity is null || executionSettings?.ExtensionData is null)
        {
            return;
        }

        void TryAddTag(string key, string tag)
        {
            if (executionSettings.ExtensionData.TryGetValue(key, out var value))
            {
                activity.SetTag(tag, value);
            }
        }

        TryAddTag("max_tokens", ModelDiagnosticsTags.MaxToken);
        TryAddTag("temperature", ModelDiagnosticsTags.Temperature);
        TryAddTag("top_p", ModelDiagnosticsTags.TopP);
    }

    /// <summary>
    /// Convert chat history to a string aligned with the OpenAI format
    /// </summary>
    private static string ToOpenAIFormat(IEnumerable<ChatMessageContent> chatHistory)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        var isFirst = true;
        foreach (var message in chatHistory)
        {
            if (!isFirst)
            {
                // Append a comma and a newline to separate the elements after the previous one.
                // This can avoid adding an unnecessary comma after the last element.
                sb.Append(", \n");
            }

            sb.Append("{\"role\": \"");
            sb.Append(message.Role);
            sb.Append("\", \"content\": \"");
            sb.Append(JsonSerializer.Serialize(message.Content));
            sb.Append("\"}");

            isFirst = false;
        }
        sb.Append(']');

        return sb.ToString();
    }

    /// <summary>
    /// Start a completion activity and return the activity.
    /// The `formatPrompt` delegate won't be invoked if events are disabled.
    /// </summary>
    private static Activity? StartCompletionActivity<T>(
        Uri? endpoint,
        string modelName,
        string modelProvider,
        T prompt,
        PromptExecutionSettings? executionSettings,
        Func<T, string> formatPrompt)
    {
        if (!IsModelDiagnosticsEnabled())
        {
            return null;
        }

        string operationName = prompt is ChatHistory ? "chat.completions" : "text.completions";
        var activity = s_activitySource.StartActivityWithTags(
            $"{operationName} {modelName}",
            [
                new(ModelDiagnosticsTags.Operation, operationName),
                new(ModelDiagnosticsTags.System, modelProvider),
                new(ModelDiagnosticsTags.Model, modelName),
            ],
            ActivityKind.Client);

        if (endpoint is not null)
        {
            activity?.SetTags([
                // Skip the query string in the uri as it may contain keys
                new(ModelDiagnosticsTags.Address, endpoint.GetLeftPart(UriPartial.Path)),
                new(ModelDiagnosticsTags.Port, endpoint.Port),
            ]);
        }

        AddOptionalTags(activity, executionSettings);

        if (s_enableSensitiveEvents)
        {
            var formattedContent = formatPrompt(prompt);
            activity?.AttachSensitiveDataAsEvent(
                ModelDiagnosticsTags.PromptEvent,
                [
                    new(ModelDiagnosticsTags.PromptEventPrompt, formattedContent),
                ]);
        }

        return activity;
    }

    /// <summary>
    /// Set the completion response for a given activity.
    /// The `formatCompletions` delegate won't be invoked if events are disabled.
    /// </summary>
    private static void SetCompletionResponse<T>(
        Activity activity,
        T completions,
        int? promptTokens,
        int? completionTokens,
        Func<T, string> formatCompletions) where T : IEnumerable<KernelContent>
    {
        if (!IsModelDiagnosticsEnabled())
        {
            return;
        }

        if (promptTokens != null)
        {
            activity.SetTag(ModelDiagnosticsTags.PromptToken, promptTokens);
        }

        if (completionTokens != null)
        {
            activity.SetTag(ModelDiagnosticsTags.CompletionToken, completionTokens);
        }

        activity
            .SetFinishReasons(completions)
            .SetResponseId(completions.FirstOrDefault());

        if (s_enableSensitiveEvents)
        {
            activity.AttachSensitiveDataAsEvent(
                ModelDiagnosticsTags.CompletionEvent,
                [
                    new(ModelDiagnosticsTags.CompletionEventCompletion, formatCompletions(completions)),
                ]);
        }
    }

    // Returns an activity for chaining
    private static Activity SetFinishReasons(this Activity activity, IEnumerable<KernelContent> completions)
    {
        var finishReasons = completions.Select(c =>
        {
            if (c.Metadata?.TryGetValue("FinishReason", out var finishReason) == true && !string.IsNullOrEmpty(finishReason as string))
            {
                return finishReason;
            }

            return "N/A";
        });

        if (finishReasons.Any())
        {
            activity.SetTag(ModelDiagnosticsTags.FinishReason, $"{string.Join(",", finishReasons)}");
        }

        return activity;
    }

    // Returns an activity for chaining
    private static Activity SetResponseId(this Activity activity, KernelContent? completion)
    {
        if (completion?.Metadata?.TryGetValue("Id", out var id) == true && !string.IsNullOrEmpty(id as string))
        {
            activity.SetTag(ModelDiagnosticsTags.ResponseId, id);
        }

        return activity;
    }

    /// <summary>
    /// Tags used in model diagnostics
    /// </summary>
    private static class ModelDiagnosticsTags
    {
        // Activity tags
        public const string System = "gen_ai.system";
        public const string Operation = "gen_ai.operation.name";
        public const string Model = "gen_ai.request.model";
        public const string MaxToken = "gen_ai.request.max_tokens";
        public const string Temperature = "gen_ai.request.temperature";
        public const string TopP = "gen_ai.request.top_p";
        public const string ResponseId = "gen_ai.response.id";
        public const string ResponseModel = "gen_ai.response.model";
        public const string FinishReason = "gen_ai.response.finish_reason";
        public const string PromptToken = "gen_ai.response.prompt_tokens";
        public const string CompletionToken = "gen_ai.response.completion_tokens";
        public const string Prompt = "gen_ai.content.prompt";
        public const string Completion = "gen_ai.content.completion";
        public const string Address = "server.address";
        public const string Port = "server.port";

        // Activity events
        public const string PromptEvent = "gen_ai.content.prompt";
        public const string PromptEventPrompt = "gen_ai.prompt";
        public const string CompletionEvent = "gen_ai.content.completion";
        public const string CompletionEventCompletion = "gen_ai.completion";
    }
    # endregion
}
