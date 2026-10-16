// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SlimCodeDesktop.Core;

/// <summary>One GitHub release as seen by the updater.</summary>
public sealed class UpdateRelease
{
   public string   Tag             { get; init; } = "";
   public Version? Version         { get; init; }
   public string   HtmlUrl         { get; init; } = "";

   /// <summary>Archive for this OS/arch, or null when the release has none.</summary>
   public string?  AssetName       { get; init; }
   public string?  AssetApiUrl     { get; init; }
   public long     AssetSize       { get; init; }

   /// <summary>SHA256SUMS.txt asset, or null when the release has none.</summary>
   public string?  ChecksumsApiUrl { get; init; }
}

/// <summary>
/// Network half of the self-updater: asks GitHub for the latest release and
/// downloads + verifies this platform's archive. Nothing here runs on its own
/// -- every call is user-triggered from the About page.
///
/// Assets are fetched through the API endpoint (<c>/releases/assets/{id}</c>
/// with <c>Accept: application/octet-stream</c>) rather than the browser URL:
/// it works anonymously on a public repo and with a token on a private one,
/// so testing against the private repo exercises the same code path.
///
/// TEMPORARY: the <see cref="TokenEnvVar"/> support exists only to test
/// against the private repo and goes away once the repo is public -- remove
/// <see cref="TokenEnvVar"/>, <see cref="HasToken"/> and <c>_Token()</c>.
/// HttpClient drops the Authorization header when following GitHub's redirect
/// to the storage host, so the token never leaves api.github.com.
/// </summary>
public static class UpdateService
{
   public const string Owner = "andj1210";
   public const string Repo  = "SlimCodeDesktop";

   /// <summary>TEMPORARY: optional GitHub token (read access) for testing against the private repo.</summary>
   public const string TokenEnvVar = "SLIMCODE_UPDATE_TOKEN";

   public const string ChecksumsAssetName = "SHA256SUMS.txt";

   private const string ApiBase = "https://api.github.com/repos/" + Owner + "/" + Repo;

   private static readonly TimeSpan s_apiTimeout = TimeSpan.FromSeconds(30);

   // No client-wide timeout: the archive download is ~45 MB and may be slow.
   // API calls get their own deadline via a linked token instead.
   private static readonly HttpClient s_http = _CreateClient();

   // -- platform identity ----------------------------------------------------

   /// <summary>RID used in the archive name, e.g. "win-x64" / "linux-x64".</summary>
   public static string RuntimeId =>
      (OperatingSystem.IsWindows() ? "win-" : "linux-") +
      RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

   public static string ArchiveExtension => OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";

   /// <summary>Archive name publish.ps1 produces for <paramref name="version"/> on this platform.</summary>
   public static string ExpectedAssetName(string version) =>
      "slimcode_desktop_v" + version + "_" + RuntimeId + ArchiveExtension;

   /// <summary>TEMPORARY: true when <see cref="TokenEnvVar"/> is set.</summary>
   public static bool HasToken => !string.IsNullOrWhiteSpace(_Token());

   // -- versions -------------------------------------------------------------

   /// <summary>
   /// Parses "v1.2.3", "1.2" or "1.2.3-beta+abc" into a normalized Version
   /// (missing parts become 0, so "1.2" equals "1.2.0"). Null when unparseable.
   /// </summary>
   public static Version? ParseVersion(string? text)
   {
      if (string.IsNullOrWhiteSpace(text))
         return null;

      var v = text.Trim().TrimStart('v', 'V');
      int cut = v.IndexOfAny(new[] { '-', '+' });
      if (cut >= 0)
         v = v.Substring(0, cut);

      if (!Version.TryParse(v, out var parsed))
         return null;

      return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
   }

   /// <summary>True when <paramref name="remote"/> is strictly newer than <paramref name="local"/>.</summary>
   public static bool IsNewer(Version? remote, string local)
   {
      var loc = ParseVersion(local);
      return remote != null && loc != null && remote > loc;
   }

   // -- check ----------------------------------------------------------------

   /// <summary>
   /// Latest published, non-prerelease release (GitHub's /releases/latest
   /// skips drafts and prereleases). Throws with a readable message on failure.
   /// </summary>
   public static async Task<UpdateRelease> CheckLatestAsync(CancellationToken ct = default)
   {
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      cts.CancelAfter(s_apiTimeout);

      using var req = _Request(ApiBase + "/releases/latest", "application/vnd.github+json");
      using var resp = await s_http.SendAsync(req, cts.Token).ConfigureAwait(false);

      if (resp.StatusCode == HttpStatusCode.NotFound)
      {
         throw new InvalidOperationException(HasToken
            ? "No published release found (or the token cannot read " + Owner + "/" + Repo + ")."
            : "No published release found. If the repository is private, set " + TokenEnvVar + " to a GitHub token with read access.");
      }
      if (!resp.IsSuccessStatusCode)
         throw new InvalidOperationException("GitHub answered " + (int)resp.StatusCode + " " + resp.ReasonPhrase + ".");

      var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
      return ParseRelease(json);
   }

