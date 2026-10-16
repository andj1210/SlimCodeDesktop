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
/// Model selection, the reasoning-level picker, the context gauge and the
/// session statistics bars.
/// </summary>
public partial class NativeAgentView
{
   // -- model / status --------------------------------------------------------

   private void RefreshModelCombo()
   {
      if (_agent == null) return;
      var s            = _agent.Settings;
      var selProvider  = _selProvider;
      var selModel     = _selModel;

      var choices = new List<ModelChoice>();
      foreach (var prov in s.Providers)
      {
         if (string.IsNullOrWhiteSpace(prov.Name)) continue;
         foreach (var m in prov.Models)
         {
            if (string.IsNullOrWhiteSpace(m.Id)) continue;
            // Hidden models are excluded from the picker, except the exact
            // (provider, model) the session is currently using — otherwise
            // resuming a session on a now-hidden model would silently switch.
            bool isCurrent = string.Equals(prov.Name, selProvider, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(m.Id, selModel, StringComparison.OrdinalIgnoreCase);
            if (m.Hidden && !isCurrent) continue;
            choices.Add(new ModelChoice { Provider = prov.Name, ModelId = m.Id });
         }
      }

      // Suppress the SelectionChanged side-effects while we rebind the items.
      _suppressModelChange = true;
      try
      {
         CmbModel.ItemsSource = choices;
         var match = choices.Find(c => c.Matches(selProvider, selModel));
         if (match != null)
            CmbModel.SelectedItem = match;
         else if (choices.Count > 0)
         {
            // Selection vanished from the catalog: fall back visually. Only
            // this tab's selection moves — the global default is untouched.
            CmbModel.SelectedIndex = 0;
            _selProvider           = choices[0].Provider;
            _selModel              = choices[0].ModelId;
         }
      }
      finally
      {
         _suppressModelChange = false;
      }
   }

   private void CmbModel_SelectionChanged(object? sender, SelectionChangedEventArgs e)
   {
      if (_suppressModelChange || _agent == null || _store == null) return;
      if (CmbModel.SelectedItem is not ModelChoice pick) return;

      if (pick.Matches(_selProvider, _selModel)) return;

      var prevProvider = _selProvider;
      var prevModel    = _selModel;
      _selProvider = pick.Provider;
      _selModel    = pick.ModelId;

      // An explicit pick also becomes the app-wide default for *future*
      // sessions. Live tabs are unaffected — each holds its own selection.
      var s = _agent.Settings;
      s.SelectedProvider = pick.Provider;
      s.SelectedModel    = pick.ModelId;
      _store.SaveAgent(_agent);
      UpdateGaugeWindow();
      RefreshStatus();

      // Hot-swap: keep the active loop's history, redirect subsequent turns
      // to the new model. The first request after the swap may surface a
      // provider error if tool-call structures aren't accepted by the new
      // model; that bubbles up through loop.Error like any other failure.
      ApplyModelSwap(prevProvider, prevModel, pick.Provider, pick.ModelId);
   }

   private void ApplyModelSwap(string? prevProvider, string? prevModel, string newProvider, string newModel)
   {
      // No live loop? Nothing to swap — the next Send will build a loop
      // with the current provider + model from settings.
      if (_loop == null || _agent == null) return;

      // A provider change means a different endpoint / key, so the wire client
      // has to be rebuilt; a same-provider model change only redirects the id.
      if (!string.Equals(prevProvider, newProvider, StringComparison.OrdinalIgnoreCase))
      {
         var prov = _agent.Settings.ResolveProvider(newProvider);
         if (prov != null)
            _loop.SetClient(new OpenAiCompatClient(prov.BaseUrl, NullIfBlank(prov.ApiKey)));
      }

      _loop.Model   = newModel;
      _loopModelId  = newModel;
      _loopProvider = newProvider;

      // Re-resolve against the model now in play. Without this the loop keeps
      // the previous model's spec and can send it a level its template does
      // not list, which fails the request outright rather than degrading.
      _ConfigureReasoning(_loop, _agent.Settings.ResolveProvider(newProvider), newModel);

      // Visual + persisted marker so the transcript reads cleanly and a
      // resume picks up the latest provider + model (not the meta's start ones).
      AppendItem(new NoticeLine(_ModelSwapNote(prevProvider, prevModel, newProvider, newModel)));
      _transcriptStore?.AppendModelChange(newModel, newProvider);
   }

   // "model set to OpenAI · gpt-4o-mini" / "model switched: X · a → Y · b".
   private static string _ModelSwapNote(string? prevProvider, string? prevModel, string newProvider, string newModel)
   {
      var to = newProvider + " · " + newModel;
      if (string.IsNullOrEmpty(prevModel))
         return "model set to " + to;
      return "model switched: " + (prevProvider ?? "") + " · " + prevModel + " → " + to;
   }

   /// <summary>Look up the selected model's context window and push it to the gauge.</summary>
   private void UpdateGaugeWindow()
   {
      if (_agent == null) { Gauge.WindowTokens = NativeAgentSettings.DEFAULT_NEW_MODEL_CONTEXT; return; }
      var prov  = _agent.Settings.ResolveProvider(_selProvider);
      var model = prov?.FindModel(_selModel);
      Gauge.WindowTokens = model?.ContextWindow ?? NativeAgentSettings.DEFAULT_NEW_MODEL_CONTEXT;
   }

   /// <summary>
   /// Seed the cumulative session accumulators by folding the per-turn token
   /// stats of every assistant message in history. Call only when the loop is
   /// idle (e.g. on resume) — it reads the history list, which the loop thread
   /// mutates while running; live updates come through the UsageReported path
   /// instead. Fields absent from older sessions fold in as zero.
   /// </summary>
   private void _SeedSessionTotalsFromHistory()
   {
      _sumInput = _sumCached = _sumOutput = _sumReasoning = _sumCacheWrite = 0;

      var history = _loop?.History;
      if (history != null)
      {
         foreach (var m in history)
         {
            if (m.Role != ChatRole.Assistant) continue;
            _sumInput      += m.PromptTokens     ?? 0;
            _sumCached     += m.CachedReadTokens  ?? 0;
            _sumOutput     += m.CompletionTokens  ?? 0;
            _sumReasoning  += m.ReasoningTokens   ?? 0;
            _sumCacheWrite += m.CacheWriteTokens  ?? 0;
         }
      }
      Gauge.SetSessionTotals(_sumInput, _sumCached, _sumOutput, _sumReasoning, _sumCacheWrite);
   }

   // Running cumulative token totals for the active session (UI thread only).
   private int _sumInput;
   private int _sumCached;
   private int _sumOutput;
   private int _sumReasoning;
   private int _sumCacheWrite;

   // -- session statistics (UI thread only) ------------------------------------

   // Time per phase: settled runs, plus the run in flight (its closed phases
   // arrive with every PhaseChanged; the open one grows on the bar's clock).
   private PhaseTimings _sessionTimings;
   private PhaseTimings _runSoFar;
   private TurnPhase    _livePhase = TurnPhase.Idle;

   // Token provenance. The rate denominators count the MAIN loop only:
   // subagent tokens are generated inside the parent's tool-exec phase and
   // would corrupt a tokens-per-second over the parent's generation time.
   private TokenReads   _sessionReads;
   private int          _mainOutputTokens;
   private int          _mainReasoningTokens;
   private int          _mainUncachedInput;
   private LlmUsage?    _lastUsage;

   private SegmentBar   _phaseBar = null!;
   private SegmentBar   _tokenBar = null!;

   private void _BuildStatsBars()
   {
      _phaseBar = new SegmentBar(120, 7);
      _tokenBar = new SegmentBar(120, 7);
      PnlStats.Children.Add(_phaseBar);
      PnlStats.Children.Add(_tokenBar);
      // The tooltip carries the live phase's elapsed time, so it has to
      // follow the same clock as the bar.
      _phaseBar.LiveTick += _RefreshPhaseTooltip;
      _RefreshStatsBars();
   }

   private void _ResetStats()
   {
      _sessionTimings      = default;
      _runSoFar            = default;
      _livePhase           = TurnPhase.Idle;
      _sessionReads        = default;
      _mainOutputTokens    = 0;
      _mainReasoningTokens = 0;
      _mainUncachedInput   = 0;
      _lastUsage           = null;
      _RefreshStatsBars();
   }

   // Fold what a stored session recorded. Turns before timing existed carry
   // nothing and simply do not count; the bar then understates the session,
   // which is the honest failure.
   private void _SeedStatsFromHistory(IReadOnlyList<ChatMessage> history)
   {
      foreach (var m in history)
      {
         if (m.Timings.HasValue) _sessionTimings.Add(m.Timings.Value);
         if (m.Role != ChatRole.Assistant) continue;
         if (m.Reads.HasValue) _sessionReads.Add(m.Reads.Value);
         _mainOutputTokens    += m.CompletionTokens ?? 0;
         _mainReasoningTokens += m.ReasoningTokens ?? 0;
         _mainUncachedInput   += Math.Max(0, (m.PromptTokens ?? 0) - (m.CachedReadTokens ?? 0));
      }
      _RefreshStatsBars();
   }

   private void _RefreshStatsBars()
   {
      var shown = _sessionTimings;
      shown.Add(_runSoFar);
      _phaseBar.SetSegments(TimingViz.PhaseSegments(shown), TimingViz.PhaseIndex(_livePhase));
      _RefreshPhaseTooltip();

      int think = _mainReasoningTokens;
      int output = Math.Max(0, _mainOutputTokens - think);
      _tokenBar.SetSegments(TimingViz.TokenSegments(_sessionReads, think, output));
      _tokenBar.SetTooltip(TimingViz.TokenTooltip("Session tokens (main loop)", _sessionReads, think, output));
   }

   private void _RefreshPhaseTooltip()
   {
      var settled = _sessionTimings;
      settled.Add(_runSoFar);
      var live = TimeSpan.FromSeconds(_phaseBar.LiveSeconds);
      // Rates over settled time only; a phase still open has no token count yet.
      var rates = TimingViz.Rates(settled, _mainUncachedInput, _mainOutputTokens, null);
      _phaseBar.SetTooltip(TimingViz.PhaseTooltip("Session time (main loop)", settled,
                                                  _livePhase, live, rates));
   }

   /// <summary>
   /// Attach the reasoning capability of the model this loop actually runs.
   /// Per-loop and per-model, because a subagent may
   /// run <c>Settings.SubagentModel</c>, and a level its template does not
   /// list is an HTTP 500 from the Jinja layer rather than a silent ignore.
   ///
   /// The subagent inherits the session's level and lets the clamp fold it —
   /// so "the parent is thinking hard" carries over as far as the delegate's
   /// model can express it, and no further.
   /// </summary>
   private void _ConfigureReasoning(AgentLoop loop, ProviderInfo? prov, string modelId)
   {
      var (spec, level)  = _ResolveReasoning(prov, modelId);
      loop.ReasoningCaps = spec;
      loop.Reasoning     = level;
   }

   /// <summary>
   /// The capability and level that apply to <paramref name="modelId"/> right
   /// now. Session choice wins; absent one, the model's catalog preference;
   /// a still-Default level is resolved by the spec's own clamp at send time.
   /// </summary>
   private (ReasoningSpec Spec, ReasoningLevel Level) _ResolveReasoning(ProviderInfo? prov, string modelId)
   {
      var mi   = prov?.FindModel(modelId);
      var spec = mi?.Reasoning ?? ReasoningSpec.For(ReasoningStyle.None);
      var lvl  = _selReasoning != ReasoningLevel.Default
                 ? _selReasoning
                 : (mi?.DefaultReasoning ?? ReasoningLevel.Default);
      return (spec, lvl);
   }

   /// <summary>Same resolution as a loop gets, for the tab's own selection.</summary>
   private (ReasoningSpec Spec, ReasoningLevel Level) _ResolveReasoning() =>
      _ResolveReasoning(_agent?.Settings.ResolveProvider(_selProvider), _selModel);

   /// <summary>
   /// Offer this model's levels and select the one that would actually be
   /// sent, or hide the picker when the model declares no reasoning style.
   /// Selects the clamped value so the picker never shows a level the
   /// template would reject.
   ///
   /// The item list is rebuilt only when the style changes: this runs on
   /// every status refresh, and repopulating each time would collapse the
   /// dropdown under a user who has it open.
   /// </summary>
   private void _RefreshReasoningPicker()
   {
      if (PnlReasoning == null || CmbReasoning == null) return;

      var (spec, level) = _ResolveReasoning();
      PnlReasoning.IsVisible = spec.HasControl;
      if (!spec.HasControl)
      {
         _reasoningItemsFor = null;
         return;
      }

      _suppressReasoningChange = true;

      if (_reasoningItemsFor != spec.Style)
      {
         var items = new List<ComboBoxItem>(spec.Choices.Count);
         foreach (var lvl in spec.Choices)
            items.Add(new ComboBoxItem { Content = _ReasoningLabel(lvl), Tag = lvl });
         CmbReasoning.ItemsSource = items;
         _reasoningItemsFor       = spec.Style;
      }

      var eff = spec.Clamp(level);
      for (int i = 0; i < spec.Choices.Count; i++)
      {
         if (spec.Choices[i] == eff) { CmbReasoning.SelectedIndex = i; break; }
      }

      ToolTip.SetTip(CmbReasoning,
         "Reasoning effort for this model. Applies from the next turn. "
       + "Levels that change the system prompt cost a full prompt-cache reprefill.");

      _suppressReasoningChange = false;
   }

   private static string _ReasoningLabel(ReasoningLevel level) => level switch
   {
      ReasoningLevel.Off    => "off",
      ReasoningLevel.On     => "on",
      ReasoningLevel.Low    => "low",
      ReasoningLevel.Medium => "medium",
      ReasoningLevel.High   => "high",
      ReasoningLevel.XHigh  => "xhigh",
      ReasoningLevel.Max    => "max",
      _                     => "default",
   };

   /// <summary>
   /// One list from the user's side: that "off" travels as enable_thinking
   /// while the rest travel as reasoning_effort is a transport detail, and
   /// surfacing it would be asking the user to care about Jinja.
   ///
   /// Applies to the live loop immediately, which means from its next turn —
   /// a request already in flight keeps the level it was built with.
   /// </summary>
   private void CmbReasoning_SelectionChanged(object? sender, SelectionChangedEventArgs e)
   {
      // Programmatic repopulation raises this too; without the guard a refresh
      // would write the displayed level back as though the user had picked it.
      if (_suppressReasoningChange) return;
      if (CmbReasoning?.SelectedItem is not ComboBoxItem it || it.Tag is not ReasoningLevel lvl)
         return;

      _selReasoning = lvl;
      if (_loop != null)
         _loop.Reasoning = lvl;
   }

   // One entry in the chat model picker: a (provider, model) pair shown with
   // inline attribution ("model · provider") so the same id on two providers
   // stays distinguishable. ToString drives the ComboBox item text.
   private sealed class ModelChoice
   {
      public string Provider { get; init; } = "";
      public string ModelId  { get; init; } = "";

      public bool Matches(string? provider, string? modelId) =>
         string.Equals(Provider, provider, StringComparison.OrdinalIgnoreCase) &&
         string.Equals(ModelId,  modelId,  StringComparison.OrdinalIgnoreCase);

      // Provider first: provider names tend to share a length, so the model
      // ids line up in a clean column in the dropdown.
      public override string ToString() => Provider + "  ·  " + ModelId;
   }

   // This tab's reasoning level. Default means "not chosen this session", which
   // falls through to the model's own ModelInfo.DefaultReasoning and then to the
   // style's intrinsic default. Held unclamped so a level stated for one model
   // survives a swap to another that cannot express it and back again.
   private ReasoningLevel                         _selReasoning = ReasoningLevel.Default;

   // Style the reasoning picker's items were built for, so a status refresh
   // only repopulates when the model's style actually changed. Null = hidden.
   private ReasoningStyle?                        _reasoningItemsFor;

   // Same role as _suppressModelChange, for the reasoning picker: guards
   // against a programmatic repopulate being mistaken for a user pick.
   private bool                                   _suppressReasoningChange;

   // Guard against CmbModel.SelectionChanged firing during a programmatic
   // rebind (RefreshModelCombo) — without it, every refresh would trigger
   // a SaveAgent + Reset cycle even when nothing actually changed.
   private bool                                   _suppressModelChange;
}
