using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using System.Linq;
using System.Collections.Generic;
using System.Collections;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace TLS
{
    [BepInPlugin("com.tls.mod", "Tactical Laser System (TLS)", "2.2")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static Dictionary<string, ConfigEntry<bool>> UnitConfigs = new Dictionary<string, ConfigEntry<bool>>();
        public static ConfigEntry<float> MinRange;
        public static ManualLogSource _log;

        private void Awake()
        {
            Instance = this;
            _log = Logger;
            Logger.LogInfo("Tactical Laser System (TLS) starting...");

            MinRange = Config.Bind("General", "Minimum Range", 50f, "Minimum distance from the muzzle before damage starts (prevents self-damage).");

            var harmony = new Harmony("com.tls.mod");
            try
            {
                harmony.PatchAll();
                Logger.LogInfo("TLS Fully Active.");
                
                StartCoroutine(ScannerRoutine());
            }
            catch (Exception ex)
            {
                Logger.LogError($"TLS Hook failed: {ex}");
            }
        }

        private IEnumerator ScannerRoutine()
        {
            while (true)
            {
                ScanForUnits();
                yield return new WaitForSeconds(10f);
            }
        }

        public void ScanForUnits()
        {
            // Find all lasers (including prefabs)
            var lasers = Resources.FindObjectsOfTypeAll<Laser>();
            foreach (var l in lasers)
            {
                string uName = GetUnitName(l);
                if (string.IsNullOrEmpty(uName) || uName == "Unknown") continue;

                if (!UnitConfigs.ContainsKey(uName))
                {
                    UnitConfigs[uName] = Config.Bind("TLS Targets", uName, true, $"Enable TLS overdrive for {uName}");
                    Logger.LogInfo($"Scanner: Found laser-capable unit '{uName}'. Added toggle to ConfigManager.");
                }
            }
        }

        public static bool IsUnitComponent(MonoBehaviour mb)
        {
            if (mb == null) return false;
            Type t = mb.GetType();
            while (t != null && t.Name != "MonoBehaviour")
            {
                string n = t.Name;
                if (n == "Aircraft" || n == "Ship" || n == "GroundVehicle" || n == "Unit" || n == "Station")
                    return true;
                t = t.BaseType;
            }
            return false;
        }

        private static bool IsGenericName(string n)
        {
            n = n.ToLower();
            return n.Contains("nose") || n.Contains("pivot") || n.Contains("turret") || n.Contains("mount") || 
                   n.Contains("barrel") || n.Contains("muzzle") || n.Contains("mesh") || n.Contains("root") ||
                   n == "p" || n == "g"; // Common tiny names
        }

        private static string SanitizeUnitName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "Unknown";
            if (n.Contains("(Clone)")) n = n.Substring(0, n.IndexOf("(Clone)"));
            n = Regex.Replace(n, @"[ \-_]+\d+$", "").Trim();
            return n;
        }

        public static string GetUnitName(Component c)
        {
            if (c == null) return "Unknown";
            
            Transform current = c.transform;
            string fallbackName = null;

            while (current != null)
            {
                var mb = current.GetComponents<MonoBehaviour>();
                foreach (var comp in mb)
                {
                    if (comp == null) continue;
                    if (IsUnitComponent(comp))
                    {
                        string n = current.gameObject.name;
                        // If the unit object has a generic name, keep looking up for a better parent name
                        if (!IsGenericName(n)) return SanitizeUnitName(n);
                        fallbackName = n;
                    }
                }
                
                // Track the highest non-generic name we see
                if (!IsGenericName(current.gameObject.name))
                    fallbackName = current.gameObject.name;

                current = current.parent;
            }

            return SanitizeUnitName(fallbackName ?? c.transform.root.name);
        }

        // --- Role Identity Expansion ---
        // Caches original bool values of roleIdentity fields, keyed by unit instance ID
        private static readonly Dictionary<int, Dictionary<string, bool>> _roleCache =
            new Dictionary<int, Dictionary<string, bool>>();

        private static (MonoBehaviour unit, object roleId) GetRoleIdentity(Component laserComp)
        {
            // Walk up the hierarchy to find the unit MonoBehaviour
            foreach (var mb in laserComp.GetComponentsInParent<MonoBehaviour>())
            {
                if (!IsUnitComponent(mb)) continue;

                // Get the 'definition' field/property
                var defField = mb.GetType().GetField("definition",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object def = defField?.GetValue(mb);

                if (def == null) continue;

                // Get 'roleIdentity' from definition
                var roleField = def.GetType().GetField("roleIdentity",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object roleId = roleField?.GetValue(def);

                if (roleId != null) return (mb, roleId);
            }
            return (null, null);
        }

        public static void ExpandRoleIdentity(Component laserComp)
        {
            try
            {
                var (unit, roleId) = GetRoleIdentity(laserComp);
                if (unit == null || roleId == null) return;

                int id = unit.GetInstanceID();
                if (_roleCache.ContainsKey(id)) return; // already expanded

                // Cache and set all bool fields to true
                var saved = new Dictionary<string, bool>();
                var roleType = roleId.GetType();
                foreach (var f in roleType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType != typeof(bool)) continue;
                    saved[f.Name] = (bool)f.GetValue(roleId);
                    f.SetValue(roleId, true);
                }
                foreach (var p in roleType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.PropertyType != typeof(bool) || !p.CanWrite) continue;
                    saved["prop_" + p.Name] = (bool)p.GetValue(roleId);
                    p.SetValue(roleId, true);
                }
                _roleCache[id] = saved;
                _log.LogInfo($"[TLS] Expanded roleIdentity for '{GetUnitName(laserComp)}' ({saved.Count} flags).");
            }
            catch (Exception ex) { _log.LogWarning($"[TLS] ExpandRoleIdentity failed: {ex.Message}"); }
        }

        public static void RestoreRoleIdentity(Component laserComp)
        {
            try
            {
                var (unit, roleId) = GetRoleIdentity(laserComp);
                if (unit == null || roleId == null) return;

                int id = unit.GetInstanceID();
                if (!_roleCache.TryGetValue(id, out var saved)) return;

                var roleType = roleId.GetType();
                foreach (var f in roleType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType != typeof(bool)) continue;
                    if (saved.TryGetValue(f.Name, out bool orig)) f.SetValue(roleId, orig);
                }
                foreach (var p in roleType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.PropertyType != typeof(bool) || !p.CanWrite) continue;
                    if (saved.TryGetValue("prop_" + p.Name, out bool orig)) p.SetValue(roleId, orig);
                }
                _roleCache.Remove(id);
                _log.LogInfo($"[TLS] Restored roleIdentity for '{GetUnitName(laserComp)}'.");
            }
            catch (Exception ex) { _log.LogWarning($"[TLS] RestoreRoleIdentity failed: {ex.Message}"); }
        }
    } // end Plugin class


    [HarmonyPatch(typeof(Laser), "FixedUpdate")]
    public static class Laser_Injector_Patch
    {
        private static bool _playerTriggered = false;
        private static BepInEx.Logging.ManualLogSource _log =
            BepInEx.Logging.Logger.CreateLogSource("TLS");

        public static void Prefix(Laser __instance)
        {
            try {
                var traverse = Traverse.Create(__instance);
                string uName = Plugin.GetUnitName(__instance);

                // Initialize TLSBeam and capture original damage values
                var db = __instance.GetComponent<TLSBeam>();
                if (db == null)
                {
                    db = __instance.gameObject.AddComponent<TLSBeam>();
                    db.laserInst = __instance;
                    db.dirT = traverse.Field("directionTransform").GetValue<Transform>();
                    db.CaptureOriginals(traverse);
                }

                // Per-unit config check
                bool tlsEnabled = !Plugin.UnitConfigs.TryGetValue(uName, out var entry) || entry.Value;
                if (!tlsEnabled)
                {
                    db.RestoreOriginals(traverse);
                    Plugin.RestoreRoleIdentity(__instance);
                    return;
                }

                // Expand engagement roles so the unit can engage Air + Surface targets
                Plugin.ExpandRoleIdentity(__instance);

                // Keep ammo unlimited so the laser never runs dry
                if (traverse.Field("ammo").FieldExists())
                    traverse.Field("ammo").SetValue(1000);

                // Amplify native first-hit damage values
                if (traverse.Field("fireCommanded").FieldExists() &&
                    traverse.Field("fireCommanded").GetValue<bool>())
                {
                    if (traverse.Field("fireDamage").FieldExists())  traverse.Field("fireDamage").SetValue(1000000f);
                    if (traverse.Field("blastDamage").FieldExists()) traverse.Field("blastDamage").SetValue(1000000f);
                    if (traverse.Field("pierceDamage").FieldExists())traverse.Field("pierceDamage").SetValue(1000000f);
                }

                // Track whether the player is currently firing (used in Postfix gate)
                if (traverse.Field("fireCommanded").FieldExists())
                    _playerTriggered = traverse.Field("fireCommanded").GetValue<bool>();

                // Inject crosshair HUD on player aircraft lasers
                if (__instance is MonoBehaviour mb)
                {
                    var dirT = traverse.Field("directionTransform").GetValue<Transform>();
                    var crosshairComp = mb.gameObject.GetComponent<LaserCrosshairUI>();
                    string rootName = mb.transform.root.name.ToLower();
                    if (crosshairComp == null &&
                        (rootName.Contains("coin") || rootName.Contains("helo") || rootName.Contains("vtol")))
                    {
                        crosshairComp = mb.gameObject.AddComponent<LaserCrosshairUI>();
                        crosshairComp.laser = traverse;
                        crosshairComp.dirT  = dirT;
                    }
                }
            } catch (Exception ex) { BepInEx.Logging.Logger.CreateLogSource("PrefixErr").LogError(ex.ToString()); }
        }

        public static void Postfix(Laser __instance)
        {
            // TLSBeam management moved to Prefix for state capture
        }
    } // end Laser_Injector_Patch

    /// <summary>
    /// TLSBeam — volume-based damage pass using Physics.OverlapCapsuleNonAlloc.
    /// Visual appearance is entirely native (MeshRenderer beam + native hit spark).
    /// This component only handles silent multi-target piercing damage.
    /// </summary>
    public class TLSBeam : MonoBehaviour
    {
        public Transform  dirT;
        public Laser      laserInst;

        private Type          _unitPartType;
        private MethodInfo    _applyDmg;
        private MonoBehaviour _ownerUnit;
        private float         _lastDmgTime;
        private Collider[]    _capsuleBuffer = new Collider[256];

        private float _origFire, _origBlast, _origPierce;
        private bool  _captured = false;

        // Terrain+water only, used to find beam end for the capsule's far cap
        private static readonly int _terrainMask = (1 << 6) | (1 << 4);
        // Everything except IgnoreRaycast, terrain, water
        private static readonly int _damageMask  = ~((1 << 2) | (1 << 6) | (1 << 4));
        private const float BEAM_RADIUS = 0.5f; // must not overlap own aircraft

        private static BepInEx.Logging.ManualLogSource _log =
            BepInEx.Logging.Logger.CreateLogSource("TLSBeam");

        public void CaptureOriginals(Traverse tr)
        {
            if (_captured) return;
            if (tr.Field("fireDamage").FieldExists())   _origFire   = tr.Field("fireDamage").GetValue<float>();
            if (tr.Field("blastDamage").FieldExists())  _origBlast  = tr.Field("blastDamage").GetValue<float>();
            if (tr.Field("pierceDamage").FieldExists()) _origPierce = tr.Field("pierceDamage").GetValue<float>();
            _captured = true;
        }

        public void RestoreOriginals(Traverse tr)
        {
            if (!_captured) return;
            if (tr.Field("fireDamage").FieldExists())   tr.Field("fireDamage").SetValue(_origFire);
            if (tr.Field("blastDamage").FieldExists())  tr.Field("blastDamage").SetValue(_origBlast);
            if (tr.Field("pierceDamage").FieldExists()) tr.Field("pierceDamage").SetValue(_origPierce);
        }

        private void Start()
        {
            _unitPartType = Type.GetType("UnitPart, Assembly-CSharp");
            _applyDmg     = _unitPartType?.GetMethod("ApplyDamage",
                BindingFlags.Public | BindingFlags.Instance);

            // Find the unit this laser belongs to (used to skip self-damage)
            foreach (var c in GetComponentsInParent<MonoBehaviour>())
            {
                if (Plugin.IsUnitComponent(c))
                    { _ownerUnit = c; break; }
            }

            _log.LogInfo($"[TLS] Started on {name}  owner={_ownerUnit?.name ?? "none"} (Resolved: {Plugin.GetUnitName(this)})");
        }

        private void Update()
        {
            if (dirT == null) return;

            bool firing = laserInst != null &&
                          Traverse.Create(laserInst).Field("fireCommanded").GetValue<bool>();
            if (!firing) return;

            // Check if still enabled for this unit
            string uName = Plugin.GetUnitName(this);
            if (Plugin.UnitConfigs.TryGetValue(uName, out var entry))
            {
                if (!entry.Value) return;
            }
            else
            {
                // Config not found for this name - should have been caught by scanner
                // We proceed by default but log once
                if (Time.frameCount % 1000 == 0)
                    _log.LogDebug($"[TLS] Config missing for '{uName}', defaulting to enabled.");
            }

            // Damage at 10 Hz
            if (Time.time - _lastDmgTime < 0.1f) return;
            _lastDmgTime = Time.time;

            Vector3 origin = dirT.position;
            Vector3 dir    = dirT.forward;

            // Find terrain/water to cap the beam length (don't damage beyond ground)
            float beamLen = 15000f;
            if (Physics.Raycast(origin, dir, out RaycastHit terrainHit, 15000f, _terrainMask))
                beamLen = terrainHit.distance;

            // Capsule starts at MinRange m ahead of the muzzle — clears own aircraft
            Vector3 capsuleStart = origin + dir * Plugin.MinRange.Value;
            Vector3 beamEnd      = origin + dir * beamLen;
            int count = Physics.OverlapCapsuleNonAlloc(
                capsuleStart, beamEnd, BEAM_RADIUS, _capsuleBuffer, _damageMask);

            var seen    = new HashSet<int>();
            int pierced = 0;

            for (int i = 0; i < count; i++)
            {
                Collider col = _capsuleBuffer[i];
                if (col == null) continue;
                if (!seen.Add(col.GetInstanceID())) continue;

                // Skip self-owner
                MonoBehaviour hitUnit = null;
                foreach (var c in col.transform.GetComponentsInParent<MonoBehaviour>())
                {
                    string cn = c.GetType().Name;
                    if (cn == "Aircraft" || cn == "Ship" || cn == "GroundVehicle" || cn == "Unit")
                        { hitUnit = c; break; }
                }
                if (hitUnit != null && hitUnit == _ownerUnit) continue;

                // Damage the UnitPart this collider belongs to
                if (_unitPartType != null && _applyDmg != null)
                {
                    Component part = col.GetComponent(_unitPartType)
                                  ?? col.GetComponentInParent(_unitPartType);
                    if (part != null)
                    {
                        try { _applyDmg.Invoke(part, new object[] { 500000f, 0f, 0f, 0f }); pierced++; }
                        catch (Exception ex) { _log.LogError($"[DB] ApplyDamage: {ex.Message}"); }
                        continue;
                    }
                }

                // Fallback: buildings / standalone destructibles
                foreach (var c in col.GetComponentsInParent<MonoBehaviour>())
                {
                    string cn = c.GetType().Name;
                    if (cn == "UnitPart" || cn == "Destructible" || cn == "Building")
                    {
                        var m = c.GetType().GetMethod("ApplyDamage",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (m != null) try { m.Invoke(c, new object[] { 500000f, 0f, 0f, 0f }); pierced++; } catch { }
                        break;
                    }
                }
            }

            if (pierced > 0)
                _log.LogInfo($"[TLS] Pierced {pierced}/{count} colliders  beamLen={beamLen:F0}m (Unit: {Plugin.GetUnitName(this)})");
        }
    }


    [HarmonyPatch(typeof(Laser), "SetTarget")]
    public static class LaserCenterMassPatch
    {
        public static bool Prefix(Laser __instance, Unit target)
        {
            ((Behaviour)(object)__instance).enabled = true;
            var tr = Traverse.Create((object)__instance);
            tr.Field("currentTargetTransform").SetValue(
                (object)((target != null) ? ((Component)(object)target).transform : null));
            tr.Field("currentTarget").SetValue((object)target);
            return false;
        }
    }


    public class LaserCrosshairUI : MonoBehaviour
    {
        public Traverse laser;
        public Transform dirT;
        private Texture2D _crosshairTex;

        private void Start()
        {
            _crosshairTex = new Texture2D(32, 32);
            for (int x=0; x< 32; x++) {
                for (int y=0; y<32; y++) {
                    float dist = Vector2.Distance(new Vector2(x,y), new Vector2(16,16));
                    if (dist > 14 && dist < 16) _crosshairTex.SetPixel(x, y, Color.red);
                    else _crosshairTex.SetPixel(x, y, Color.clear);
                }
            }
            _crosshairTex.Apply();
        }

        private void OnGUI()
        {
            if (Camera.main != null && dirT != null)
            {
                // Check if TLS is enabled for this unit to show crosshair
                string uName = Plugin.GetUnitName(this);
                if (Plugin.UnitConfigs.TryGetValue(uName, out var entry) && !entry.Value)
                    return;

                // Trace forward to see where it hits
                Vector3 targetPoint = dirT.position + (dirT.forward * 15000f);
                if (Physics.Raycast(dirT.position, dirT.forward, out RaycastHit hit, 15000f))
                {
                    targetPoint = hit.point;
                }

                Vector3 screenPos = Camera.main.WorldToScreenPoint(targetPoint);
                if (screenPos.z > 0) // Target is in front of camera
                {
                    float size = 32f;
                    // Y axis is inverted in OnGUI
                    Rect rect = new Rect(screenPos.x - size/2, Screen.height - screenPos.y - size/2, size, size);
                    GUI.DrawTexture(rect, _crosshairTex);
                }
            }
        }
    }
}
