// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SlimCodeDesktop.Agents.Native.Llm;

namespace SlimCodeDesktop.Agents.Native.Sessions;

/// <summary>
/// Names a session from its first user prompt, on the session's own model.
///
/// The call is tiny by construction: one prompt in, a few words out, capped
/// hard enough that a model which decides to explain itself gets cut off
/// rather than billed for. Everything about *how* the call is made lives in
/// <see cref="SideCall"/>; what is here is the prompt, the schema, and the
/// cleanup that turns a model's idea of a title into a caption the picker can
/// render.
///
/// Failure is always silent — <see cref="ProposeAsync"/> returns null and the
/// session keeps falling back to a quote of its first prompt, exactly as it
/// did before. A session name is never worth an error in the user's face.
/// </summary>
public sealed class SessionTitler : SideCall
{
   /// <summary>Enough for a name several times over; a model that rambles gets cut.</summary>
   private const int OUTPUT_CAP_TOKENS = 64;

   /// <summary>
   /// How much of the first prompt is sent. The ask is at the front — a long
   /// pasted log or stack trace after it adds cost without adding meaning —
   /// so this clips the tail.
   /// </summary>
   private const int PROMPT_MAX_CHARS = 1500;

   /// <summary>Matches the picker's caption limit (NativeAgent.CAPTION_MAX_CHARS).</summary>
   public const int TITLE_MAX_CHARS = 120;

   public SessionTitler(ILlmClient client, string model, string? noThinkKwargs)
      : base(client, model, noThinkKwargs)
   {
   }

   /// <summary>
   /// Propose a name for a session whose first prompt is
   /// <paramref name="firstPrompt"/>. Null when the prompt carries nothing to
   /// name (an image-only turn), or on any failure.
   /// </summary>
   public async Task<string?> ProposeAsync(string firstPrompt, CancellationToken cancel)
   {
      if (string.IsNullOrWhiteSpace(firstPrompt))
         return null;

      var call = await CallAsync(
         SYSTEM_PROMPT,
         _Clip(firstPrompt),
         OUTPUT_CAP_TOKENS,
         RESPONSE_FORMAT,
         cancel).ConfigureAwait(false);

      if (!call.Ok) return null;

      // The schema asks for JSON, but a schema-ignoring model may hand back
      // the bare title instead. Both are usable — though once an object is
      // there, only its title field counts: falling back to the raw text
      // would "clean" the JSON envelope into the session name.
      var json = ExtractJsonObject(call.Text);
      return json != null ? Clean(_FromJson(json)) : Clean(call.Text);
   }

   /// <summary>
   /// Reduce a model's output to a one-line caption: first non-empty line,
   /// stripped of the wrapping and decoration models habitually add, clamped
   /// to <see cref="TITLE_MAX_CHARS"/>. Null when nothing usable is left.
   ///
   /// Public because the manual "Suggest name" path pre-fills a rename dialog
   /// with the result and wants the same normalization.
   /// </summary>
   public static string? Clean(string? raw)
   {
      if (string.IsNullOrWhiteSpace(raw)) return null;

      // A stray fence or a "Title: " lead-in is far more common than a title
      // that legitimately spans lines, so take the first line with content.
      string line = "";
      foreach (var part in raw.Split('\n'))
      {
         var t = part.Trim().Trim('`').Trim();
         if (t.Length == 0) continue;
         if (t.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            t = t.Substring(6).Trim();
         if (t.Length == 0) continue;
         line = t;
         break;
      }
      if (line.Length == 0) return null;

      // Surrounding quotes come back often enough to be worth stripping; a
      // trailing period never belongs on a label.
      line = line.Trim('"', '\'', '“', '”', '*', ' ').TrimEnd('.', ' ');

      // Collapse internal whitespace — the picker renders one line.
      var sb = new StringBuilder(Math.Min(line.Length, TITLE_MAX_CHARS));
      bool prevSpace = false;
      foreach (var ch in line)
      {
         if (char.IsWhiteSpace(ch) || char.IsControl(ch))
         {
            if (!prevSpace && sb.Length > 0) sb.Append(' ');
            prevSpace = true;
         }
         else
         {
            sb.Append(ch);
            prevSpace = false;
         }
         if (sb.Length >= TITLE_MAX_CHARS) break;
      }

      var result = sb.ToString().Trim();
      return result.Length == 0 ? null : result;
   }

   private static string _Clip(string prompt)
   {
      var p = prompt.Trim();
      return p.Length <= PROMPT_MAX_CHARS
         ? p
         : p.Substring(0, PROMPT_MAX_CHARS) + "\n[…prompt truncated…]";
   }

   private static string? _FromJson(string json)
   {
      try { return JsonSerializer.Deserialize<Dto>(json, JsonOpts)?.Title; }
      catch (JsonException) { return null; }
   }

   private sealed class Dto
   {
      [JsonPropertyName("title")] public string? Title { get; set; }
   }

   // -- prompt ------------------------------------------------------------------

   // Name the request, don't answer it. Models handed a coding question with
   // no other instruction start solving it, and the output cap then turns
   // that into a truncated first sentence used as the session name.
   private const string SYSTEM_PROMPT =
      "You name coding sessions. The user message is the opening request of a session with a " +
      "coding assistant. Produce a short label for it, the way a person would name a tab.\n" +
      "\n" +
      "Do NOT answer the request, do not comment on it, do not ask anything.\n" +
      "\n" +
      "Rules:\n" +
      "- Three to six words. A noun phrase, not a sentence.\n" +
      "- Name the specific subject: the feature, file, component or bug in question. " +
      "'Fix session switching in tab host' is a good name; 'Code help' and 'User request' " +
      "are useless ones.\n" +
      "- Keep identifiers the user used (file, type or function names) when they are what the " +
      "request is about.\n" +
      "- No quotes, no trailing period, no 'Session:' or 'Task:' prefix.\n" +
      "- Write it in the language the request is written in.\n" +
      "\n" +
      "Reply with a single JSON object: {\"title\": \"...\"}";

   private const string RESPONSE_FORMAT = """
   {
      "type": "json_schema",
      "json_schema": {
         "name": "session_title",
         "strict": true,
         "schema": {
            "type": "object",
            "properties": {
               "title": { "type": "string" }
            },
            "required": ["title"],
            "additionalProperties": false
         }
      }
   }
   """;
}
