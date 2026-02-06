using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services;
using Xunit;

namespace TinyGenerator.Tests;

public sealed class AddVoiceTagsDeterministicValidationTests
{
    private sealed class TrackingServiceProvider : IServiceProvider
    {
        public bool WasAskedForChecker { get; private set; }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ResponseCheckerService))
            {
                WasAskedForChecker = true;
                return null;
            }

            return null;
        }
    }

    private static LangChainChatBridge CreateBridgeForValidationOnly(IServiceProvider? services = null)
    {
        return new LangChainChatBridge(
            modelEndpoint: "http://localhost:1234",
            modelId: "dummy",
            apiKey: "",
            httpClient: null,
            logger: null,
            services: services);
    }

    private static List<ConversationMessage> BuildMessagesWithDialogueIds(params int[] ids)
    {
        var dialogueLines = new List<string>();
        foreach (var id in ids)
        {
            dialogueLines.Add($"{id:000} \"ciao\"");
        }

        var userContent =
            "RIGHE DI DIALOGO (tra virgolette) DA TAGGARE CON PERSONAGGIO+EMOZIONE:\n"
            + string.Join("\n", dialogueLines)
            + "\n\n"
            + "TESTO COMPLETO (righe numerate):\n"
            + string.Join("\n", dialogueLines);

        return new List<ConversationMessage>
        {
            new() { Role = "user", Content = userContent },
        };
    }

    private static string WrapAsChatResponseJson(string content)
    {
        // Minimal OpenAI-like response envelope for ParseChatResponseWithFinishReason
        return "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(content) + "},\"finish_reason\":\"stop\"}]}";
    }

    private static async Task<ValidationResult> InvokeValidateResponseJsonAsync(
        LangChainChatBridge bridge,
        List<ConversationMessage> messages,
        string responseJson,
        bool enableChecker)
    {
        var method = typeof(LangChainChatBridge).GetMethod(
            "ValidateResponseJsonAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var task = (Task<ValidationResult>)method!.Invoke(
            bridge,
            new object[]
            {
                messages,
                new List<Dictionary<string, object>>(),
                responseJson,
                new ResponseValidationOptions { Enabled = true },
                enableChecker,
                Array.Empty<ResponseValidationRule>(),
                null!, // primaryResponseLogId
                CancellationToken.None,
            })!;

        return await task.ConfigureAwait(false);
    }

    [Fact]
    public async Task AddVoiceTags_MissingRequestedId_FailsDeterministically()
    {
        using var _ = LogScope.Push("story/add_voice_tags_to_story");
        var bridge = CreateBridgeForValidationOnly();
        var messages = BuildMessagesWithDialogueIds(1, 2);

        var mapping = "001 [PERSONAGGIO: A] [EMOZIONE: felice]"; // missing 002
        var json = WrapAsChatResponseJson(mapping);

        var result = await InvokeValidateResponseJsonAsync(bridge, messages, json, enableChecker: false);

        Assert.False(result.IsValid);
        Assert.True(result.NeedsRetry);
        Assert.Contains("Mancano tag", result.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddVoiceTags_MissingEmotionTag_FailsDeterministically()
    {
        using var _ = LogScope.Push("story/add_voice_tags_to_story");
        var bridge = CreateBridgeForValidationOnly();
        var messages = BuildMessagesWithDialogueIds(1);

        var mapping = "001 [PERSONAGGIO: A]";
        var json = WrapAsChatResponseJson(mapping);

        var result = await InvokeValidateResponseJsonAsync(bridge, messages, json, enableChecker: false);

        Assert.False(result.IsValid);
        Assert.True(result.NeedsRetry);
        Assert.Contains("EMOZIONE", result.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddVoiceTags_ValidMapping_PassesDeterministically_WhenCheckerDisabled()
    {
        using var _ = LogScope.Push("story/add_voice_tags_to_story");
        var bridge = CreateBridgeForValidationOnly();
        var messages = BuildMessagesWithDialogueIds(1, 2);

        var mapping =
            "001 [PERSONAGGIO: A] [EMOZIONE: felice]\n"
            + "002 [PERSONAGGIO: B] [EMOZIONE: neutra]";
        var json = WrapAsChatResponseJson(mapping);

        var result = await InvokeValidateResponseJsonAsync(bridge, messages, json, enableChecker: false);

        Assert.True(result.IsValid);
        Assert.False(result.NeedsRetry);
    }

    [Fact]
    public async Task AddVoiceTags_DeterministicFail_DoesNotResolveChecker_WhenCheckerEnabled()
    {
        using var _ = LogScope.Push("story/add_voice_tags_to_story");
        var tracking = new TrackingServiceProvider();
        var bridge = CreateBridgeForValidationOnly(tracking);
        var messages = BuildMessagesWithDialogueIds(1, 2);

        var mapping = "001 [PERSONAGGIO: A] [EMOZIONE: felice]"; // missing 002 => deterministic fail
        var json = WrapAsChatResponseJson(mapping);

        var result = await InvokeValidateResponseJsonAsync(bridge, messages, json, enableChecker: true);

        Assert.False(result.IsValid);
        Assert.True(result.NeedsRetry);
        Assert.False(tracking.WasAskedForChecker);
    }

    [Fact]
    public async Task AddVoiceTags_DeterministicPass_ResolvesChecker_WhenCheckerEnabled()
    {
        using var _ = LogScope.Push("story/add_voice_tags_to_story");
        var tracking = new TrackingServiceProvider();
        var bridge = CreateBridgeForValidationOnly(tracking);
        var messages = BuildMessagesWithDialogueIds(1);

        var mapping = "001 [PERSONAGGIO: A] [EMOZIONE: neutra]";
        var json = WrapAsChatResponseJson(mapping);

        var result = await InvokeValidateResponseJsonAsync(bridge, messages, json, enableChecker: true);

        Assert.True(tracking.WasAskedForChecker);
        // Checker is not provided by this test provider, so bridge will skip checker and accept.
        Assert.True(result.IsValid);
    }
}
