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
    }


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
                if (Plugin.UnitConfigs.TryGetValue(uName, out var entry) && !entry.Value)
                {
                    db.RestoreOriginals(traverse);
                    return;
                }

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
