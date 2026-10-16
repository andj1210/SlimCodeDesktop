// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SlimCodeDesktop.Agents.Native.Tools;

/// <summary>
/// Incremental reader over one tool call's <c>arguments</c> JSON while it is
/// still arriving. The point is <c>write_file</c> / <c>edit_file</c>: their
/// argument object carries the entire file body, so the model spends minutes
/// generating it and the UI has nothing to show until the call is complete.
/// Feeding the fragments through here yields the decoded file text as it is
/// produced.
///
/// Deliberately NOT a JSON parser round-trip: <c>JsonDocument.Parse</c> fails
/// on every fragment until the final byte, and re-parsing the accumulated
/// buffer per token is quadratic on a large file. This is a resumable
/// character state machine — it holds its position across <see cref="Feed"/>
/// calls, including mid-escape (<c>\u12</c> split over two fragments) and mid
/// surrogate pair.
///
/// Tolerant by design: the input is a truncated prefix of a JSON object at
/// every moment, and malformed arguments are a real (handled) case upstream.
/// Anything unrecognized is skipped rather than throwing.
///
/// Not thread-safe — feed from one thread (the UI thread, in practice).
/// </summary>
public sealed class ToolArgsStream
{
   /// <param name="streamKeys">Top-level string keys whose value should be
   /// handed out incrementally, in the order the tool declares them (e.g.
   /// <c>old_string</c>, <c>new_string</c>). Every other string value is
   /// captured whole and read back via <see cref="GetValue"/>.</param>
   public ToolArgsStream(params string[] streamKeys)
   {
      if (streamKeys != null)
      {
         foreach (var k in streamKeys)
         {
            if (!string.IsNullOrEmpty(k))
               _streamKeys.Add(k);
         }
      }
   }

   /// <summary>Raw argument characters fed so far, decoded or not.</summary>
   public long RawChars => _rawChars;

   /// <summary>Decoded characters handed out for streamed keys so far.</summary>
   public long StreamedChars => _streamedChars;

   /// <summary>
   /// Completed value of a non-streamed string key, or null while it is still
   /// arriving (or absent). Capped at <see cref="MAX_CAPTURE"/> characters —
   /// this exists for short scalars like <c>path</c>, not for bulk text.
   /// </summary>
   public string? GetValue(string key) => _values.TryGetValue(key, out var v) ? v : null;

   /// <summary>
   /// Consume the next raw fragment. <paramref name="onText"/> is invoked with
   /// (key, decoded text) for each contiguous run belonging to a streamed key
   /// — at most once per key per call, and never for a key not listed in the
   /// constructor.
   /// </summary>
   public void Feed(string fragment, Action<string, string>? onText)
   {
      if (string.IsNullOrEmpty(fragment))
         return;

      _rawChars += fragment.Length;

      for (int i = 0; i < fragment.Length; i++)
      {
         char c = fragment[i];
         switch (_state)
         {
            case State.PreObject:
               // Skip whatever precedes the object. Nothing is emitted for
               // arguments that never open one.
               if (c == '{')
                  _state = State.PreKey;
               break;

            case State.PreKey:
               if (c == '"')
               {
                  _buf.Clear();
                  _emitting    = false;
                  _escape      = Escape.None;
                  _pendingHigh = '\0';
                  _state       = State.InKey;
               }
               else if (c == '}')
               {
                  _state = State.Done;
               }
               break;

            case State.InKey:
               if (_DecodeStringChar(c))
               {
                  _key   = _buf.ToString();
                  _state = State.PostKey;
               }
               break;

            case State.PostKey:
               if (c == ':')
                  _state = State.PreValue;
               break;

            case State.PreValue:
               if (c == '"')
               {
                  _emitting = _streamKeys.Contains(_key);
                  if (_emitting)
                  {
                     // Hand the previous key's run over before the sink starts
                     // carrying a different key's text.
                     _FlushSink(onText);
                     _sinkKey = _key;
                  }
                  _buf.Clear();
                  _escape      = Escape.None;
                  _pendingHigh = '\0';
                  _state       = State.InString;
               }
               else if (c == '{' || c == '[')
               {
                  _depth      = 1;
                  _nestString = false;
                  _escape     = Escape.None;
                  _state      = State.InNested;
               }
               else if (!char.IsWhiteSpace(c))
               {
                  _state = State.InLiteral;
               }
               break;

            case State.InString:
               if (_DecodeStringChar(c))
               {
                  _FlushPendingHigh();
                  if (!_emitting && _key.Length > 0)
                     _values[_key] = _buf.ToString();
                  _emitting = false;
                  _state    = State.PostValue;
               }
               break;

            case State.InLiteral:
               // Numbers / true / false / null. Never contain a quote, so a
               // plain terminator scan is enough.
               if (c == ',')      _state = State.PreKey;
               else if (c == '}') _state = State.Done;
               break;

            case State.InNested:
               // Skip a nested object/array, staying string-aware so a brace
               // inside a string value doesn't unbalance the depth count.
               if (_nestString)
               {
                  if (_escape == Escape.Backslash)  _escape = Escape.None;
                  else if (c == '\\')               _escape = Escape.Backslash;
                  else if (c == '"')                _nestString = false;
               }
               else if (c == '"')
               {
                  _nestString = true;
                  _escape     = Escape.None;
               }
               else if (c == '{' || c == '[')
               {
                  _depth++;
               }
               else if (c == '}' || c == ']')
               {
                  if (--_depth <= 0)
                     _state = State.PostValue;
               }
               break;

            case State.PostValue:
               if (c == ',')      _state = State.PreKey;
               else if (c == '}') _state = State.Done;
               break;

            case State.Done:
               break;
         }
      }

      _FlushSink(onText);
   }

