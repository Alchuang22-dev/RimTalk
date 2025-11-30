using System;
using RimTalk.Data;
using RimTalk.Source.Data;
using RimWorld;
using Verse;

namespace RimTalk.Util
{
    /// <summary>
    /// Utility methods for mapping LLM emotion outputs to RimWorld ThoughtDefs
    /// and applying them as simple, non-social mood memories.
    /// </summary>
    public static class RimTalkThoughtUtility
    {
        // 缓存 RimTalk 的三个 ThoughtDef，避免每次都查表
        // 这里的 defName 需要和 Defs/ThoughtDefs.xml 对应：
        //   <defName>Praised</defName>
        //   <defName>Talked with others</defName>
        //   <defName>Insulted</defName>
        private static readonly ThoughtDef PraisedDef =
            DefDatabase<ThoughtDef>.GetNamedSilentFail("RimTalk_Praised");
        private static readonly ThoughtDef TalkedWithOthersDef =
            DefDatabase<ThoughtDef>.GetNamedSilentFail("RimTalk_Chatted");
        private static readonly ThoughtDef InsultedDef =
            DefDatabase<ThoughtDef>.GetNamedSilentFail("RimTalk_Insulted");

        /// <summary>
        /// Apply a simple non-social mood Thought based on the LLM emotion
        /// attached to a TalkResponse. This is called when the pawn actually
        /// speaks (i.e., when TalkService.ConsumeTalk is executed).
        /// </summary>
        /// <param name="pawn">The speaking pawn.</param>
        /// <param name="response">The talk response being spoken.</param>
        public static void ApplyNonSocialMoodFromTalk(Pawn pawn, TalkResponse response)
        {
            if (pawn == null || response == null)
            {
                return;
            }

            // Pawn 没有心情系统（如机械体）时直接跳过
            if (pawn.needs?.mood?.thoughts?.memories == null)
            {
                return;
            }

            var emotion = response.Emotion;
            if (emotion == null)
            {
                // 当前还未从 AIClient 中填充 Emotion 时，这里会直接返回。
                // 如果你想给所有对话一个基础“聊天很愉快”的 buff，可以在这里改成：
                //   TryGainMemory(pawn, TalkedWithOthersDef);
                return;
            }

            var thoughtDef = MapEmotionToThought(emotion, response.TalkType);
            if (thoughtDef == null)
            {
                return;
            }

            TryGainMemory(pawn, thoughtDef);
        }

        /// <summary>
        /// 将 Emotion(Label + Score) 映射到一个具体的 ThoughtDef。
        /// 目前策略：
        /// 1. 如果 Label 中包含 "insult"/"negative"/"angry" → Insulted
        /// 2. 如果 Label 中包含 "praise"/"compliment"/"kind"/"positive" → Praised
        /// 3. 否则根据 Score：
        ///    - Score ≥ 70 → Praised
        ///    - Score ≤ 30 → Insulted
        ///    - 其余 → Talked with others
        /// </summary>
        private static ThoughtDef MapEmotionToThought(TalkEmotionResult emotion, TalkType talkType)
        {
            if (emotion == null)
            {
                return null;
            }

            string label = emotion.Label?.ToLowerInvariant() ?? string.Empty;
            int score = emotion.Score;

            // 1) 先根据标签判断
            if (!string.IsNullOrEmpty(label))
            {
                if (label.Contains("insult") || label.Contains("negative") || label.Contains("angry"))
                {
                    return InsultedDef ?? FallbackByScore(score);
                }

                if (label.Contains("praise") || label.Contains("compliment") ||
                    label.Contains("kind") || label.Contains("positive"))
                {
                    return PraisedDef ?? FallbackByScore(score);
                }

                if (label.Contains("chat") || label.Contains("chitchat") || label.Contains("neutral"))
                {
                    return TalkedWithOthersDef ?? FallbackByScore(score);
                }
            }

            // 2) 标签不明确时，用 Score 粗分三挡
            return FallbackByScore(score);
        }

        /// <summary>
        /// 根据分数粗略选择一个 ThoughtDef。
        /// 假设分数范围在 1-100：
        ///   >= 70 → Praised
        ///   <= 30 → Insulted
        ///   其他 → Talked with others
        ///
        /// 如果以后你改用 -1~1，可以把这里的阈值改一下。
        /// </summary>
        private static ThoughtDef FallbackByScore(int score)
        {
            if (score >= 70)
            {
                return PraisedDef ?? TalkedWithOthersDef;
            }

            if (score <= 30)
            {
                return InsultedDef ?? TalkedWithOthersDef;
            }

            // 中间值默认归为愉快聊天
            return TalkedWithOthersDef ?? PraisedDef ?? InsultedDef;
        }

        private static void TryGainMemory(Pawn pawn, ThoughtDef def)
        {
            if (pawn == null || def == null)
            {
                return;
            }

            try
            {
                pawn.needs.mood.thoughts.memories.TryGainMemory(def);
            }
            catch (Exception)
            {
                // 防止其他 mod 修改 Thought 系统导致崩溃，这里静默失败即可。
            }
        }
    }
}