   /// <summary>Picks the fields the updater needs out of a GitHub release JSON object.</summary>
   public static UpdateRelease ParseRelease(string json)
   {
      using var doc = JsonDocument.Parse(json);
      var root = doc.RootElement;

      var tag     = _String(root, "tag_name");
      var version = ParseVersion(tag);
      var wanted  = version != null ? ExpectedAssetName(_VersionText(tag)) : null;

      string? assetName = null, assetUrl = null, sumsUrl = null;
      long    assetSize = 0;
      if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
      {
         foreach (var a in assets.EnumerateArray())
         {
            var name = _String(a, "name");
            if (wanted != null && string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
            {
               assetName = name;
               assetUrl  = _String(a, "url");
               assetSize = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            }
            else if (string.Equals(name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
            {
               sumsUrl = _String(a, "url");
            }
         }
      }

      return new UpdateRelease
      {
         Tag             = tag,
         Version         = version,
         HtmlUrl         = _String(root, "html_url"),
         AssetName       = assetName,
         AssetApiUrl     = string.IsNullOrEmpty(assetUrl) ? null : assetUrl,
         AssetSize       = assetSize,
         ChecksumsApiUrl = string.IsNullOrEmpty(sumsUrl) ? null : sumsUrl,
      };
   }

   // -- download -------------------------------------------------------------

   /// <summary>
   /// Downloads the release archive into <paramref name="targetDir"/> and
   /// verifies it against SHA256SUMS.txt. Returns the archive path. A release
   /// without checksums is refused -- an unverifiable archive is not installed.
   /// <paramref name="progress"/> receives 0..1 when the size is known.
   /// </summary>
   public static async Task<string> DownloadAsync(UpdateRelease rel, string targetDir,
                                                  IProgress<double>? progress = null,
                                                  CancellationToken ct = default)
   {
      if (rel.AssetApiUrl == null || rel.AssetName == null)
         throw new InvalidOperationException("Release " + rel.Tag + " has no package for " + RuntimeId + ".");
      if (rel.ChecksumsApiUrl == null)
         throw new InvalidOperationException("Release " + rel.Tag + " has no " + ChecksumsAssetName + "; refusing an unverifiable download.");

      // Checksums first: small, and a failure here costs nothing.
      string sumsText;
      using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
      {
         cts.CancelAfter(s_apiTimeout);
         using var req  = _Request(rel.ChecksumsApiUrl, "application/octet-stream");
         using var resp = await s_http.SendAsync(req, cts.Token).ConfigureAwait(false);
         _EnsureSuccess(resp, ChecksumsAssetName);
         sumsText = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
      }

      var sums = ParseChecksums(sumsText);
      if (!sums.TryGetValue(rel.AssetName, out var expected))
         throw new InvalidOperationException(ChecksumsAssetName + " has no entry for " + rel.AssetName + ".");

      Directory.CreateDirectory(targetDir);
      var path = Path.Combine(targetDir, rel.AssetName);

      using (var req = _Request(rel.AssetApiUrl, "application/octet-stream"))
      using (var resp = await s_http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
      {
         _EnsureSuccess(resp, rel.AssetName);
         long total = resp.Content.Headers.ContentLength ?? rel.AssetSize;

         await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
         await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

         var  buffer = new byte[81920];
         long done   = 0;
         int  read;
         while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
         {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0)
               progress?.Report((double)done / total);
         }
      }

      var actual = ComputeSha256(path);
      if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
      {
         try { File.Delete(path); } catch { }
         throw new InvalidOperationException("Checksum mismatch for " + rel.AssetName + " -- the download is corrupt or was altered.");
      }

      return path;
   }

   /// <summary>
   /// Parses <c>sha256sum</c> output ("&lt;hex&gt;  &lt;name&gt;", binary
   /// mode marks the name with '*') into name -> lowercase hex.
   /// </summary>
   public static Dictionary<string, string> ParseChecksums(string text)
   {
      var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var raw in text.Split('\n'))
      {
         var line = raw.Trim();
         int sp = line.IndexOf(' ');
         if (sp != 64)
            continue;

         var hash = line.Substring(0, 64);
         var name = line.Substring(sp).TrimStart(' ', '*');
         if (name.Length > 0 && _IsHex(hash))
            map[name] = hash.ToLowerInvariant();
      }
      return map;
   }

   public static string ComputeSha256(string path)
   {
      using var fs = File.OpenRead(path);
      return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
   }

   // -- private --------------------------------------------------------------

   private static HttpClient _CreateClient()
   {
      var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
      // GitHub rejects requests without a User-Agent.
      c.DefaultRequestHeaders.UserAgent.ParseAdd("SlimCodeDesktop-Updater/" + AppInfo.Version);
      c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
      return c;
   }

   private static HttpRequestMessage _Request(string url, string accept)
   {
      var req = new HttpRequestMessage(HttpMethod.Get, url);
      req.Headers.Accept.ParseAdd(accept);
      var token = _Token();
      if (!string.IsNullOrWhiteSpace(token))
         req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
      return req;
   }

   private static string? _Token() => Environment.GetEnvironmentVariable(TokenEnvVar);

   private static void _EnsureSuccess(HttpResponseMessage resp, string what)
   {
      if (!resp.IsSuccessStatusCode)
         throw new InvalidOperationException("Downloading " + what + " failed: " + (int)resp.StatusCode + " " + resp.ReasonPhrase + ".");
   }

   private static string _String(JsonElement e, string name) =>
      e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

   // The tag as written minus the 'v' -- archive names use the csproj text
   // verbatim ("0.0.1"), not the normalized four-part Version.
   private static string _VersionText(string tag) => tag.Trim().TrimStart('v', 'V');

   private static bool _IsHex(string s)
   {
      foreach (var c in s)
      {
         if (!Uri.IsHexDigit(c))
            return false;
      }
      return true;
   }
}
