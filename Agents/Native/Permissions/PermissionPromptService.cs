// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SlimCodeDesktop.Agents.Native.Permissions;

/// <summary>
/// Bridge between the (non-UI) <see cref="PermissionGate"/> and whatever
/// UI surface decides to show a permission dialog. The view registers a
/// handler via <see cref="SetHandler"/>; the gate awaits the response.
///
/// When no handler is registered, <see cref="AskAsync"/> returns a safe
/// Deny grant so the agent can't silently escape into "no UI = no
/// supervision = no rules".
/// </summary>
public sealed class PermissionPromptService
{
   public void SetHandler(Func<PromptRequest, CancellationToken, Task<PromptResponse>> handler)
   {
      _handler = handler;
   }

   public void ClearHandler() => _handler = null;

   public async Task<PromptResponse> AskAsync(PromptRequest request, CancellationToken cancel)
   {
      var h = _handler;
      if (h == null)
      {
         return new PromptResponse
         {
            Decision = PermissionDecision.Deny,
            Scope    = PermissionScope.User,
            Reason   = "no UI handler registered",
         };
      }
      try
      {
         return await h(request, cancel).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
         return new PromptResponse
         {
            Decision = PermissionDecision.Deny,
            Scope    = PermissionScope.User,
            Reason   = "prompt cancelled",
         };
      }
      catch (Exception ex)
      {
         return new PromptResponse
         {
            Decision = PermissionDecision.Deny,
            Scope    = PermissionScope.User,
            Reason   = "prompt handler threw: " + ex.Message,
         };
      }
   }

   // -- fields ---------------------------------------------------------------

   private Func<PromptRequest, CancellationToken, Task<PromptResponse>>? _handler;
}
