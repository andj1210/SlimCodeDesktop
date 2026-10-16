// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;

namespace SlimCodeDesktop.Agents;

/// <summary>
/// One session placed in the fork forest: the session itself plus how deep it
/// sits under the root of its branch.
/// </summary>
/// <param name="Session">The session card, unchanged.</param>
/// <param name="Depth">0 for a root, 1 for a direct fork of one, and so on.</param>
public readonly record struct LineageEntry(AgentSession Session, int Depth);

/// <summary>
/// Arranges a flat session list into the forest implied by
/// <see cref="AgentSession.ParentId"/>, for pickers that want to show which
/// session a fork came out of.
///
/// The single rule this file exists to enforce: a parent id is a *hint*, never
/// a dependency. Sessions are deleted individually and forks are never taken
/// down with their parent, so a child routinely outlives the session it was
/// forked from. Every unresolvable parent — deleted, belonging to another
/// project, or part of a cycle in a hand-edited file — demotes its child to a
/// root and the list stays complete. Nothing here can drop a session.
/// </summary>
public static class SessionLineage
{
   /// <summary>
   /// Depth beyond which a chain is treated as a root. Guards against a cycle
   /// introduced by hand-editing a transcript; also keeps the indent from
   /// eating the sidebar on a deeply re-branched session.
   /// </summary>
   public const int MAX_DEPTH = 6;

   /// <summary>
   /// Order <paramref name="sessions"/> so each fork follows its parent.
   ///
   /// Roots are ordered by the most recent activity anywhere in their branch,
   /// so working in a fork keeps the whole family at the top of the list
   /// rather than stranding the root where it was last touched. Siblings are
   /// most-recent-first, matching the flat ordering this replaces.
   ///
   /// Output always contains exactly one entry per input session.
   /// </summary>
   public static IReadOnlyList<LineageEntry> Arrange(IReadOnlyList<AgentSession> sessions)
   {
      if (sessions == null || sessions.Count == 0)
         return Array.Empty<LineageEntry>();

      var byId = new Dictionary<string, AgentSession>(sessions.Count, StringComparer.Ordinal);
      foreach (var s in sessions)
         if (!string.IsNullOrEmpty(s.Id)) byId[s.Id] = s;

      // Resolve each session's effective parent once. Anything that doesn't
      // land on a live, different, non-cyclic session becomes a root.
      var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
      var roots    = new List<AgentSession>();
      var kids     = new Dictionary<string, List<AgentSession>>(StringComparer.Ordinal);

      foreach (var s in sessions)
      {
         var parent = _ResolveParent(s, byId);
         if (parent == null)
         {
            roots.Add(s);
            continue;
         }
         parentOf[s.Id] = parent;
         if (!kids.TryGetValue(parent, out var list))
            kids[parent] = list = new List<AgentSession>();
         list.Add(s);
      }

      // A cycle leaves every member with a resolvable parent and none of them
      // reachable from a root, which would silently swallow them. Walk each
      // session up to its root; anything that doesn't reach one within
      // MAX_DEPTH is promoted to a root itself, breaking the ring.
      foreach (var s in sessions)
      {
         if (!parentOf.ContainsKey(s.Id)) continue;
         if (_RootDepth(s.Id, parentOf) >= 0) continue;

         var stranded = parentOf[s.Id];
         parentOf.Remove(s.Id);
         if (kids.TryGetValue(stranded, out var siblings))
            siblings.RemoveAll(k => string.Equals(k.Id, s.Id, StringComparison.Ordinal));
         roots.Add(s);
      }

      // Subtree recency drives root order, so a branch surfaces on the
      // strength of its newest member rather than its oldest.
      var recency = new Dictionary<string, DateTime>(StringComparer.Ordinal);
      foreach (var s in sessions)
         _SubtreeRecency(s, kids, recency, 0);

      roots.Sort((a, b) => _Recency(b, recency).CompareTo(_Recency(a, recency)));

      var result = new List<LineageEntry>(sessions.Count);
      foreach (var root in roots)
         _Emit(root, 0, kids, recency, result);
      return result;
   }

   /// <summary>Short label for a fork kind, or "" for anything unrecognized.</summary>
   public static string DescribeFork(string? forkKind) => forkKind switch
   {
      "revert"     => "reverted",
      "regenerate" => "regenerated",
      _            => "",
   };

   // -- private methods ---------------------------------------------------------

   private static string? _ResolveParent(AgentSession s, Dictionary<string, AgentSession> byId)
   {
      var parent = s.ParentId;
      if (string.IsNullOrEmpty(parent)) return null;
      // Self-parenting and a parent that isn't in this project's list (deleted,
      // or filtered out as a foreign-project transcript) both mean "root".
      if (string.Equals(parent, s.Id, StringComparison.Ordinal)) return null;
      return byId.ContainsKey(parent!) ? parent : null;
   }

   // Depth from `id` up to a root, or -1 when no root is reachable within
   // MAX_DEPTH (a cycle, or a chain too deep to indent usefully).
   private static int _RootDepth(string id, Dictionary<string, string> parentOf)
   {
      var cur = id;
      for (int d = 0; d <= MAX_DEPTH; d++)
      {
         if (!parentOf.TryGetValue(cur, out var next)) return d;
         cur = next;
      }
      return -1;
   }

   private static DateTime _SubtreeRecency(
      AgentSession s,
      Dictionary<string, List<AgentSession>> kids,
      Dictionary<string, DateTime> memo,
      int depth)
   {
      if (memo.TryGetValue(s.Id, out var cached)) return cached;
      // Depth is bounded above, but memoize before recursing anyway so a ring
      // that slipped through cannot spin here.
      memo[s.Id] = s.LastUsed;

      var best = s.LastUsed;
      if (depth < MAX_DEPTH && kids.TryGetValue(s.Id, out var children))
      {
         foreach (var c in children)
         {
            var t = _SubtreeRecency(c, kids, memo, depth + 1);
            if (t > best) best = t;
         }
      }
      memo[s.Id] = best;
      return best;
   }

   private static DateTime _Recency(AgentSession s, Dictionary<string, DateTime> memo) =>
      memo.TryGetValue(s.Id, out var t) ? t : s.LastUsed;

   private static void _Emit(
      AgentSession s,
      int depth,
      Dictionary<string, List<AgentSession>> kids,
      Dictionary<string, DateTime> recency,
      List<LineageEntry> into)
   {
      into.Add(new LineageEntry(s, depth));
      if (depth >= MAX_DEPTH) return;
      if (!kids.TryGetValue(s.Id, out var children) || children.Count == 0) return;

      children.Sort((a, b) => _Recency(b, recency).CompareTo(_Recency(a, recency)));
      foreach (var c in children)
         _Emit(c, depth + 1, kids, recency, into);
   }
}
