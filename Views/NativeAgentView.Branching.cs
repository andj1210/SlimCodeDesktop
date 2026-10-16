// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SlimCodeDesktop.Themes;

using SlimCodeDesktop.Agents.Native;
using SlimCodeDesktop.Agents.Native.Config;
using SlimCodeDesktop.Agents.Native.Llm;
using SlimCodeDesktop.Agents.Native.Mcp;
using SlimCodeDesktop.Agents.Native.Permissions;
using SlimCodeDesktop.Agents.Native.Sessions;
using SlimCodeDesktop.Agents.Native.Skills;
using SlimCodeDesktop.Agents.Native.Tools;
using SlimCodeDesktop.Core;
using SlimCodeDesktop.Views.Native;
using SkiaSharp;

// `using Avalonia;` brings the Avalonia.Platform namespace into scope, which
// makes the bare name `Platform` ambiguous with our own static class.
using CorePlatform = SlimCodeDesktop.Core.Platform;

namespace SlimCodeDesktop.Views;

/// <summary>
/// Revert and branch: forking the persisted transcript at a user or
/// assistant bubble into a new session.
/// </summary>
public partial class NativeAgentView
{
   /// <summary>
   /// Re-evaluate which assistant turns offer the branch footer.
   ///
   /// A turn that reached the transcript forks at its own record, so it stays
   /// branchable for the rest of the session — all it needs is not to be
   /// the one currently streaming.
   ///
   /// A turn that ended without a record — cancelled mid-stream, or killed
   /// by a stream error — has nothing to fork AT, but the transcript
   /// already ends exactly where that turn began, so forking at the tail
   /// re-runs it against the identical context. That only holds for the LAST
   /// bubble and only while nothing is running: once another turn has been
   /// sent, undoing this one means dropping the message after it, which is
   /// revert's job and not this one's.
   /// </summary>
   private void _RefreshBranchAffordances()
   {
      var tail = _assistantBubbles.Count > 0 ? _assistantBubbles[^1] : null;
      foreach (var b in _assistantBubbles)
      {
         bool branchable = b.RecordOrdinal >= 0
            ? !ReferenceEquals(b, _currentAssistant)
            : !_running && ReferenceEquals(b, tail) && b.HasContent;
         b.EnableBranching(branchable);
      }
   }

   /// <summary>
   /// Fork the conversation at <paramref name="bubble"/>: write a new
   /// session file containing everything that came strictly before this
   /// user message, switch the view to it, and re-seed the chat input
   /// with the reverted text so the user can edit and resend.
   /// </summary>
   private void OnRevertRequested(UserBubble bubble)
   {
      if (_running)
      {
         AppendError("Cancel the running agent before reverting.");
         return;
      }

      int targetUserOrdinal = _userBubbles.IndexOf(bubble);
      if (targetUserOrdinal < 0) return;

      var revertedText   = bubble.Text;
      var revertedImages = bubble.Images;

      var forkId = _ForkAtRecord(
         records => _FindNthMessage(records, "user", targetUserOrdinal, _ => true),
         TranscriptStore.FORK_REVERT);
      if (forkId == null) return;

      // Tear down and rebuild against the new file. ResumeSession handles
      // the visual replay, history rebuild, gate session-id update, and
      // gauge rewinding.
      Reset();
      ResumeSession(forkId);

      TxtInput.Text = revertedText;
      // Restage the attachments alongside the text: the reverted turn is
      // being re-authored, and its images are part of it.
      Attachments.Reset(revertedImages);
      TxtInput.Focus();
   }

   /// <summary>
   /// Branch at <paramref name="bubble"/> and re-run that assistant turn:
   /// everything from this turn onwards is dropped from the fork and the
   /// model answers again against the identical preceding context.
   ///
   /// A turn that never reached the transcript (cancelled mid-stream) forks at
   /// the tail rather than at its own record — see
   /// <see cref="_RefreshBranchAffordances"/> for why those are the same
   /// point. Nothing is dropped there, because nothing was written.
   ///
   /// The version being replaced is not lost — it stays in the session we
   /// fork away from, which the picker lists as this one's parent. A cancelled
   /// turn is the exception: it was never persisted, so there is nothing to
   /// keep and the fork is a plain retry.
   /// </summary>
   private async void OnBranchRequested(AssistantBubble bubble)
   {
      if (_running)
      {
         AppendError("Cancel the running agent before branching.");
         return;
      }
      if (_loop == null) return;

      int ordinal = bubble.RecordOrdinal;

      var forkId = _ForkAtRecord(
         records => ordinal < 0
            ? records.Count
            : _FindNthMessage(records, "assistant", ordinal, _RendersAsBubble),
         TranscriptStore.FORK_REGENERATE);
      if (forkId == null)
      {
         AppendError("Could not branch this session.");
         return;
      }

      Reset();
      ResumeSession(forkId);
      if (_loop == null) return;

      if (_loop!.Temperature is <= 0f)
      {
         // Greedy decoding is deterministic: same context in, same tokens out.
         // Say so rather than let the user click twice and conclude the button
         // is broken. Not silently corrected — sampling is their setting.
         AppendItem(new NoticeLine(
            "temperature is pinned to 0 — a plain regenerate will reproduce the same answer; "
            + "raise it in configuration"));
      }

      await _RunTurn(ct => _loop!.ContinueAsync(ct));
   }

