// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

namespace SlimCodeDesktop.Agents.Native.Permissions;

/// <summary>
/// Which layer a grant attaches to. Ordering is "narrowest" → "broadest" so
/// <see cref="User"/> &lt; <see cref="Session"/> &lt; <see cref="Project"/>
/// &lt; <see cref="Global"/> reads naturally as "scope of authority".
///
/// <see cref="Default"/> tags grants emitted by the resolver when nothing
/// explicit matches and the global default kicked in (e.g. Read tools).
/// </summary>
public enum PermissionScope
{
   User,      // one-shot, in-memory, cleared after the call
   Session,   // scoped to one session: persisted as a "perm" record in the
              // session transcript, re-seeded into the gate on resume, and
              // cleared on every session boundary so it never leaks to siblings
   Project,   // persistent, %APPDATA%\SlimCode\<profile>\projects\<slug>\permissions.json
   Global,    // persistent, %APPDATA%\SlimCode\<profile>\global-permissions.json
   Default,   // baked-in fallback (Read=Allow, Write/Execute=Prompt)
}
