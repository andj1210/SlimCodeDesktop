// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System.IO;
using SlimCodeDesktop.Agents.Native.Config;
using SlimCodeDesktop.Agents.Native.Mcp;
using SlimCodeDesktop.Agents.Native.Skills;
using SlimCodeDesktop.Core;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop.Views.Native;

/// <summary>
/// Modal editor for a project's <see cref="ProjectConfig"/> — the multi-step
/// build / test commands + per-step timeouts. Edits a local copy; only Save
/// commits via the supplied callback. Cancel / close discards.
/// </summary>
public partial class ProjectSettingsWindow : Window
{
   public ProjectSettingsWindow()
   {
      InitializeComponent();
   }

   /// <summary>
   /// Bind to a config + a save callback. The dialog works on a clone.
   /// When <paramref name="onAliasSave"/> is supplied (sidebar caller) the
   /// display-alias field is shown and its value is reported back on Save;
   /// callers without a sidebar entry (e.g. the chat view) pass null and the
   /// field stays hidden.
   /// </summary>
   public void Configure(ProjectConfig config, string projectPath,
                         IReadOnlyList<SkillInfo> skills, Action<ProjectConfig> onSave,
                         string? alias = null, Action<string?>? onAliasSave = null)
   {
      _onSave      = onSave;
      _onAliasSave = onAliasSave;
      _projectPath = projectPath ?? "";
      _working     = (config ?? new ProjectConfig()).Clone();
      _skills      = skills ?? new List<SkillInfo>();

      if (onAliasSave != null)
      {
         AliasSection.IsVisible = true;
         TxtAlias.Text          = alias ?? "";
      }

      NudBuildTimeout.Value = _working.BuildTimeoutSec;
      NudTestTimeout.Value  = _working.TestTimeoutSec;

      ChkSkipSkillAnnouncement.IsChecked = _working.SkipSkillAnnouncement;

      _ApplyKindLayout();

      if (_working.Kind == ProjectKind.Mcp)
      {
         RebuildMcpRows();
         return;
      }

      RebuildBuildRows();
      RebuildTestRows();
      RebuildSkillRows();
      _RefreshProjectSkillsButton();
   }

   // -- project kind ---------------------------------------------------------

   /// <summary>
   /// A code project and an MCP project are configured by two disjoint sets of
   /// controls, so the dialog shows one layout or the other — never a combo to
   /// switch between them. The kind is decided when the project is added and
   /// is fixed for its lifetime: it selects the storage slug and the entire
   /// tool set, so changing it here would orphan the project's own config
   /// rather than convert it.
   /// </summary>
   private void _ApplyKindLayout()
   {
      bool mcp = _working.Kind == ProjectKind.Mcp;
      McpSection.IsVisible   = mcp;
      CodeSections.IsVisible = !mcp;

      Title = mcp ? "MCP project setup" : "Project setup";

      TxtIntro.Text = mcp
         ? "Sessions in this project talk to MCP servers instead of using the built-in coding tools — no file, build, test or subagent tools, only what the servers advertise. Everything else about a session is unchanged: same transcript, resume, permission prompts and tool cards."
         : "Build / test steps the agent may run for this project. The model can only trigger these fixed steps (no free-form shell); each runs in order and stops at the first failure. 'Log on success' attaches a step's output even when it succeeds — failed steps always include their log.";
   }

   // -- row UI ---------------------------------------------------------------

   private void RebuildBuildRows()
   {
      BuildStepsPanel.Children.Clear();
      if (_working.BuildSteps.Count == 0)
         BuildStepsPanel.Children.Add(EmptyHint());
      foreach (var step in _working.BuildSteps)
         BuildStepsPanel.Children.Add(BuildStepRow(step, _working.BuildSteps, RebuildBuildRows));
   }

   private void RebuildTestRows()
   {
      TestStepsPanel.Children.Clear();
      if (_working.TestSteps.Count == 0)
         TestStepsPanel.Children.Add(EmptyHint());
      foreach (var step in _working.TestSteps)
         TestStepsPanel.Children.Add(BuildStepRow(step, _working.TestSteps, RebuildTestRows));
   }

