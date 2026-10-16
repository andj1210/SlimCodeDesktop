// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;

namespace SlimCodeDesktop.Agents.Native.Mcp;

/// <summary>
/// One tool as a server advertises it. <see cref="InputSchemaJson"/> is kept
/// as raw text and forwarded to the model verbatim: it is already a JSON
/// Schema, and re-modelling it here could only lose information.
/// </summary>
public sealed class McpToolInfo
{
   public string Name { get; init; } = "";

   /// <summary>Human-facing label when the server supplies one; display only.</summary>
   public string? Title { get; init; }

   public string Description { get; init; } = "";

   /// <summary>Raw JSON Schema object for the arguments.</summary>
   public string InputSchemaJson { get; init; } = "{\"type\":\"object\",\"properties\":{}}";

   /// <summary>
   /// The server's <c>readOnlyHint</c> annotation. Null when it said nothing,
   /// which is treated as "assume it writes" — the safe reading, since this
   /// decides whether the permission gate auto-allows the call.
   /// </summary>
   public bool? ReadOnlyHint { get; init; }

   /// <summary>The server's <c>destructiveHint</c>. Display only in v1.</summary>
   public bool? DestructiveHint { get; init; }

   /// <summary>Parse one element of a <c>tools/list</c> result array.</summary>
   public static McpToolInfo? FromJson(JsonElement el)
   {
      if (el.ValueKind != JsonValueKind.Object) return null;
      // Every read is ValueKind-checked: this is untrusted input from a
      // third-party process, and GetString() on a number throws rather than
      // returning null.
      var name = _String(el, "name");
      if (string.IsNullOrWhiteSpace(name)) return null;

      string schema = "{\"type\":\"object\",\"properties\":{}}";
      if (el.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object)
         schema = s.GetRawText();

      bool? readOnly = null, destructive = null;
      if (el.TryGetProperty("annotations", out var a) && a.ValueKind == JsonValueKind.Object)
      {
         if (a.TryGetProperty("readOnlyHint", out var r) &&
             (r.ValueKind == JsonValueKind.True || r.ValueKind == JsonValueKind.False))
            readOnly = r.GetBoolean();
         if (a.TryGetProperty("destructiveHint", out var d) &&
             (d.ValueKind == JsonValueKind.True || d.ValueKind == JsonValueKind.False))
            destructive = d.GetBoolean();
      }

      return new McpToolInfo
      {
         Name            = name!,
         Title           = _String(el, "title"),
         Description     = _String(el, "description") ?? "",
         InputSchemaJson = schema,
         ReadOnlyHint    = readOnly,
         DestructiveHint = destructive,
      };
   }

   private static string? _String(JsonElement obj, string name) =>
      obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
         ? v.GetString() : null;
}

/// <summary>What the server said about itself during <c>initialize</c>.</summary>
public sealed class McpServerIdentity
{
   public string ProtocolVersion { get; init; } = "";
   public string ServerName      { get; init; } = "";
   public string ServerVersion   { get; init; } = "";

   /// <summary>True when the server declared <c>tools.listChanged</c>.</summary>
   public bool ToolsListChanged { get; init; }

   public string Describe =>
      (string.IsNullOrEmpty(ServerName) ? "(unnamed server)" : ServerName) +
      (string.IsNullOrEmpty(ServerVersion) ? "" : " " + ServerVersion) +
      (string.IsNullOrEmpty(ProtocolVersion) ? "" : "  ·  protocol " + ProtocolVersion);
}

/// <summary>
/// Flattened <c>tools/call</c> result. The protocol returns a content array of
/// mixed parts; the agent loop's tool results are plain text, so the parts are
/// rendered into one string here and anything non-textual is announced rather
/// than silently dropped.
/// </summary>
public sealed class McpCallResult
{
   public bool   IsError { get; init; }
   public string Text    { get; init; } = "";

   public static McpCallResult Failure(string message) =>
      new() { IsError = true, Text = message };

   /// <summary>Flatten the <c>result</c> object of a tools/call response.</summary>
   public static McpCallResult FromResult(JsonElement result)
   {
      bool isError = result.TryGetProperty("isError", out var e) &&
                     e.ValueKind == JsonValueKind.True;

      var parts = new List<string>();
      if (result.TryGetProperty("content", out var content) &&
          content.ValueKind == JsonValueKind.Array)
      {
         foreach (var item in content.EnumerateArray())
            _AppendPart(parts, item);
      }

      // Newer servers may answer with structuredContent alone. Serve it as
      // JSON rather than reporting an empty result the model can't act on.
      if (parts.Count == 0 &&
          result.TryGetProperty("structuredContent", out var structured) &&
          structured.ValueKind != JsonValueKind.Undefined &&
          structured.ValueKind != JsonValueKind.Null)
      {
         parts.Add(structured.GetRawText());
      }

      var text = parts.Count > 0 ? string.Join("\n", parts) : "(no content)";
      return new McpCallResult { IsError = isError, Text = text };
   }

   private static void _AppendPart(List<string> parts, JsonElement item)
   {
      if (item.ValueKind != JsonValueKind.Object) return;
      var type = _Str(item, "type");

      switch (type)
      {
         case "text":
            parts.Add(_Str(item, "text") ?? "");
            break;

         case "image":
         case "audio":
         {
            // The tool-result channel is text-only, so the bytes can't travel.
            // Say what was withheld: a model told "[image omitted]" can ask for
            // another route, one handed an empty result cannot.
            var mime  = _Str(item, "mimeType");
            var b64   = _Str(item, "data");
            int bytes = b64 != null ? (int)(b64.Length * 3L / 4) : 0;
            parts.Add("[" + type + " omitted: " + (mime ?? "unknown type") +
                      (bytes > 0 ? ", ~" + bytes + " bytes" : "") + "]");
            break;
         }

         case "resource":
         {
            if (item.TryGetProperty("resource", out var res) && res.ValueKind == JsonValueKind.Object)
            {
               var uri  = _Str(res, "uri");
               var text = _Str(res, "text");
               if (text != null)
                  parts.Add((uri != null ? "[" + uri + "]\n" : "") + text);
               else
                  parts.Add("[binary resource omitted: " + (uri ?? "unknown uri") + "]");
            }
            break;
         }

         case "resource_link":
            parts.Add("[resource link: " + (_Str(item, "uri") ?? "unknown uri") + "]");
            break;

         default:
            // Unknown part kind from a newer spec revision: hand over the raw
            // JSON rather than pretending it wasn't there.
            parts.Add(item.GetRawText());
            break;
      }
   }

   private static string? _Str(JsonElement obj, string name) =>
      obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
         ? v.GetString() : null;
}
