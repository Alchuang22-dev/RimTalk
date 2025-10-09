using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimTalk.Data;
using RimTalk.Source.Data;
using RimTalk.UI;
using RimTalk.Util;
using RimWorld;
using Verse;
using Cache = RimTalk.Data.Cache;
using Logger = RimTalk.Util.Logger;

namespace RimTalk.Service;

/// <summary>
/// Core service for generating and managing AI-driven conversations between pawns.
/// </summary>
public static class TalkService
{
    /// <summary>
    /// Initiates the process of generating a conversation. It performs initial checks and then
    /// starts a background task to handle the actual AI communication.
    /// </summary>
    public static bool GenerateTalk(TalkRequest talkRequest)
    {
        // Guard clauses to prevent generation when the feature is disabled or the AI service is busy.
        var settings = Settings.Get();
        if (!settings.IsEnabled || !CommonUtil.ShouldAiBeActiveOnSpeed()) return false;
        if (settings.GetActiveConfig() == null) return false;
        if (AIService.IsBusy()) return false;

        PawnState pawn1 = Cache.Get(talkRequest.Initiator);
        if (pawn1 == null || !pawn1.CanGenerateTalk()) return false;

        // Ensure the recipient is valid and capable of talking.
        PawnState pawn2 = talkRequest.Recipient != null ? Cache.Get(talkRequest.Recipient) : null;
        if (pawn2 == null || talkRequest.Recipient?.Name == null || !pawn2.CanDisplayTalk())
        {
            talkRequest.Recipient = null;
        }

        List<Pawn> nearbyPawns = PawnSelector.GetAllNearByPawns(talkRequest.Initiator);
        var (status, isInDanger) = PawnService.GetPawnStatusFull(talkRequest.Initiator, nearbyPawns);
        if (isInDanger) talkRequest.TalkType = TalkType.Urgent;

        // Avoid spamming generations if the pawn's status hasn't changed recently.
        if (status == pawn1.LastStatus && pawn1.RejectCount < 2)
        {
            pawn1.RejectCount++;
            return false;
        }

        pawn1.RejectCount = 0;
        pawn1.LastStatus = status;

        // Select the most relevant pawns for the conversation context.
        List<Pawn> pawns = new List<Pawn> { talkRequest.Initiator, talkRequest.Recipient }
            .Where(p => p != null)
            .Concat(nearbyPawns.Where(p => Cache.Get(p).CanDisplayTalk()))
            .Distinct()
            .Take(3)
            .ToList();

        // Build the context and decorate the prompt with current status information.
        string context = PromptService.BuildContext(pawns);
        AIService.UpdateContext(context);
        PromptService.DecoratePrompt(talkRequest, pawns, status);

        var allInvolvedPawns = pawns.Union(nearbyPawns).Distinct().ToList();

        // Offload the AI request and processing to a background thread to avoid blocking the game's main thread.
        Task.Run(() => GenerateAndProcessTalkAsync(talkRequest, allInvolvedPawns));

        return true;
    }

    /// <summary>
    /// Handles the asynchronous AI streaming and processes the responses.
    /// </summary>
    private static async Task GenerateAndProcessTalkAsync(TalkRequest talkRequest, List<Pawn> allInvolvedPawns)
    {
        var initiator = talkRequest.Initiator;
        try
        {
            Cache.Get(initiator).IsGeneratingTalk = true;

            // Create a dictionary for quick pawn lookup by name during streaming.
            var playerDict = allInvolvedPawns.ToDictionary(p => p.LabelShort, p => p);
            var receivedResponses = new List<TalkResponse>();

            // Call the streaming chat service. The callback is executed as each piece of dialogue is parsed.
            await AIService.ChatStreaming(
                talkRequest,
                TalkHistory.GetMessageHistory(initiator),
                playerDict,
                (pawn, talkResponse) =>
                {
                    Logger.Debug($"Streamed {pawn.LabelShort}: {talkResponse.TalkType}: {talkResponse.Text}");

                    PawnState pawnState = Cache.Get(pawn);
                    talkResponse.Name = pawnState.Pawn.LabelShort;

                    // Link replies to the previous message in the conversation.
                    if (receivedResponses.Any())
                    {
                        talkResponse.ParentTalkId = receivedResponses.Last().Id;
                    }

                    receivedResponses.Add(talkResponse);

                    // Enqueue the received talk for the pawn to display later.
                    pawnState.TalkResponses.Enqueue(talkResponse);
                }
            );

            // Once the stream is complete, save the full conversation to history.
            AddResponsesToHistory(allInvolvedPawns, receivedResponses, talkRequest.Prompt);
        }
        catch (Exception ex)
        {
            Logger.Error(ex.StackTrace);
        }
        finally
        {
            Cache.Get(initiator).IsGeneratingTalk = false;
        }
    }