   private static TextBlock EmptyHint() => new()
   {
      Text       = "(no steps — add one; an empty list hides the tool from the model)",
      Foreground = ThemeBrushes.TextMuted,
      FontStyle  = FontStyle.Italic,
      Margin     = new Thickness(4, 4),
   };

   // -- skills ---------------------------------------------------------------

   private void RebuildSkillRows()
   {
      SkillsPanel.Children.Clear();
      if (_skills.Count == 0)
      {
         SkillsPanel.Children.Add(new TextBlock
         {
            Text       = "(no skills discovered — add a SKILL.md folder under one of the skills folders below)",
            Foreground = ThemeBrushes.TextMuted,
            FontStyle  = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
            Margin     = new Thickness(4, 4),
         });
         return;
      }

      foreach (var sk in _skills)
         SkillsPanel.Children.Add(SkillRow(sk));
   }

   private Control SkillRow(SkillInfo sk)
   {
      var chk = new CheckBox
      {
         IsChecked         = _working.AutoLoadSkills.Contains(sk.Name),
         VerticalAlignment = VerticalAlignment.Center,
      };
      chk.IsCheckedChanged += (_, _) =>
      {
         _working.AutoLoadSkills.Remove(sk.Name);
         if (chk.IsChecked == true) _working.AutoLoadSkills.Add(sk.Name);
      };

      var nameTb = new TextBlock
      {
         Text              = sk.Name + "  [" + sk.SourceLabel + "]",
         FontWeight        = FontWeight.SemiBold,
         Foreground        = ThemeBrushes.TextPrimary,
         VerticalAlignment = VerticalAlignment.Center,
      };
      var descTb = new TextBlock
      {
         Text         = string.IsNullOrWhiteSpace(sk.Description) ? "(no description)" : sk.Description,
         Foreground   = ThemeBrushes.TextMuted,
         FontSize     = 11,
         TextWrapping = TextWrapping.Wrap,
      };
      var pathTb = new TextBlock
      {
         Text       = sk.Dir,
         Foreground = ThemeBrushes.TextMuted,
         FontFamily = MONO,
         FontSize   = 10,
         TextTrimming = TextTrimming.CharacterEllipsis,
      };

      var textStack = new StackPanel { Spacing = 1 };
      textStack.Children.Add(nameTb);
      textStack.Children.Add(descTb);
      textStack.Children.Add(pathTb);

      var grid = new Grid
      {
         ColumnDefinitions = new ColumnDefinitions("Auto,*"),
         Margin            = new Thickness(2, 4),
      };
      Grid.SetColumn(chk,       0);
      Grid.SetColumn(textStack, 1);
      chk.Margin       = new Thickness(0, 0, 8, 0);
      ToolTip.SetTip(chk, "Auto-load this skill at the start of every new session for this project.");
      grid.Children.Add(chk);
      grid.Children.Add(textStack);
      return grid;
   }

   private void BtnOpenGlobalSkills_Click(object? sender, RoutedEventArgs e)
   {
      // The global root lives under %APPDATA% (app-owned), so creating it on
      // demand is fine — unlike the project root, which we never touch unasked.
      var dir = SkillStore.GlobalSkillsDir();
      if (string.IsNullOrEmpty(dir)) return;
      try { Directory.CreateDirectory(dir); } catch { }
      ExternalTools.OpenFolder(dir);
   }

   // The label flips between "Create…" and "Open…": we never create the
   // project skills folder proactively, only when the user explicitly asks.
   private void BtnOpenProjectSkills_Click(object? sender, RoutedEventArgs e)
   {
      var dir = SkillStore.ProjectSkillsDir(_projectPath);
      if (string.IsNullOrEmpty(dir)) return;
      if (!Directory.Exists(dir))
      {
         try { Directory.CreateDirectory(dir); } catch { return; }
      }
      ExternalTools.OpenFolder(dir);
      _RefreshProjectSkillsButton();
   }

