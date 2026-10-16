// Copyright 2026 Andreas Jung
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace SlimCodeDesktop.Agents.Settings;

/// <summary>One renderable choice for an enum-typed setting.</summary>
public readonly record struct SettingChoice(object Value, string Label);

/// <summary>
/// Reflection-derived description of one [Setting] property. Cached per-Type;
/// the UI builder switches on <see cref="Kind"/> to pick a control, and
/// <see cref="GetValue"/> / <see cref="SetValue"/> read and write through to
/// the live POCO instance owned by the agent (or app).
/// </summary>
public sealed class SettingDescriptor
{
   public PropertyInfo Property { get; }
   public string Label { get; }
   public string? Help { get; }
   public bool Multiline { get; }
   public Type Kind => Property.PropertyType;
   public IReadOnlyList<SettingChoice>? Choices { get; }   // non-null only for enums

   /// <summary>Numeric step for up/down. 0 means unset.</summary>
   public int Increment { get; }

   /// <summary>Quick-pick chip values (numeric settings only). Null if unset.</summary>
   public IReadOnlyList<int>? Suggestions { get; }

   /// <summary>Lower bound. Null if unset.</summary>
   public int? Min { get; }

   /// <summary>Upper bound. Null if unset.</summary>
   public int? Max { get; }

   internal SettingDescriptor(
      PropertyInfo p, string label, string? help, bool multiline,
      IReadOnlyList<SettingChoice>? choices,
      int increment, IReadOnlyList<int>? suggestions, int? min, int? max)
   {
      Property = p; Label = label; Help = help; Multiline = multiline; Choices = choices;
      Increment = increment; Suggestions = suggestions; Min = min; Max = max;
   }

   public object? GetValue(object instance) => Property.GetValue(instance);
   public void   SetValue(object instance, object? value) => Property.SetValue(instance, value);
}

/// <summary>
/// Builds and caches <see cref="SettingDescriptor"/> lists from a POCO Type
/// by scanning its public instance properties for <see cref="SettingAttribute"/>.
/// </summary>
public static class SettingsReflector
{
   private static readonly ConcurrentDictionary<Type, IReadOnlyList<SettingDescriptor>> _cache = new();

   public static IReadOnlyList<SettingDescriptor> For(Type type) => _cache.GetOrAdd(type, Build);

   private static IReadOnlyList<SettingDescriptor> Build(Type type)
   {
      var list = new List<SettingDescriptor>();
      foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
      {
         var attr = p.GetCustomAttribute<SettingAttribute>();
         if (attr == null) continue;

         IReadOnlyList<SettingChoice>? choices = null;
         if (p.PropertyType.IsEnum)
         {
            var arr = new List<SettingChoice>();
            foreach (var name in Enum.GetNames(p.PropertyType))
            {
               var f = p.PropertyType.GetField(name)!;
               var ca = f.GetCustomAttribute<SettingChoiceAttribute>();
               arr.Add(new SettingChoice(Enum.Parse(p.PropertyType, name), ca?.Label ?? name));
            }
            choices = arr;
         }
         int? min = attr.Min == int.MinValue ? null : attr.Min;
         int? max = attr.Max == int.MinValue ? null : attr.Max;
         IReadOnlyList<int>? suggestions = attr.Suggestions is { Length: > 0 } s ? s : null;

         list.Add(new SettingDescriptor(p, attr.Label, attr.Help, attr.Multiline, choices,
                                        attr.Increment, suggestions, min, max));
      }
      return list;
   }
}