    /// <summary>
    /// Serializes the generated responses and adds them to the message history for all involved pawns.
    /// </summary>
    private static void AddResponsesToHistory(List<Pawn> pawns, List<TalkResponse> responses, string prompt)
    {
        if (!responses.Any()) return;

        string cleanedPrompt = prompt.Replace(Constant.Prompt, "");
        string serializedResponses = JsonUtil.SerializeToJson(responses);

        foreach (var pawn in pawns)
        {
            TalkHistory.AddMessageHistory(pawn, cleanedPrompt, serializedResponses);
        }
    }

    /// <summary>
    /// Iterates through all pawns on each game tick to display any queued talks.
    /// </summary>
    public static void DisplayTalk()
    {
        foreach (Pawn pawn in Cache.Keys)
        {
            PawnState pawnState = Cache.Get(pawn);
            if (pawnState == null || pawnState.TalkResponses.Empty()) continue;

            var talk = pawnState.TalkResponses.Peek();
            if (talk == null)
            {
                pawnState.TalkResponses.Dequeue();
                continue;
            }

            // Skip this talk if its parent was ignored or the pawn is currently unable to speak.
            if (TalkHistory.IsTalkIgnored(talk.ParentTalkId) || !pawnState.CanDisplayTalk())
            {
                ConsumeTalk(pawnState, true);
                continue;
            }

            if (!talk.IsReply() && !CommonUtil.HasPassed(pawnState.LastTalkTick, Settings.Get().TalkInterval))
            {
                continue;
            }

            int replyInterval = RimTalkSettings.ReplyInterval;
            if (PawnService.IsPawnInDanger(pawn))
            {
                replyInterval = 2;
                while (pawnState.TalkResponses.Count > 0)
                {
                    talk = pawnState.TalkResponses.Peek();
                    if (talk.TalkType == TalkType.Urgent)
                        break;

                    ConsumeTalk(pawnState, true);
                }
                if (pawnState.TalkResponses.Empty())
                    continue;
            }

            // Enforce a delay for replies to make conversations feel more natural.
            int parentTalkTick = TalkHistory.GetSpokenTick(talk.ParentTalkId);
            if (parentTalkTick == -1 || !CommonUtil.HasPassed(parentTalkTick, replyInterval)) continue;

            // Create the interaction log entry, which triggers the display of the talk bubble in-game.
            InteractionDef intDef = DefDatabase<InteractionDef>.GetNamed("RimTalkInteraction");
            var playLogEntryInteraction = new PlayLogEntry_RimTalkInteraction(intDef, pawn, pawn, null);

            Find.PlayLog.Add(playLogEntryInteraction);
            break; // Display only one talk per tick to prevent overwhelming the screen.
        }
    }

    /// <summary>
    /// Retrieves the text for a pawn's current talk. Called by the game's UI system.
    /// </summary>
    public static string GetTalk(Pawn pawn)
    {
        PawnState pawnState = Cache.Get(pawn);
        if (pawnState == null) return null;

        TalkResponse talkResponse = ConsumeTalk(pawnState);
        pawnState.LastTalkTick = GenTicks.TicksGame;

        return talkResponse.Text;
    }

    /// <summary>
    /// Dequeues a talk and updates its history as either spoken or ignored.
    /// </summary>
    private static TalkResponse ConsumeTalk(PawnState pawnState, bool ignored = false)
    {
        TalkResponse talkResponse = pawnState.TalkResponses.Dequeue();
        if (ignored)
        {
            TalkHistory.AddIgnored(talkResponse.Id);
        }
        else
        {
            TalkHistory.AddSpoken(talkResponse.Id);
            var apiLog = ApiHistory.GetApiLog(talkResponse.Id);
            if (apiLog != null)
                apiLog.SpokenTick = GenTicks.TicksGame;
            ;

            Overlay.NotifyLogUpdated();
        }

        return talkResponse;
    }
}