   private void _RefreshProjectSkillsButton()
   {
      // Null when the "Project skill folders" app setting is empty.
      var dir = SkillStore.ProjectSkillsDir(_projectPath);
      bool exists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
      BtnOpenProjectSkills.IsEnabled = !string.IsNullOrEmpty(dir);
      BtnOpenProjectSkills.Content = exists
         ? "Open project skills folder"
         : "Create project skills folder";
      ToolTip.SetTip(BtnOpenProjectSkills, dir ?? "No project skill folder configured (Application settings)");
   }

   // -- MCP servers ----------------------------------------------------------

   private void RebuildMcpRows()
   {
      McpServersPanel.Children.Clear();
      _mcpCommits.Clear();

      if (_working.McpServers.Count == 0)
      {
         McpServersPanel.Children.Add(new TextBlock
         {
            Text         = "(no servers — add one; an MCP project with no server gives the model no tools at all)",
            Foreground   = ThemeBrushes.TextMuted,
            FontStyle    = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(4, 4),
         });
         return;
      }

      foreach (var s in _working.McpServers)
         McpServersPanel.Children.Add(McpServerRow(s));
   }

   private void BtnAddMcpServer_Click(object? sender, RoutedEventArgs e)
   {
      _CommitMcpRows();
      _working.McpServers.Add(new McpServerConfig
      {
         Name    = "server" + (_working.McpServers.Count + 1),
         Command = "npx",
         Args    = new List<string> { "-y" },
      });
      RebuildMcpRows();
   }

