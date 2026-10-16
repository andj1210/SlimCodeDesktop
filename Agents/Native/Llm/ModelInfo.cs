// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using SlimCodeDesktop.Agents.Settings;

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>
/// One model entry in the user's catalog. <see cref="Id"/> is the wire name
/// passed in the chat-completion request. <see cref="ContextWindow"/> is
/// user-supplied — the OpenAI /v1/models endpoint generally does not report
/// it reliably, and llama.cpp's response only carries the id.
/// </summary>
public sealed class ModelInfo
{
   /// <summary>Wire id (e.g. "qwen3-coder-30b", "gpt-4o-mini").</summary>
   public string Id { get; set; } = "";

   /// <summary>Context window in tokens. User-configurable; defaults to 32768.</summary>
   [Setting("Context window",
            Help        = "Maximum context size in tokens. Step the spinner in 1k increments or pick a common preset.",
            Increment   = 1024,
            Min         = 1024,
            Max         = 2_000_000,
            Suggestions = new[] { 32768, 65536, 131072, 262144 })]
   public int ContextWindow { get; set; } = 32768;

   /// <summary>True when the user added this entry manually (vs. via discovery).</summary>
   public bool AddedManually { get; set; }

   /// <summary>
   /// When true the model is kept in the catalog but excluded from the chat
   /// model picker. Survives re-discovery (Merge preserves existing entries),
   /// so hiding an unwanted discovered model makes the choice stick.
   /// </summary>
   public bool Hidden { get; set; }

   /// <summary>Optional friendly name shown in pickers; falls back to <see cref="Id"/>.</summary>
   public string? DisplayName { get; set; }

   [Setting("Reasoning style",
            Help = "Which reasoning controls this model's chat template understands. "
                 + "There is no way to detect this — the only machine-readable source is "
                 + "the template itself, which requires the model to be loaded first. "
                 + "Default sends nothing and leaves the provider's own setting in place; "
                 + "the other styles state the level explicitly on every request. "
                 + "Declaring a style the template does not implement makes requests fail "
                 + "outright rather than degrade.")]
   public ReasoningStyle ReasoningStyle { get; set; }

   [Setting("Default reasoning",
            Help = "Where a new session starts. Levels the declared style does not offer "
                 + "fold onto its default, so this stays valid if the style is changed later.")]
   public ReasoningLevel DefaultReasoning { get; set; } = ReasoningLevel.Default;

   /// <summary>Levels this model's declared style actually supports.</summary>
   public ReasoningSpec Reasoning => ReasoningSpec.For(this.ReasoningStyle);
}

/// <summary>
/// One model the user approved after discovery: the wire id plus whether it
/// should land in the catalog hidden from the chat model picker.
/// </summary>
public sealed class ModelPick
{
   public string Id { get; set; } = "";

   /// <summary>Add it, but keep it out of the chat picker (<see cref="ModelInfo.Hidden"/>).</summary>
   public bool Hidden { get; set; }
}

/// <summary>What to do with a catalog entry the server no longer offers.</summary>
public enum StaleModelAction
{
   /// <summary>Leave the entry exactly as it is (default).</summary>
   Keep,

   /// <summary>Keep it in the catalog but take it out of the chat picker.</summary>
   Hide,

   /// <summary>Drop it from the catalog.</summary>
   Delete,
}

/// <summary>
/// One catalog entry that discovery did not find on the server any more,
/// together with the user's verdict on it.
/// </summary>
public sealed class StaleModelPick
{
   public string Id { get; set; } = "";

   public StaleModelAction Action { get; set; }
}

/// <summary>
/// Static helpers for merging discovery results into a persisted catalog
/// without clobbering user-set context windows.
/// </summary>
public static class ModelCatalog
{
   /// <summary>
   /// Reconcile <paramref name="picks"/> against <paramref name="existing"/>:
   /// preserves user-set <see cref="ModelInfo.ContextWindow"/> for known ids,
   /// adds new ids with <paramref name="defaultContextWindow"/>, and keeps any
   /// manually-added entries even when they did not appear in discovery.
   ///
   /// Only the picks the user selected are passed in — an id already in the
   /// catalog keeps its entry untouched, including its Hidden flag, so a
   /// re-discovery never resets what the user configured.
   /// </summary>
   public static List<ModelInfo> Merge(
      IReadOnlyList<ModelInfo>? existing,
      IReadOnlyList<ModelPick> picks,
      int defaultContextWindow)
   {
      var byId = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
      if (existing != null)
      {
         foreach (var m in existing)
         {
            if (string.IsNullOrWhiteSpace(m.Id)) continue;
            byId[m.Id] = m;
         }
      }

      foreach (var pick in picks)
      {
         if (string.IsNullOrWhiteSpace(pick.Id)) continue;
         if (byId.ContainsKey(pick.Id))
            continue;
         byId[pick.Id] = new ModelInfo
         {
            Id            = pick.Id,
            ContextWindow = defaultContextWindow,
            AddedManually = false,
            Hidden        = pick.Hidden,
         };
      }

      var result = new List<ModelInfo>(byId.Values);
      result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
      return result;
   }

   /// <summary>
   /// Apply the verdicts the user gave on entries the server no longer offers:
   /// <see cref="StaleModelAction.Delete"/> drops the entry,
   /// <see cref="StaleModelAction.Hide"/> sets <see cref="ModelInfo.Hidden"/>,
   /// and anything else (or any id not mentioned) is left untouched.
   ///
   /// Returns a fresh list -- the caller assigns it back to the provider.
   /// </summary>
   public static List<ModelInfo> ApplyStale(
      IReadOnlyList<ModelInfo>? models,
      IReadOnlyList<StaleModelPick>? picks)
   {
      var result = new List<ModelInfo>();
      if (models == null) return result;
      if (picks == null || picks.Count == 0)
      {
         result.AddRange(models);
         return result;
      }

      var verdict = new Dictionary<string, StaleModelAction>(StringComparer.OrdinalIgnoreCase);
      foreach (var p in picks)
      {
         if (string.IsNullOrWhiteSpace(p.Id)) continue;
         verdict[p.Id] = p.Action;
      }

      foreach (var m in models)
      {
         if (string.IsNullOrWhiteSpace(m.Id) || !verdict.TryGetValue(m.Id, out var action))
         {
            result.Add(m);
            continue;
         }
         if (action == StaleModelAction.Delete) continue;
         if (action == StaleModelAction.Hide) m.Hidden = true;
         result.Add(m);
      }
      return result;
   }
}