   // -- private methods ------------------------------------------------------

   // Feeds one raw character of a JSON string body (key or value). Returns
   // true when that character was the closing quote. Escape state persists
   // across calls, so a fragment may end anywhere inside "ä".
   private bool _DecodeStringChar(char c)
   {
      switch (_escape)
      {
         case Escape.None:
            if (c == '\\')
            {
               _escape = Escape.Backslash;
               return false;
            }
            if (c == '"')
               return true;
            _Emit(c);
            return false;

         case Escape.Backslash:
            _escape = Escape.None;
            switch (c)
            {
               case 'n':  _Emit('\n'); break;
               case 't':  _Emit('\t'); break;
               case 'r':  _Emit('\r'); break;
               case 'b':  _Emit('\b'); break;
               case 'f':  _Emit('\f'); break;
               case '/':  _Emit('/');  break;
               case '"':  _Emit('"');  break;
               case '\\': _Emit('\\'); break;
               case 'u':
                  _escape = Escape.Unicode;
                  _hex.Clear();
                  break;
               default:
                  // Not a legal escape. Keep both characters rather than
                  // silently dropping content the model actually produced.
                  _Emit('\\');
                  _Emit(c);
                  break;
            }
            return false;

         case Escape.Unicode:
            _hex.Append(c);
            if (_hex.Length == 4)
            {
               _escape = Escape.None;
               if (ushort.TryParse(_hex.ToString(), NumberStyles.HexNumber,
                                   CultureInfo.InvariantCulture, out var code))
                  _Emit((char)code);
            }
            return false;
      }
      return false;
   }

   // Routes one decoded character to the streamed sink or the capture buffer.
   //
   // A high surrogate is held back until its partner arrives: emitting one
   // alone would put a lone surrogate into the live view for a frame, which
   // renders as a replacement glyph. It is flushed unconditionally when the
   // string closes, so nothing is lost if the pair never completes.
   private void _Emit(char ch)
   {
      if (!_emitting)
      {
         if (_buf.Length < MAX_CAPTURE)
            _buf.Append(ch);
         return;
      }

      if (_pendingHigh != '\0')
      {
         _sink.Append(_pendingHigh).Append(ch);
         _pendingHigh    = '\0';
         _streamedChars += 2;
         return;
      }
      if (char.IsHighSurrogate(ch))
      {
         _pendingHigh = ch;
         return;
      }
      _sink.Append(ch);
      _streamedChars++;
   }

   private void _FlushPendingHigh()
   {
      if (_pendingHigh == '\0')
         return;
      _sink.Append(_pendingHigh);
      _pendingHigh = '\0';
      _streamedChars++;
   }

   private void _FlushSink(Action<string, string>? onText)
   {
      if (_sink.Length == 0)
         return;
      onText?.Invoke(_sinkKey, _sink.ToString());
      _sink.Clear();
   }

   // -- nested types ---------------------------------------------------------

   private enum State
   {
      PreObject,
      PreKey,
      InKey,
      PostKey,
      PreValue,
      InString,
      InLiteral,
      InNested,
      PostValue,
      Done,
   }

   private enum Escape
   {
      None,
      Backslash,
      Unicode,
   }

   // -- fields ---------------------------------------------------------------

   // Ceiling on a captured (non-streamed) value. Guards against a large
   // string argument on a tool with no stream key pinning the whole body in
   // memory twice — the loop already holds the full args string.
   private const int MAX_CAPTURE = 8192;

   private readonly HashSet<string>             _streamKeys = new(StringComparer.Ordinal);
   private readonly Dictionary<string, string>  _values     = new(StringComparer.Ordinal);

   private readonly StringBuilder _buf  = new();   // current key, or a captured value
   private readonly StringBuilder _sink = new();   // decoded text pending hand-off
   private readonly StringBuilder _hex  = new(4);  // \uXXXX digits seen so far

   private State  _state    = State.PreObject;
   private Escape _escape   = Escape.None;
   private string _key      = "";
   private string _sinkKey  = "";
   private bool   _emitting;                       // current value goes to _sink
   private char   _pendingHigh;                    // held high surrogate, '\0' if none
   private int    _depth;                          // nesting depth while skipping
   private bool   _nestString;                     // inside a string while skipping
   private long   _rawChars;
   private long   _streamedChars;
}