   /// <summary>
   /// One server card. Every editable control writes back through a commit
   /// action rather than a LostFocus handler: Test connection has to see what
   /// is on screen right now, and relying on focus having moved first is the
   /// kind of thing that works until it doesn't.
   /// </summary>
   private Control McpServerRow(McpServerConfig cfg)
   {
      var nameTb    = Field(cfg.Name, "Name (becomes the tool prefix)");
      var enabledCk = new CheckBox
      {
         IsChecked         = cfg.Enabled,
         Content           = "Enabled",
         FontSize          = 11,
         VerticalAlignment = VerticalAlignment.Center,
      };

      var transportCb = new ComboBox
      {
         ItemsSource = new List<ComboBoxItem>
         {
            new() { Content = "stdio (child process)", Tag = McpTransportKind.Stdio },
            new() { Content = "streamable HTTP",       Tag = McpTransportKind.StreamableHttp },
         },
         SelectedIndex     = cfg.Transport == McpTransportKind.StreamableHttp ? 1 : 0,
         MinWidth          = 180,
         VerticalAlignment = VerticalAlignment.Center,
      };

      var delBtn = new Button
      {
         Content           = "✕",
         Padding           = new Thickness(8, 2),
         Foreground        = ThemeBrushes.TextPrimary,
         Background        = ThemeBrushes.NeutralButtonBackground,
         VerticalAlignment = VerticalAlignment.Center,
         Margin            = new Thickness(8, 0, 0, 0),
      };

      var timeoutNud = new NumericUpDown
      {
         Minimum      = 1,
         Maximum      = 3600,
         Increment    = 10,
         FormatString = "0",
         Width        = 110,
         Value        = cfg.TimeoutSec > 0 ? cfg.TimeoutSec : 60,
         Foreground   = ThemeBrushes.TextPrimary,
         Background   = ThemeBrushes.PanelBackground,
      };
      ToolTip.SetTip(timeoutNud, "Per tool call. A call that outruns this comes back to the model as a timeout error.");

      // -- transport-specific fields ------------------------------------------

      var commandTb = Field(cfg.Command, "Command (e.g. npx, uvx, or a full path)");
      var argsTb    = Field(JoinArgs(cfg.Args), "Arguments (space separated; quote anything containing spaces)");
      var cwdTb     = Field(cfg.Cwd ?? "", "Working directory (empty = the project folder)");
      var envTb     = Area(JoinPairs(cfg.Env, '='), "Environment, one KEY=VALUE per line");

      var urlTb     = Field(cfg.Url, "URL (the server's single MCP endpoint)");
      var headersTb = Area(JoinPairs(cfg.Headers, ':'), "Headers, one Name: value per line");

      var stdioPanel = Labelled(("Command", commandTb), ("Arguments", argsTb),
                                ("Working dir", cwdTb), ("Environment", envTb));
      var httpPanel  = Labelled(("URL", urlTb), ("Headers", headersTb));

      void ApplyTransport()
      {
         bool http = transportCb.SelectedItem is ComboBoxItem t &&
                     t.Tag is McpTransportKind.StreamableHttp;
         stdioPanel.IsVisible = !http;
         httpPanel.IsVisible  = http;
      }
      ApplyTransport();
      transportCb.SelectionChanged += (_, _) => ApplyTransport();

      // -- test connection -----------------------------------------------------

      var resultPanel = new StackPanel { Spacing = 3, Margin = new Thickness(0, 6, 0, 0) };
      var testBtn = new Button { Content = "Test connection", Padding = new Thickness(10, 4) };
      ToolTip.SetTip(testBtn,
         "Connect, negotiate and list tools exactly the way a session's first message does, "
       + "then disconnect. For stdio, 'reachable' means the process started and answered initialize.");

      delBtn.Click += (_, _) =>
      {
         _CommitMcpRows();
         _working.McpServers.Remove(cfg);
         RebuildMcpRows();
      };

      // -- commit -------------------------------------------------------------

      _mcpCommits.Add(() =>
      {
         cfg.Name      = (nameTb.Text ?? "").Trim();
         cfg.Enabled   = enabledCk.IsChecked == true;
         cfg.Transport = transportCb.SelectedItem is ComboBoxItem ti && ti.Tag is McpTransportKind k
                         ? k : McpTransportKind.Stdio;
         cfg.Command   = (commandTb.Text ?? "").Trim();
         cfg.Args      = SplitArgs(argsTb.Text ?? "");
         cfg.Cwd       = string.IsNullOrWhiteSpace(cwdTb.Text) ? null : cwdTb.Text!.Trim();
         cfg.Env       = SplitPairs(envTb.Text ?? "", '=');
         cfg.Url       = (urlTb.Text ?? "").Trim();
         cfg.Headers   = SplitPairs(headersTb.Text ?? "", ':');
         cfg.TimeoutSec = timeoutNud.Value is decimal d && d > 0 ? (int)d : 60;
      });

      testBtn.Click += async (_, _) =>
      {
         _CommitMcpRows();
         testBtn.IsEnabled = false;
         try { await TestMcpServer(cfg, resultPanel); }
         finally { testBtn.IsEnabled = true; }
      };

      // -- layout -------------------------------------------------------------

      var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
      Grid.SetColumn(nameTb,      0);
      Grid.SetColumn(transportCb, 1);
      Grid.SetColumn(enabledCk,   2);
      Grid.SetColumn(delBtn,      3);
      transportCb.Margin = new Thickness(8, 0, 0, 0);
      enabledCk.Margin   = new Thickness(10, 0, 0, 0);
      head.Children.Add(nameTb);
      head.Children.Add(transportCb);
      head.Children.Add(enabledCk);
      head.Children.Add(delBtn);

      var footer = new StackPanel
      {
         Orientation = Orientation.Horizontal,
         Spacing     = 10,
         Margin      = new Thickness(0, 6, 0, 0),
      };
      footer.Children.Add(testBtn);
      footer.Children.Add(new TextBlock
      {
         Text              = "Call timeout (s):",
         FontSize          = 11,
         Foreground        = ThemeBrushes.TextMuted,
         VerticalAlignment = VerticalAlignment.Center,
      });
      footer.Children.Add(timeoutNud);

      var body = new StackPanel { Spacing = 4 };
      body.Children.Add(head);
      body.Children.Add(stdioPanel);
      body.Children.Add(httpPanel);
      body.Children.Add(footer);
      body.Children.Add(resultPanel);

      return new Border
      {
         BorderBrush     = ThemeBrushes.BorderColor,
         BorderThickness = new Thickness(1),
         CornerRadius    = new CornerRadius(3),
         Padding         = new Thickness(8),
         Child           = body,
      };
   }