   // Which assistant turns get a bubble: one with neither an answer nor a
   // captured trace renders as tool cards only, so it is not a branch point
   // and must not consume an ordinal. The two overloads must agree — the live
   // path counts messages, the replay path counts the records they became.
   private static bool _RendersAsBubble(TranscriptRecord r) =>
      !string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ReasoningContent);

   private static bool _RendersAsBubble(ChatMessage m) =>
      !string.IsNullOrEmpty(m.Content) || !string.IsNullOrEmpty(m.ReasoningContent);

   // Index of the nth "msg" record with the given role that satisfies
   // `counts`, or -1. Only records that count consume an ordinal, which is
   // what makes this agree with the ordinals stamped on the bubbles.
   private static int _FindNthMessage(
      IReadOnlyList<TranscriptRecord> records, string role, int ordinal,
      Func<TranscriptRecord, bool> counts)
   {
      int seen = 0;
      for (int i = 0; i < records.Count; i++)
      {
         var r = records[i];
         if (r.Type != "msg" || r.Role != role) continue;
         if (!counts(r)) continue;
         if (seen == ordinal) return i;
         seen++;
      }
      return -1;
   }

   /// <summary>
   /// "Name" -> "Name - Fork". A fork of a fork does NOT stack suffixes: the
   /// lineage is already drawn by the picker, and "X - Fork - Fork - Fork"
   /// costs caption length to say what the indentation says better. Two forks
   /// of one parent therefore share a name — they're told apart by their
   /// position and timestamp, and either can be renamed.
   /// </summary>
   private static string _ForkTitle(string caption)
   {
      const string SUFFIX = " - Fork";

      var baseName = caption;
      while (baseName.EndsWith(SUFFIX, StringComparison.OrdinalIgnoreCase))
         baseName = baseName.Substring(0, baseName.Length - SUFFIX.Length).TrimEnd();

      if (baseName.Length == 0) baseName = caption;

      int max = SessionTitler.TITLE_MAX_CHARS - SUFFIX.Length;
      if (baseName.Length > max) baseName = baseName.Substring(0, max).TrimEnd();
      return baseName + SUFFIX;
   }

   /// <summary>
   /// Copy this session into a new one containing everything strictly before
   /// the record <paramref name="findSlicePoint"/> selects, and record where
   /// it came from. Returns the new session id, or null if anything about the
   /// slice didn't resolve.
   ///
   /// A slice point of <c>records.Count</c> is legal and copies the whole
   /// transcript — that is how a turn which never reached the file (cancelled
   /// mid-stream) is retried: there is nothing of it to drop.
   ///
   /// The fork is a full copy, never a reference: it must stay readable and
   /// resumable after the session it came from is deleted. The recorded parent
   /// id is for the picker's benefit only.
   /// </summary>
   private string? _ForkAtRecord(
      Func<IReadOnlyList<TranscriptRecord>, int> findSlicePoint, string forkKind)
   {
      if (_agent == null || _projectPath == null || _transcriptStore == null)
         return null;

      var oldPath = _transcriptStore.FilePath;
      var records = TranscriptStore.Read(oldPath);
      if (records.Count == 0) return null;

      int sliceEndExclusive = findSlicePoint(records);
      if (sliceEndExclusive < 0) return null;

      // Pull forward whatever the original meta carried — model, system
      // prompt, project path — so the fork inherits the same conversation
      // identity. The fork gets a fresh sessionId and startedAt.
      //
      // The meta model stays the one this conversation *started* on, even
      // when the fork will run on a different one: it dates the replayed
      // history above the branch point. The switch to the model the fork
      // actually uses is written as a marker at the tail (see below), so the
      // replayed sequence of "model switched" notices reads in order.
      string metaModel       = "";
      string metaProvider    = "";
      string metaPrompt      = "";
      string metaProjectPath = _projectPath;
      foreach (var r in records)
      {
         if (r.Type != "meta") continue;
         metaModel       = r.Model        ?? metaModel;
         metaProvider    = r.Provider     ?? metaProvider;
         metaPrompt      = r.SystemPrompt ?? metaPrompt;
         metaProjectPath = r.ProjectPath  ?? metaProjectPath;
         break;
      }

      // What the fork will actually talk to: this tab's live selection. A
      // fork is a turn the user is about to author, so their current pick
      // beats whatever happened to be active at the branch point — otherwise
      // "switch model, then revert/regenerate" silently undoes the switch,
      // and regenerate (which re-runs immediately) could never use it at all.
      // Falls back to the inherited meta when nothing is selected yet.
      string forkModel    = string.IsNullOrWhiteSpace(_selModel) ? metaModel    : _selModel;
      string forkProvider = string.IsNullOrWhiteSpace(_selModel) ? metaProvider : _selProvider;

      // Branching mints ids far more often than reverting did, so take the
      // collision-checked one — appending a fork onto an existing transcript
      // would corrupt both.
      var newSessionId = TranscriptStore.MintSessionId(_projectPath);
      var newPath      = TranscriptStore.FileFor(_projectPath, newSessionId);
      if (newPath == null) return null;

      var newStore = new TranscriptStore(newPath);
      newStore.AppendMeta(newSessionId, metaModel, metaProvider, metaPrompt, metaProjectPath,
                          kind: null, parentSessionId: _sessionId, forkKind: forkKind);

      // Carry the parent's name across, marked as a branch of it. The replay
      // loop below deliberately drops "title" records — a caption applies to
      // the session as a whole, not to a point in it, so the last one wins
      // regardless of where the slice falls. An unnamed parent stays unnamed:
      // the picker falls back to the first prompt for both, which reads
      // correctly since they share it.
      string? parentCaption = null;
      foreach (var r in records)
         if (r.Type == "title")
            parentCaption = string.IsNullOrWhiteSpace(r.Title) ? null : r.Title!.Trim();
      if (parentCaption != null)
         newStore.AppendTitle(_ForkTitle(parentCaption));

      // Re-emit everything before the slice point, dropping the original meta
      // (we already wrote a fresh one above). The historical model swaps are
      // kept — they explain the answers above them — so we track what they
      // leave active in order to correct the tail afterwards.
      string trailingModel    = metaModel;
      string trailingProvider = metaProvider;
      for (int i = 0; i < sliceEndExclusive; i++)
      {
         var r = records[i];
         switch (r.Type)
         {
            case "meta":
               continue;
            case "model":
               newStore.AppendModelChange(r.Model ?? "", r.Provider ?? "");
               trailingModel    = r.Model    ?? "";
               trailingProvider = r.Provider ?? "";
               break;
            case "mode":
               // Preserve every mode-switch marker (including the
               // initial-mode record written in EnsureNewSession) so the
               // forked session resumes in whatever mode the original
               // turn was sent under. Without this, reverting a Plan-mode
               // turn silently lands the fork in Implement.
               if (System.Enum.TryParse<AgentMode>(r.Mode, out var parsedMode))
                  newStore.AppendModeChange(parsedMode);
               break;
            case "perm":
               // Preserve session-scope grants earned before the fork point
               // so the forked session doesn't re-prompt for tools the user
               // already allowed. ResumeSession re-seeds them into the gate.
               if (!string.IsNullOrEmpty(r.PermTool) &&
                   System.Enum.TryParse<PermissionDecision>(r.PermDecision, out var parsedPerm))
                  newStore.AppendSessionGrant(r.PermTool!, parsedPerm);
               break;
            case "mcpTools":
               // Carry the MCP catalog markers across so the fork's first send
               // diffs against what the branched-from turn actually saw, not
               // against nothing. Without this every fork of an MCP session
               // would announce its whole tool set as freshly added.
               if (r.McpTools != null)
                  newStore.AppendMcpTools(r.McpTools);
               break;
            case "msg":
            {
               var msg = new ChatMessage
               {
                  Role             = TranscriptStore.ParseRole(r.Role),
                  Content          = r.Content ?? "",
                  ToolCallId       = r.ToolCallId,
                  ReasoningContent = r.ReasoningContent,
                  ReasoningTokens  = r.ReasoningTokens,
                  PromptTokens     = r.PromptTokens,
                  CompletionTokens = r.CompletionTokens,
                  CachedReadTokens = r.CachedReadTokens,
                  CacheWriteTokens = r.CacheWriteTokens,
                  IsError          = r.IsError == true,
                  // Carried across the fork: a reverted turn that loses its
                  // attachments would resend as a different message than the
                  // one being branched from.
                  Images           = TranscriptStore.ImagesFrom(r),
                  Timings          = TranscriptStore.TimingsFrom(r),
                  Reads            = TranscriptStore.ReadsFrom(r),
               };
               if (r.ToolCalls != null && r.ToolCalls.Count > 0)
               {
                  msg.ToolCalls = new List<ToolCall>(r.ToolCalls.Count);
                  foreach (var t in r.ToolCalls)
                  {
                     msg.ToolCalls.Add(new ToolCall
                     {
                        Id            = t.Id,
                        Name          = t.Name,
                        ArgumentsJson = t.ArgumentsJson,
                     });
                  }
               }
               newStore.AppendMessage(msg);
               break;
            }
         }
      }

      // The replayed history ended on a different model than the one the user
      // has selected now. Close the file on their pick so ResumeSession's
      // "resume on whatever was last active" rule lands on it, and so the
      // transcript states the switch instead of it happening silently.
      if (!string.Equals(trailingModel,    forkModel,    StringComparison.OrdinalIgnoreCase) ||
          !string.Equals(trailingProvider, forkProvider, StringComparison.OrdinalIgnoreCase))
      {
         newStore.AppendModelChange(forkModel, forkProvider);
      }

      return newSessionId;
   }
}
