using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace OniAdvisor.Llm
{
    /// <summary>A provider-neutral conversation: what the advisor said and did, whichever model it talks to.</summary>
    public enum Role { User, Assistant }

    public abstract class Block { }

    public sealed class TextBlock : Block
    {
        public string Text;
        public TextBlock(string text) { Text = text; }
    }

    /// <summary>A JPEG or PNG, base64-encoded (a screenshot or map image).</summary>
    public sealed class ImageBlock : Block
    {
        public string MediaType, Base64;
        public ImageBlock(string mediaType, string base64) { MediaType = mediaType; Base64 = base64; }
    }

    /// <summary>The model asks to run one of our tools.</summary>
    public sealed class ToolCallBlock : Block
    {
        public string Id, Name;
        public JObject Input;      // null when the model's arguments were not valid JSON
        public string RawInput;    // the arguments as the model wrote them
        public ToolCallBlock(string id, string name, JObject input, string rawInput) { Id = id; Name = name; Input = input; RawInput = rawInput; }
    }

    public sealed class ToolResultBlock : Block
    {
        public string CallId, Content;
        public bool IsError;
        public ToolResultBlock(string callId, string content, bool isError = false) { CallId = callId; Content = content; IsError = isError; }
    }

    /// <summary>
    /// A block only its own provider understands (e.g. Anthropic thinking blocks, which must be sent back
    /// unchanged). Replayed to that provider, skipped for any other.
    /// </summary>
    public sealed class OpaqueBlock : Block
    {
        public string Provider;
        public JObject Raw;
        public OpaqueBlock(string provider, JObject raw) { Provider = provider; Raw = raw; }
    }

    public sealed class Message
    {
        public Role Role;
        public List<Block> Blocks = new List<Block>();
        public Message(Role role, params Block[] blocks) { Role = role; Blocks.AddRange(blocks); }
        public string Text => string.Concat(Blocks.OfType<TextBlock>().Select(b => b.Text));
    }

    /// <summary>The message history. Appending to the same role as the last message merges into it, as the APIs require alternating roles.</summary>
    public sealed class Conversation
    {
        public readonly List<Message> Messages = new List<Message>();

        public void Add(Role role, params Block[] blocks)
        {
            if (Messages.Count > 0 && Messages[Messages.Count - 1].Role == role) Messages[Messages.Count - 1].Blocks.AddRange(blocks);
            else Messages.Add(new Message(role, blocks));
        }

        public void AddUser(string text) => Add(Role.User, new TextBlock(text));
    }

    /// <summary>A tool the model may call: a name, what it does, and a JSON Schema for its input.</summary>
    public sealed class ToolDefinition
    {
        public string Name, Description;
        public JObject InputSchema;
        public ToolDefinition(string name, string description, JObject inputSchema) { Name = name; Description = description; InputSchema = inputSchema; }
    }

    public sealed class ChatRequest
    {
        public string Model;
        /// <summary>The stable part of the instructions (cached where the provider supports it).</summary>
        public string System;
        public Conversation Conversation;
        public List<ToolDefinition> Tools = new List<ToolDefinition>();
        public int MaxTokens = 16000;
        /// <summary>low | medium | high (where the model supports it); null = the model's default.</summary>
        public string Effort;
    }

    public enum StopReason { EndTurn, ToolUse, MaxTokens, Refusal, Other }

    public sealed class Usage
    {
        public long InputTokens, OutputTokens, CacheWriteTokens, CacheReadTokens;
        public void Add(Usage u) { InputTokens += u.InputTokens; OutputTokens += u.OutputTokens; CacheWriteTokens += u.CacheWriteTokens; CacheReadTokens += u.CacheReadTokens; }
        public override string ToString() => $"in {InputTokens} (+{CacheReadTokens} cached, {CacheWriteTokens} written), out {OutputTokens}";
    }

    public sealed class ChatResponse
    {
        public Message Message;
        public StopReason Stop;
        public string RawStopReason;
        public Usage Usage = new Usage();
        public string Model;
        public IEnumerable<ToolCallBlock> ToolCalls => Message.Blocks.OfType<ToolCallBlock>();
    }

    /// <summary>What a streaming response reports as it arrives.</summary>
    public interface IStreamListener
    {
        void OnText(string delta);
        /// <summary>A tool call has started (its input is still arriving).</summary>
        void OnToolCallStart(string name);
    }

    public sealed class NullListener : IStreamListener
    {
        public static readonly NullListener Instance = new NullListener();
        public void OnText(string delta) { }
        public void OnToolCallStart(string name) { }
    }
}
