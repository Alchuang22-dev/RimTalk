using System;
using System.Runtime.Serialization;
using RimTalk.Source.Data;

namespace RimTalk.Data;

[DataContract]
public class TalkResponse(TalkType talkType, string name, string text) : IJsonData
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public TalkType TalkType { get; set; } = talkType;

    [DataMember(Name = "name")] public string Name { get; set; } = name;

    [DataMember(Name = "text")] public string Text { get; set; } = text;

    public Guid ParentTalkId { get; set; }

    /// <summary>
    /// LLM 对本次回复给出的情绪/奖励结果。
    /// 由 AIService 解析，并在 TalkService 中用于施加 Thought。
    /// 可以为空（例如旧版本 provider 或未开启情绪功能时）。
    /// </summary>
    [DataMember(Name = "emotion", EmitDefaultValue = false)]
    public TalkEmotionResult Emotion { get; set; }
    
    public bool IsReply()
    {
        return ParentTalkId != Guid.Empty;
    }
        
    public override string ToString()
    {
        return Text;
    }
}

/// <summary>
/// 一次对话的情绪结果：数值 + 标签
/// 例如：Score=85, Label="praise_target"
/// </summary>
[DataContract]
public class TalkEmotionResult
{
    /// <summary>
    /// 数值奖励（1-100 或其他约定范围）。
    /// </summary>
    [DataMember(Name = "score")]
    public int Score { get; set; }

    /// <summary>
    /// 情绪标签（例如 "praise", "insult", "chitchat" 等）。
    /// 具体枚举先由 Prompt 协议约定，用字符串方便多模型兼容。
    /// </summary>
    [DataMember(Name = "label")]
    public string Label { get; set; }

    public TalkEmotionResult()
    {
    }

    public TalkEmotionResult(int score, string label)
    {
        Score = score;
        Label = label;
    }

    public override string ToString()
    {
        return $"{Label}({Score})";
    }
}