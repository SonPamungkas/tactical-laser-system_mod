using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
namespace TLS.Extra
{
    [HarmonyPatch(typeof(Encyclopedia), "AfterLoad", new Type[] { })]
    public static class QolExecutor
    {
        private static string[] _qolCommands = null;
        private static bool _hasRun = false;
        private static readonly Type[] DefinitionTypes = {
            typeof(VehicleDefinition),
            typeof(AircraftDefinition),
            typeof(BuildingDefinition),
            typeof(MissileDefinition),
            typeof(WeaponInfo)
        };
        private static readonly RoleIdentity FullRole = new RoleIdentity {
            antiSurface = 1f, antiAir = 1f, antiMissile = 1f, antiRadar = 1f
        };
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (_hasRun) return;
            _hasRun = true;
            if (Plugin._log == null) return;
            if (!Plugin.AntiEverything.Value)
            {
                Plugin._log.LogInfo("[TLS] Anti-Everything disabled, skipping role manifest.");
                return;
            }
            Plugin.Instance?.ScanForUnits();
            LoadCommands();
            var handled = new HashSet<UnitDefinition>();
            foreach (string line in _qolCommands)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                try
                {
                    Match fmMatch = FieldModifyPattern.Match(line.Trim());
                    if (fmMatch.Success) ProcessFieldModify(fmMatch, handled);
                    else Plugin._log.LogWarning($"[TLS] Unmatched command line: '{line}'");
                }
                catch (Exception ex)
                {
                    Plugin._log.LogError($"[TLS] Failed to process line: '{line}'. Error: {ex}");
                }
            }
            AutoDiscover(handled);
        }
        private static void LoadCommands()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                string resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("tls.commands.qol"));
                if (resourceName != null)
                {
                    using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        string content = reader.ReadToEnd();
                        _qolCommands = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    }
                }
                else
                {
                    Plugin._log.LogError("[TLS] Embedded resource tls.commands.qol not found!");
                    _qolCommands = new string[0];
                }
            }
            catch (Exception ex)
            {
                Plugin._log.LogError($"[TLS] Failed to load commands: {ex}");
                _qolCommands = new string[0];
            }
        }
        private static readonly Regex FieldModifyPattern = new Regex(
            @"^(?<target>[^\s]+)\s+(?<component>[^\s]+)\s+(?<field>[^\s]+)\s+(?<operation>modify4|modify)\s+(?<value>[^\s]+)$",
            RegexOptions.Compiled);
        private static void ProcessFieldModify(Match match, HashSet<UnitDefinition> handled)
        {
            string target = match.Groups["target"].Value;
            string component = match.Groups["component"].Value;
            string fieldName = match.Groups["field"].Value;
            string valStr = match.Groups["value"].Value;
            foreach (Type type in DefinitionTypes)
            {
                UnityEngine.Object asset = Resources.FindObjectsOfTypeAll(type)
                    .FirstOrDefault(r => r.name.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (asset == null) continue;
                FieldInfo structField = type.GetField(component,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (structField == null)
                {
                    Plugin._log.LogWarning($"[TLS] Struct field '{component}' not found on {type.Name} for '{target}'");
                    return;
                }
                if (asset is UnitDefinition unitDef && !IsUnitEnabled(unitDef))
                {
                    handled.Add(unitDef);
                    return;
                }
                if (SetStructField(asset, structField, fieldName, valStr, target))
                {
                    if (asset is UnitDefinition def) handled.Add(def);
                }
                return;
            }
            Plugin._log.LogWarning($"[TLS] Asset not found: '{target}' (skipping {component}.{fieldName})");
        }
        private static bool SetStructField(UnityEngine.Object asset, FieldInfo structField,
                                           string fieldName, string valStr, string target)
        {
            object box = structField.GetValue(asset);
            FieldInfo inner = structField.FieldType.GetField(fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (inner == null)
            {
                Plugin._log.LogWarning($"[TLS] Field '{fieldName}' not found on {structField.FieldType.Name} for '{target}'");
                return false;
            }
            object before = inner.GetValue(box);
            inner.SetValue(box, Convert.ChangeType(valStr, inner.FieldType, CultureInfo.InvariantCulture));
            structField.SetValue(asset, box);
            Plugin._log.LogInfo($"[TLS] {target}.{structField.Name}.{fieldName}: {before} -> {valStr}");
            return true;
        }
        private static void AutoDiscover(HashSet<UnitDefinition> handled)
        {
            foreach (var laser in Resources.FindObjectsOfTypeAll<Laser>())
            {
                UnitDefinition def = Plugin.ResolveDefinition(laser);
                if (def == null || !handled.Add(def)) continue;
                if (!IsUnitEnabled(def)) continue;
                RoleIdentity before = def.roleIdentity;
                def.roleIdentity = FullRole;
                Plugin._log.LogInfo($"[TLS] auto: {def.name} ({def.unitName}) roleIdentity {Fmt(before)} -> {Fmt(FullRole)}");
                WeaponInfo info = laser.info;
                if (info != null && !IsFull(info.effectiveness))
                {
                    RoleIdentity beforeEff = info.effectiveness;
                    info.effectiveness = FullRole;
                    Plugin._log.LogInfo($"[TLS] auto: {info.name} effectiveness {Fmt(beforeEff)} -> {Fmt(FullRole)}");
                }
            }
        }
        private static bool IsUnitEnabled(UnitDefinition def)
        {
            if (string.IsNullOrEmpty(def.unitName)) return true;
            return !Plugin.UnitConfigs.TryGetValue(def.unitName, out var entry) || entry.Value;
        }
        private static bool IsFull(RoleIdentity r) =>
            r.antiSurface >= 1f && r.antiAir >= 1f && r.antiMissile >= 1f && r.antiRadar >= 1f;
        private static string Fmt(RoleIdentity r) =>
            $"{r.antiSurface:0.##}/{r.antiAir:0.##}/{r.antiMissile:0.##}/{r.antiRadar:0.##}";
    }
}