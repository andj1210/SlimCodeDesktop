// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;

namespace SlimCodeDesktop.Agents.Native.Llm;

/// <summary>
/// One named OpenAI-compatible endpoint. The <see cref="Name"/> is the
/// user-facing label shown alongside a model in the picker (so the same
/// model id hosted on two different servers stays distinguishable). Each
/// provider owns its own model catalog: discovery and per-model context
/// windows are scoped to the provider that hosts them.
/// </summary>
public sealed class ProviderInfo
{
   /// <summary>Unique, user-facing provider label (e.g. "Local", "OpenRouter").</summary>
   public string Name { get; set; } = "";

   /// <summary>Base URL of the OpenAI-compatible server (no trailing /v1).</summary>
   public string BaseUrl { get; set; } = "";

   /// <summary>Bearer token. Empty for local servers that don't require auth.</summary>
   public string ApiKey { get; set; } = "";

   /// <summary>Models hosted by this provider; populated via discovery + manual adds.</summary>
   public List<ModelInfo> Models { get; set; } = new();

   /// <summary>
   /// Name a new session automatically from its first prompt, using the
   /// session's own model. One short call per session, fired beside the first
   /// turn — see <see cref="Sessions.SessionTitler"/>. Per-provider because
   /// the cost is: free on a local endpoint, billed on a hosted one.
   /// </summary>
   public bool AutoTitleSessions { get; set; }

   /// <summary>The model matching <paramref name="modelId"/>, or null.</summary>
   public ModelInfo? FindModel(string? modelId)
   {
      if (string.IsNullOrEmpty(modelId)) return null;
      foreach (var m in Models)
      {
         if (string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
            return m;
      }
      return null;
   }
}
