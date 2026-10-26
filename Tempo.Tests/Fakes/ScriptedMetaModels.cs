using Tempo.Core.Configuration;
using Tempo.Core.Models;

namespace Tempo.Tests.Fakes;

/// <summary>
/// A fake IMetaModels that replays a scripted sequence, so agent behaviour is tested
/// without network (agent-loop spec: Scripted model for tests). Chat turns are dequeued
/// in order; typed results are dequeued from a shared object queue.
/// </summary>
public sealed class ScriptedMetaModels : IMetaModels
{
    private readonly Queue<ChatResult> chatTurns = new();
    private readonly Queue<object> typedResults = new();

    public List<IReadOnlyList<ChatMessage>> ChatInvocations { get; } = [];
    public List<IReadOnlyList<ToolDefinition>> ToolsSeen { get; } = [];
    public string Transcript { get; set; } = "";
    public string TranscriptLanguage { get; set; } = "en";

    public ScriptedMetaModels EnqueueToolCalls(params ToolCall[] calls)
    {
        chatTurns.Enqueue(new ChatResult(null, calls, ModelProvider.MetaApi));
        return this;
    }

    public ScriptedMetaModels EnqueueFinal(string text)
    {
        chatTurns.Enqueue(new ChatResult(text, [], ModelProvider.MetaApi));
        return this;
    }

    public ScriptedMetaModels EnqueueTyped(object value)
    {
        typedResults.Enqueue(value);
        return this;
    }

    public Task<ChatResult> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
        ChatCallOptions? options = null, CancellationToken ct = default)
    {
        ChatInvocations.Add(messages);
        ToolsSeen.Add(tools);
        if (chatTurns.Count == 0)
            throw new InvalidOperationException("ScriptedMetaModels: no chat turn scripted for this call");
        return Task.FromResult(chatTurns.Dequeue());
    }

    public Task<ModelResult<T>> ChatJsonAsync<T>(IReadOnlyList<ChatMessage> messages, string jsonSchema,
        string purpose = "unspecified", CancellationToken ct = default)
        => Task.FromResult(new ModelResult<T>((T)typedResults.Dequeue(), ModelProvider.MetaApi));

    public Task<ModelResult<T>> DescribeImageAsync<T>(ReadOnlyMemory<byte> image, string contentType,
        string instruction, string jsonSchema, string purpose = "unspecified", CancellationToken ct = default)
        => Task.FromResult(new ModelResult<T>((T)typedResults.Dequeue(), ModelProvider.MetaApi));

    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<byte> audio, string contentType, CancellationToken ct = default)
        => Task.FromResult(new TranscriptionResult(Transcript, TranscriptLanguage, ModelProvider.MetaApi));
}