   /// <summary>
   /// Connect the way a session would, report what came back, and offer a
   /// checkbox per discovered tool that writes
   /// <see cref="McpServerConfig.EnabledTools"/>. The connection is dropped
   /// again immediately — this is a probe, not a session.
   /// </summary>
   private async Task TestMcpServer(McpServerConfig cfg, StackPanel resultPanel)
   {
      resultPanel.Children.Clear();
      resultPanel.Children.Add(Note("connecting…", ThemeBrushes.TextMuted));

      var handle = new McpServerHandle(cfg.Clone(), _projectPath);
      var log    = new List<string>();
      handle.SetLogSink((_, line) => log.Add(line));

      bool ok;
      using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TEST_TIMEOUT_SEC)))
      {
         try { ok = await handle.ConnectAsync(cts.Token); }
         catch (Exception ex) { ok = false; log.Add(ex.Message); }
      }

      resultPanel.Children.Clear();

      if (!ok)
      {
         resultPanel.Children.Add(Note("failed: " + (handle.Error ?? "unknown reason"), FAIL_BRUSH));
         // The server's own stderr is usually the only thing that explains a
         // failed launch ("command not found", a stack trace from the module).
         foreach (var line in log)
            resultPanel.Children.Add(Note(line, ThemeBrushes.TextMuted, mono: true));
         handle.Dispose();
         return;
      }

      var id = handle.Identity;
      resultPanel.Children.Add(Note("connected — " + (id?.Describe ?? ""), OK_BRUSH));

      var tools = handle.AllTools;
      if (tools.Count == 0)
      {
         resultPanel.Children.Add(Note("the server advertises no tools", ThemeBrushes.TextMuted));
         handle.Dispose();
         return;
      }

      resultPanel.Children.Add(Note(
         "Tools (unchecked ones are not advertised to the model):", ThemeBrushes.TextMutedStrong));

      var boxes = new List<(CheckBox Box, string Name)>();
      foreach (var t in tools)
      {
         var box = new CheckBox
         {
            IsChecked = cfg.EnabledTools == null || cfg.EnabledTools.Contains(t.Name),
            FontSize  = 11,
            Content   = new TextBlock
            {
               Text         = t.Name + (string.IsNullOrWhiteSpace(t.Description) ? "" : " — " + t.Description),
               FontSize     = 11,
               TextWrapping = TextWrapping.Wrap,
               Foreground   = ThemeBrushes.TextPrimary,
            },
         };
         if (t.ReadOnlyHint == true)
            ToolTip.SetTip(box, "Annotated read-only by the server, so it is auto-allowed by the permission gate.");
         boxes.Add((box, t.Name));
         resultPanel.Children.Add(box);
      }

      void WriteAllowList()
      {
         var kept = new List<string>();
         foreach (var (box, name) in boxes)
            if (box.IsChecked == true) kept.Add(name);
         // All checked means "everything", stored as null — so a tool the
         // server adds later is picked up instead of being silently excluded
         // by a list written before it existed.
         cfg.EnabledTools = kept.Count == boxes.Count ? null : kept;
      }
      foreach (var (box, _) in boxes)
         box.IsCheckedChanged += (_, _) => WriteAllowList();
      WriteAllowList();

      handle.Dispose();
   }

   // -- small control factories ----------------------------------------------

   private static TextBox Field(string text, string placeholder) => new()
   {
      Text              = text,
      PlaceholderText   = placeholder,
      Foreground        = ThemeBrushes.TextPrimary,
      Background        = ThemeBrushes.PanelBackground,
      CaretBrush        = ThemeBrushes.TextPrimary,
      BorderBrush       = ThemeBrushes.BorderColor,
      VerticalAlignment = VerticalAlignment.Center,
   };

   private static TextBox Area(string text, string placeholder) => new()
   {
      Text            = text,
      PlaceholderText = placeholder,
      AcceptsReturn   = true,
      MinHeight       = 46,
      FontFamily      = MONO,
      FontSize        = 11,
      Foreground      = ThemeBrushes.TextPrimary,
      Background      = ThemeBrushes.PanelBackground,
      CaretBrush      = ThemeBrushes.TextPrimary,
      BorderBrush     = ThemeBrushes.BorderColor,
   };

   private static TextBlock Note(string text, IBrush brush, bool mono = false) => new()
   {
      Text         = text,
      Foreground   = brush,
      FontSize     = 11,
      FontFamily   = mono ? MONO : FontFamily.Default,
      TextWrapping = TextWrapping.Wrap,
   };

   // A label column in front of each field, so the transport blocks read as a
   // form rather than a stack of placeholders.
   private static StackPanel Labelled(params (string Label, Control Field)[] rows)
   {
      var panel = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4, 0, 0) };
      foreach (var (label, field) in rows)
      {
         var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
         var tb = new TextBlock
         {
            Text              = label,
            FontSize          = 11,
            Foreground        = ThemeBrushes.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
         };
         Grid.SetColumn(tb, 0);
         Grid.SetColumn(field, 1);
         grid.Children.Add(tb);
         grid.Children.Add(field);
         panel.Children.Add(grid);
      }
      return panel;
   }

   private void _CommitMcpRows()
   {
      foreach (var commit in _mcpCommits)
      {
         try { commit(); } catch { }
      }
   }

   // -- text <-> structure ----------------------------------------------------

   /// <summary>
   /// Split a command line into argv, honoring double quotes so a path with
   /// spaces survives. Deliberately not a shell parser: there is no shell in
   /// the launch path, so there are no variables or globs to expand.
   /// </summary>
   internal static List<string> SplitArgs(string line)
   {
      var args = new List<string>();
      var cur  = new System.Text.StringBuilder();
      bool inQuotes = false;
      // Tracked separately from cur.Length so an explicit "" stays an
      // argument instead of vanishing.
      bool haveToken = false;

      foreach (var ch in line)
      {
         if (ch == '"')
         {
            inQuotes  = !inQuotes;
            haveToken = true;
            continue;
         }
         if (!inQuotes && char.IsWhiteSpace(ch))
         {
            if (haveToken) { args.Add(cur.ToString()); cur.Clear(); haveToken = false; }
            continue;
         }
         cur.Append(ch);
         haveToken = true;
      }
      if (haveToken) args.Add(cur.ToString());
      return args;
   }

   internal static string JoinArgs(IReadOnlyList<string>? args)
   {
      if (args == null || args.Count == 0) return "";
      var sb = new System.Text.StringBuilder();
      foreach (var a in args)
      {
         if (sb.Length > 0) sb.Append(' ');
         if (a.Length == 0 || a.Contains(' ') || a.Contains('"'))
            sb.Append('"').Append(a.Replace("\"", "")).Append('"');
         else
            sb.Append(a);
      }
      return sb.ToString();
   }

   // "KEY=VALUE" / "Name: value", one per line. The FIRST separator splits, so
   // a value containing one (a URL in a header, say) survives intact.
   internal static Dictionary<string, string> SplitPairs(string text, char separator)
   {
      var map = new Dictionary<string, string>();
      foreach (var raw in text.Split('\n'))
      {
         var line = raw.Trim();
         if (line.Length == 0 || line[0] == '#') continue;
         int i = line.IndexOf(separator);
         if (i <= 0) continue;
         var key = line.Substring(0, i).Trim();
         var val = line.Substring(i + 1).Trim();
         if (key.Length > 0) map[key] = val;
      }
      return map;
   }

   internal static string JoinPairs(IReadOnlyDictionary<string, string>? map, char separator)
   {
      if (map == null || map.Count == 0) return "";
      var sb = new System.Text.StringBuilder();
      foreach (var kv in map)
      {
         if (sb.Length > 0) sb.Append('\n');
         sb.Append(kv.Key).Append(separator);
         if (separator == ':') sb.Append(' ');
         sb.Append(kv.Value);
      }
      return sb.ToString();
   }

   private Control BuildStepRow(CommandStep step, List<CommandStep> owner, Action rebuild)
   {
      var nameTb = new TextBox
      {
         Text              = step.Name,
         PlaceholderText   = "Name (e.g. Setup)",
         Foreground        = ThemeBrushes.TextPrimary,
         Background        = ThemeBrushes.PanelBackground,
         CaretBrush        = ThemeBrushes.TextPrimary,
         BorderBrush       = ThemeBrushes.BorderColor,
         VerticalAlignment = VerticalAlignment.Center,
      };
      nameTb.LostFocus += (_, _) => step.Name = nameTb.Text ?? "";

      var cmdTb = new TextBox
      {
         Text              = step.Command,
         PlaceholderText   = "Command (e.g. cmake --build build)",
         FontFamily        = MONO,
         Foreground        = ThemeBrushes.TextPrimary,
         Background        = ThemeBrushes.PanelBackground,
         CaretBrush        = ThemeBrushes.TextPrimary,
         BorderBrush       = ThemeBrushes.BorderColor,
         VerticalAlignment = VerticalAlignment.Center,
      };
      cmdTb.LostFocus += (_, _) => step.Command = cmdTb.Text ?? "";

      var logChk = new CheckBox
      {
         IsChecked         = step.LogOnSucceed,
         Content           = "Log on success",
         FontSize          = 11,
         VerticalAlignment = VerticalAlignment.Center,
      };
      ToolTip.SetTip(logChk,
         "Attach this step's log even when it succeeds. Failed steps always include their log.");
      logChk.IsCheckedChanged += (_, _) => step.LogOnSucceed = logChk.IsChecked == true;

      var delBtn = new Button
      {
         Content    = "✕",
         Padding    = new Thickness(8, 2),
         Foreground = ThemeBrushes.TextPrimary,
         Background = ThemeBrushes.NeutralButtonBackground,
         VerticalAlignment = VerticalAlignment.Center,
      };
      delBtn.Click += (_, _) => { owner.Remove(step); rebuild(); };

      var grid = new Grid
      {
         ColumnDefinitions = new ColumnDefinitions("160,*,Auto,Auto"),
         Margin            = new Thickness(2, 3),
      };
      Grid.SetColumn(nameTb, 0);
      Grid.SetColumn(cmdTb,  1);
      Grid.SetColumn(logChk, 2);
      Grid.SetColumn(delBtn, 3);
      cmdTb.Margin  = new Thickness(6, 0, 0, 0);
      logChk.Margin = new Thickness(10, 0, 0, 0);
      delBtn.Margin = new Thickness(8, 0, 0, 0);
      grid.Children.Add(nameTb);
      grid.Children.Add(cmdTb);
      grid.Children.Add(logChk);
      grid.Children.Add(delBtn);
      return grid;
   }

   // -- footer / add ---------------------------------------------------------

   private void BtnAddBuild_Click(object? sender, RoutedEventArgs e)
   {
      _working.BuildSteps.Add(new CommandStep());
      RebuildBuildRows();
   }

   private void BtnAddTest_Click(object? sender, RoutedEventArgs e)
   {
      _working.TestSteps.Add(new CommandStep());
      RebuildTestRows();
   }

   private void BtnTryBuild_Click(object? sender, RoutedEventArgs e) => TryInTerminal(_working.BuildSteps);
   private void BtnTryTest_Click(object? sender, RoutedEventArgs e)  => TryInTerminal(_working.TestSteps);

   // Chain the runnable steps in a cmd.exe kept open in the project folder.
   // Layout per step:
   //   ********* - Running Step: "Name" (i/N) - *********
   //   <command output>
   //   ********* - Step "Name" finished (exit=<code>) - *********
   //   <blank line>
   //
   // Steps run unconditionally ('&', not '&&') so the post-banner always
   // reports the exit code -- useful for a "try the commands" affordance
   // where every result is informative, even after a failure.
   //
   // 'call echo ... %^errorlevel%' is the classic cmd.exe trick to force
   // re-expansion of %errorlevel% *after* the command ran (plain %errorlevel%
   // is expanded at parse time, before the step has had a chance to set it).
   private void TryInTerminal(List<CommandStep> steps)
   {
      if (string.IsNullOrEmpty(_projectPath)) return;

      var runnable = new List<CommandStep>();
      foreach (var s in steps)
         if (!string.IsNullOrWhiteSpace(s.Command)) runnable.Add(s);
      if (runnable.Count == 0) return;

      var sb = new System.Text.StringBuilder();
      for (int i = 0; i < runnable.Count; i++)
      {
         var s     = runnable[i];
         var label = string.IsNullOrWhiteSpace(s.Name) ? ("step " + (i + 1)) : s.Name;
         var pre   = "********* - Running Step: \"" + label +
                     "\" (" + (i + 1) + "/" + runnable.Count + ") - *********";
         var post  = "********* - Step \"" + label + "\" finished (exit=%^errorlevel%) - *********";

         if (sb.Length > 0) sb.Append(" & ");
         sb.Append("echo ").Append(pre)
           .Append(" & ").Append(s.Command)
           .Append(" & call echo ").Append(post)
           .Append(" & echo.");
      }
      ExternalTools.RunInCmd(_projectPath, sb.ToString());
   }

   private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();

   private void BtnSave_Click(object? sender, RoutedEventArgs e)
   {
      _working.BuildTimeoutSec = NudBuildTimeout.Value is decimal b ? (int)b : 120;
      _working.TestTimeoutSec  = NudTestTimeout.Value  is decimal t ? (int)t : 120;

      _working.SkipSkillAnnouncement = ChkSkipSkillAnnouncement.IsChecked == true;

      // Drop fully-empty rows (no command); a named step with no command is useless.
      _working.BuildSteps = Prune(_working.BuildSteps);
      _working.TestSteps  = Prune(_working.TestSteps);

      // MCP cards write back only on demand, so pull them in before pruning.
      // A server with no name or no target can never be connected to, and
      // keeping it would only produce a failed pill every session.
      _CommitMcpRows();
      var servers = new List<McpServerConfig>();
      foreach (var s in _working.McpServers)
         if (!string.IsNullOrWhiteSpace(s.Name) && s.IsRunnable) servers.Add(s);
      _working.McpServers = servers;

      try { _onSave?.Invoke(_working); } catch { }
      if (_onAliasSave != null)
      {
         var alias = TxtAlias.Text;
         try { _onAliasSave(string.IsNullOrWhiteSpace(alias) ? null : alias); } catch { }
      }
      Close();
   }

   private static List<CommandStep> Prune(List<CommandStep> steps)
   {
      var clean = new List<CommandStep>();
      foreach (var s in steps)
         if (!string.IsNullOrWhiteSpace(s.Command)) clean.Add(s);
      return clean;
   }

   // -- fields ---------------------------------------------------------------

   private static readonly FontFamily MONO =
      new("Consolas, Cascadia Mono, Courier New, monospace");

   private static readonly IBrush OK_BRUSH   = new SolidColorBrush(Color.Parse("#6AC96A"));
   private static readonly IBrush FAIL_BRUSH = new SolidColorBrush(Color.Parse("#D86A6A"));

   // Shorter than a session's connect budget on purpose: this is an
   // interactive probe with a button the user is watching, not a turn they
   // already committed to.
   private const int TEST_TIMEOUT_SEC = 60;

   private ProjectConfig _working = new();
   private string _projectPath = "";
   private IReadOnlyList<SkillInfo> _skills = new List<SkillInfo>();
   private Action<ProjectConfig>? _onSave;
   private Action<string?>? _onAliasSave;

   // One writer per MCP server card, pulling the on-screen values into the
   // config object. Rebuilt with the rows.
   private readonly List<Action> _mcpCommits = new();
}
