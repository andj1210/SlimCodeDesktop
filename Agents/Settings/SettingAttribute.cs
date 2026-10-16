// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace SlimCodeDesktop.Agents.Settings;

/// <summary>
/// Marks a property on a settings POCO as user-editable. The property's CLR
/// type drives both UI rendering (bool -> CheckBox, enum -> ComboBox, …) and
/// JSON persistence. The property's name (case-insensitive) is the JSON key.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute : Attribute
{
   public string Label { get; }
   public string? Help { get; set; }

   /// <summary>String settings only: render as a multi-line TextBox.</summary>
   public bool Multiline { get; set; }

   /// <summary>Numeric (int) settings: step for up/down. 0 = use default (1).</summary>
   public int Increment { get; set; }

   /// <summary>Numeric (int) settings: quick-pick suggestion values rendered as chips.</summary>
   public int[]? Suggestions { get; set; }

   /// <summary>Numeric (int) settings: lower bound. int.MinValue = unset (defaults applied per-control).</summary>
   public int Min { get; set; } = int.MinValue;

   /// <summary>Numeric (int) settings: upper bound. int.MinValue = unset.</summary>
   public int Max { get; set; } = int.MinValue;

   public SettingAttribute(string label) { Label = label; }
}

/// <summary>
/// Optional display label for an enum member used as a setting choice.
/// When absent, the enum member name is shown verbatim.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SettingChoiceAttribute : Attribute
{
   public string Label { get; }
   public SettingChoiceAttribute(string label) { Label = label; }
}

/// <summary>Marker for "this agent has no settings". Always used as the default.</summary>
public sealed class EmptySettings { }
