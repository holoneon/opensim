// HoloPhysicsGuard.cs
//
// Region safety module for OpenSimulator.
// Sleeps physical root objects when a region is empty, records them in a
// separate table, and wakes them when an avatar enters.
//
// Sleep:
//   - insert row into holo_physics_guard_sleep
//   - clear physical bit in prims.ObjectFlags
//   - clear physical bit in live SceneObjectPart
//   - ApplyPhysics()
//
// Wake:
//   - set physical bit in prims.ObjectFlags
//   - set physical bit in live SceneObjectPart
//   - ApplyPhysics()
//   - delete guard table row
//
// This keeps "sleep" reversible.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Timers;

using log4net;
using Nini.Config;
using Mono.Addins;
using MySql.Data.MySqlClient;

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;

[assembly: Addin("HoloPhysicsGuard", "0.4")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace HoloNeon.RegionModules
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "HoloPhysicsGuard")]
    public class HoloPhysicsGuard : INonSharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly List<Scene> m_scenes = new List<Scene>();

        private bool m_enabled = false;
        private bool m_dryRun = true;
        private bool m_sleepWhenEmpty = true;
        private bool m_zeroVelocities = true;
        private bool m_verbose = true;
        private bool m_wakeOnStart = false;
        private bool m_wakeOnAvatarEnter = true;
        private bool m_autoCreateTable = true;

        private int m_checkIntervalSeconds = 30;
        private int m_emptyDelaySeconds = 60;

        private string m_mode = "PersistSleep";
        private string m_connectionString = "";

        private string[] m_alwaysSleepNameContains = Array.Empty<string>();
        private string[] m_neverSleepNameContains = Array.Empty<string>();

        private Timer m_timer;

        private readonly Dictionary<UUID, DateTime> m_regionBecameEmptyAt =
            new Dictionary<UUID, DateTime>();

        public string Name
        {
            get { return "HoloPhysicsGuard"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
            m_log.Info("[HOLO PHYSICS GUARD]: Initialise called");

            IConfig config = source.Configs["HoloPhysicsGuard"];
            if (config == null)
            {
                m_log.Warn("[HOLO PHYSICS GUARD]: No [HoloPhysicsGuard] config section found; module disabled");
                return;
            }

            m_enabled = config.GetBoolean("Enabled", false);
            if (!m_enabled)
            {
                m_log.Warn("[HOLO PHYSICS GUARD]: Config found but Enabled=false; module disabled");
                return;
            }

            m_dryRun = config.GetBoolean("DryRun", true);
            m_sleepWhenEmpty = config.GetBoolean("SleepWhenEmpty", true);
            m_zeroVelocities = config.GetBoolean("ZeroVelocities", true);
            m_verbose = config.GetBoolean("Verbose", true);

            m_wakeOnStart = config.GetBoolean("WakeOnStart", false);
            m_wakeOnAvatarEnter = config.GetBoolean("WakeOnAvatarEnter", true);
            m_autoCreateTable = config.GetBoolean("AutoCreateTable", true);

            m_checkIntervalSeconds = config.GetInt("CheckIntervalSeconds", 30);
            m_emptyDelaySeconds = config.GetInt("EmptyDelaySeconds", 60);

            m_mode = config.GetString("Mode", "PersistSleep").Trim();
            m_connectionString = config.GetString("ConnectionString", "").Trim();

            m_alwaysSleepNameContains = SplitList(
                config.GetString("AlwaysSleepNameContains", "")
            );

            m_neverSleepNameContains = SplitList(
                config.GetString("NeverSleepNameContains", "")
            );

            if (m_dryRun)
                m_mode = "ReportOnly";

            if (IsPersistMode() && String.IsNullOrWhiteSpace(m_connectionString))
            {
                m_log.Error("[HOLO PHYSICS GUARD]: Mode=PersistSleep requires ConnectionString");
                m_enabled = false;
                return;
            }

            if (IsPersistMode() && m_autoCreateTable)
            {
                try
                {
                    EnsureTable();
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat("[HOLO PHYSICS GUARD]: Failed creating/checking table: {0}", ex);
                    m_enabled = false;
                    return;
                }
            }

            m_timer = new Timer(Math.Max(5, m_checkIntervalSeconds) * 1000);
            m_timer.Elapsed += OnTimer;
            m_timer.AutoReset = true;
            m_timer.Start();

            m_log.InfoFormat(
                "[HOLO PHYSICS GUARD]: Enabled. Mode={0}, DryRun={1}, WakeOnStart={2}, WakeOnAvatarEnter={3}, CheckInterval={4}s, EmptyDelay={5}s",
                m_mode,
                m_dryRun,
                m_wakeOnStart,
                m_wakeOnAvatarEnter,
                m_checkIntervalSeconds,
                m_emptyDelaySeconds
            );
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_scenes)
                m_scenes.Add(scene);

            m_log.InfoFormat(
                "[HOLO PHYSICS GUARD]: Added region {0} ({1})",
                scene.RegionInfo.RegionName,
                scene.RegionInfo.RegionID
            );
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            if (m_wakeOnStart && IsPersistMode())
            {
                try
                {
                    WakeRegion(scene, "region_start");
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat(
                        "[HOLO PHYSICS GUARD]: WakeOnStart failed for region {0}: {1}",
                        scene.RegionInfo.RegionName,
                        ex
                    );
                }
            }
        }

        public void RemoveRegion(Scene scene)
        {
            lock (m_scenes)
                m_scenes.Remove(scene);

            lock (m_regionBecameEmptyAt)
                m_regionBecameEmptyAt.Remove(scene.RegionInfo.RegionID);
        }

        public void Close()
        {
            if (m_timer != null)
            {
                m_timer.Stop();
                m_timer.Elapsed -= OnTimer;
                m_timer.Dispose();
                m_timer = null;
            }

            lock (m_scenes)
                m_scenes.Clear();

            lock (m_regionBecameEmptyAt)
                m_regionBecameEmptyAt.Clear();
        }

        private void OnTimer(object sender, ElapsedEventArgs e)
        {
            if (!m_enabled)
                return;

            List<Scene> scenes;

            lock (m_scenes)
                scenes = new List<Scene>(m_scenes);

            foreach (Scene scene in scenes)
            {
                try
                {
                    CheckScene(scene);
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat(
                        "[HOLO PHYSICS GUARD]: Error checking region {0}: {1}",
                        scene.RegionInfo.RegionName,
                        ex
                    );
                }
            }
        }

        private void CheckScene(Scene scene)
        {
            int rootAgents = scene.GetRootAgentCount();
            UUID regionID = scene.RegionInfo.RegionID;

            if (rootAgents > 0)
            {
                lock (m_regionBecameEmptyAt)
                    m_regionBecameEmptyAt.Remove(regionID);

                if (m_wakeOnAvatarEnter && IsPersistMode())
                    WakeRegion(scene, "avatar_enter");

                return;
            }

            if (!m_sleepWhenEmpty)
                return;

            DateTime emptySince;

            lock (m_regionBecameEmptyAt)
            {
                if (!m_regionBecameEmptyAt.TryGetValue(regionID, out emptySince))
                {
                    emptySince = DateTime.UtcNow;
                    m_regionBecameEmptyAt[regionID] = emptySince;

                    if (m_verbose)
                    {
                        m_log.InfoFormat(
                            "[HOLO PHYSICS GUARD]: Region {0} became empty",
                            scene.RegionInfo.RegionName
                        );
                    }

                    return;
                }
            }

            double emptySeconds = (DateTime.UtcNow - emptySince).TotalSeconds;

            if (emptySeconds < m_emptyDelaySeconds)
                return;

            SleepPhysicalObjects(scene);
        }

        private void SleepPhysicalObjects(Scene scene)
        {
            int scanned = 0;
            int physical = 0;
            int slept = 0;
            int skipped = 0;

            EntityBase[] entities = scene.GetEntities();

            foreach (EntityBase entity in entities)
            {
                SceneObjectGroup sog = entity as SceneObjectGroup;
                if (sog == null || sog.RootPart == null)
                    continue;

                SceneObjectPart root = sog.RootPart;
                scanned++;

                if (!IsPhysical(root))
                    continue;

                physical++;

                string name = root.Name ?? "";

                if (!ShouldSleep(name))
                {
                    skipped++;

                    if (m_verbose)
                    {
                        m_log.InfoFormat(
                            "[HOLO PHYSICS GUARD]: Skip physical object region={0} name='{1}' uuid={2}",
                            scene.RegionInfo.RegionName,
                            name,
                            root.UUID
                        );
                    }

                    continue;
                }

                if (IsReportOnly())
                {
                    slept++;

                    m_log.WarnFormat(
                        "[HOLO PHYSICS GUARD]: REPORT would sleep physical object region={0} name='{1}' uuid={2} pos={3}",
                        scene.RegionInfo.RegionName,
                        name,
                        root.UUID,
                        root.GroupPosition
                    );

                    continue;
                }

                try
                {
                    if (IsPersistMode())
                        RecordSleepInDb(scene, sog, root);

                    SetNonPhysicalLive(sog, root);

                    if (m_zeroVelocities)
                        ZeroMotion(root);

                    sog.HasGroupChanged = true;
                    sog.ScheduleGroupForFullUpdate();

                    slept++;

                    m_log.WarnFormat(
                        "[HOLO PHYSICS GUARD]: Slept physical object region={0} name='{1}' uuid={2} pos={3}",
                        scene.RegionInfo.RegionName,
                        name,
                        root.UUID,
                        root.GroupPosition
                    );
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat(
                        "[HOLO PHYSICS GUARD]: Failed sleeping object region={0} name='{1}' uuid={2}: {3}",
                        scene.RegionInfo.RegionName,
                        name,
                        root.UUID,
                        ex
                    );
                }
            }

            if (physical > 0 || m_verbose)
            {
                m_log.InfoFormat(
                    "[HOLO PHYSICS GUARD]: Region {0}: scanned={1} physical={2} slept={3} skipped={4} mode={5}",
                    scene.RegionInfo.RegionName,
                    scanned,
                    physical,
                    slept,
                    skipped,
                    m_mode
                );
            }
        }

        private void WakeRegion(Scene scene, string reason)
        {
            List<SleepRow> rows = GetSleepRows(scene.RegionInfo.RegionID);

            if (rows.Count == 0)
                return;

            int woke = 0;
            int stale = 0;
            int failed = 0;

            foreach (SleepRow row in rows)
            {
                SceneObjectGroup sog = FindSceneObjectGroup(scene, row.ObjectUUID);

                if (sog == null || sog.RootPart == null)
                {
                    DeleteSleepRow(row.RegionUUID, row.ObjectUUID);
                    stale++;

                    m_log.WarnFormat(
                        "[HOLO PHYSICS GUARD]: Removed stale sleep row region={0} object={1} name='{2}' reason={3}",
                        scene.RegionInfo.RegionName,
                        row.ObjectUUID,
                        row.ObjectName,
                        reason
                    );

                    continue;
                }

                try
                {
                    WakeObjectInDb(row.RegionUUID, row.ObjectUUID);
                    SetPhysicalLive(sog, sog.RootPart);

                    sog.HasGroupChanged = true;
                    sog.ScheduleGroupForFullUpdate();

                    DeleteSleepRow(row.RegionUUID, row.ObjectUUID);

                    woke++;

                    m_log.WarnFormat(
                        "[HOLO PHYSICS GUARD]: Woke object region={0} name='{1}' uuid={2} reason={3}",
                        scene.RegionInfo.RegionName,
                        sog.RootPart.Name,
                        sog.RootPart.UUID,
                        reason
                    );
                }
                catch (Exception ex)
                {
                    failed++;

                    m_log.ErrorFormat(
                        "[HOLO PHYSICS GUARD]: Failed waking object region={0} object={1} reason={2}: {3}",
                        scene.RegionInfo.RegionName,
                        row.ObjectUUID,
                        reason,
                        ex
                    );
                }
            }

            if (woke > 0 || stale > 0 || failed > 0 || m_verbose)
            {
                m_log.InfoFormat(
                    "[HOLO PHYSICS GUARD]: Wake region {0}: rows={1} woke={2} stale={3} failed={4} reason={5}",
                    scene.RegionInfo.RegionName,
                    rows.Count,
                    woke,
                    stale,
                    failed,
                    reason
                );
            }
        }

        private SceneObjectGroup FindSceneObjectGroup(Scene scene, UUID rootPartUUID)
        {
            EntityBase[] entities = scene.GetEntities();

            foreach (EntityBase entity in entities)
            {
                SceneObjectGroup sog = entity as SceneObjectGroup;
                if (sog == null || sog.RootPart == null)
                    continue;

                if (sog.RootPart.UUID == rootPartUUID)
                    return sog;
            }

            return null;
        }

        private bool IsPhysical(SceneObjectPart part)
        {
            return (part.Flags & PrimFlags.Physics) != 0;
        }

        private void SetNonPhysicalLive(SceneObjectGroup sog, SceneObjectPart part)
        {
            bool setTemporary = (part.Flags & PrimFlags.TemporaryOnRez) != 0;
            bool setPhantom = (part.Flags & PrimFlags.Phantom) != 0;
            bool setVolumeDetect = part.VolumeDetectActive;
        
            // Proper OpenSim path for changing physical state.
            part.UpdatePrimFlags(
                false,              // UsePhysics
                setTemporary,
                setPhantom,
                setVolumeDetect,
                false               // building
            );
        }

        private void SetPhysicalLive(SceneObjectGroup sog, SceneObjectPart part)
        {
            bool setTemporary = (part.Flags & PrimFlags.TemporaryOnRez) != 0;
            bool setPhantom = (part.Flags & PrimFlags.Phantom) != 0;
            bool setVolumeDetect = part.VolumeDetectActive;
        
            part.UpdatePrimFlags(
                true,               // UsePhysics
                setTemporary,
                setPhantom,
                setVolumeDetect,
                false               // building
            );
        }

        private void ZeroMotion(SceneObjectPart part)
        {
            part.Velocity = Vector3.Zero;
            part.AngularVelocity = Vector3.Zero;
        }

        private bool ShouldSleep(string name)
        {
            string n = name.ToLowerInvariant();

            foreach (string s in m_neverSleepNameContains)
            {
                if (s.Length > 0 && n.Contains(s))
                    return false;
            }

            if (m_alwaysSleepNameContains.Length == 0)
                return true;

            foreach (string s in m_alwaysSleepNameContains)
            {
                if (s.Length > 0 && n.Contains(s))
                    return true;
            }

            return false;
        }

        private bool IsReportOnly()
        {
            return String.Equals(m_mode, "ReportOnly", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPersistMode()
        {
            return String.Equals(m_mode, "PersistSleep", StringComparison.OrdinalIgnoreCase);
        }

        private MySqlConnection OpenDb()
        {
            MySqlConnection conn = new MySqlConnection(m_connectionString);
            conn.Open();
            return conn;
        }

        private void EnsureTable()
        {
            string sql = @"
CREATE TABLE IF NOT EXISTS holo_physics_guard_sleep (
    region_uuid CHAR(36) NOT NULL,
    object_uuid CHAR(36) NOT NULL,
    scene_group_id CHAR(36) NOT NULL,
    object_name VARCHAR(255) NOT NULL DEFAULT '',
    original_object_flags BIGINT UNSIGNED NOT NULL DEFAULT 0,
    slept_at INT UNSIGNED NOT NULL,
    slept_by VARCHAR(64) NOT NULL DEFAULT 'HoloPhysicsGuard',

    PRIMARY KEY (region_uuid, object_uuid),
    KEY idx_region_uuid (region_uuid),
    KEY idx_scene_group_id (scene_group_id),
    KEY idx_slept_at (slept_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

            using (MySqlConnection conn = OpenDb())
            using (MySqlCommand cmd = new MySqlCommand(sql, conn))
            {
                cmd.ExecuteNonQuery();
            }

            m_log.Info("[HOLO PHYSICS GUARD]: Table holo_physics_guard_sleep ready");
        }

        private void RecordSleepInDb(Scene scene, SceneObjectGroup sog, SceneObjectPart root)
        {
            string regionUUID = scene.RegionInfo.RegionID.ToString();
            string objectUUID = root.UUID.ToString();
            string sceneGroupID = sog.UUID.ToString();
            string objectName = root.Name ?? "";
            ulong originalFlags = Convert.ToUInt64(root.Flags);
            uint now = UnixTimeNow();

            using (MySqlConnection conn = OpenDb())
            using (MySqlTransaction tx = conn.BeginTransaction())
            {
                string insertSql = @"
INSERT INTO holo_physics_guard_sleep
    (region_uuid, object_uuid, scene_group_id, object_name, original_object_flags, slept_at, slept_by)
VALUES
    (@region_uuid, @object_uuid, @scene_group_id, @object_name, @original_object_flags, @slept_at, 'HoloPhysicsGuard')
ON DUPLICATE KEY UPDATE
    scene_group_id = VALUES(scene_group_id),
    object_name = VALUES(object_name),
    original_object_flags = VALUES(original_object_flags),
    slept_at = VALUES(slept_at),
    slept_by = VALUES(slept_by);";

                using (MySqlCommand cmd = new MySqlCommand(insertSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@region_uuid", regionUUID);
                    cmd.Parameters.AddWithValue("@object_uuid", objectUUID);
                    cmd.Parameters.AddWithValue("@scene_group_id", sceneGroupID);
                    cmd.Parameters.AddWithValue("@object_name", Truncate(objectName, 255));
                    cmd.Parameters.AddWithValue("@original_object_flags", originalFlags);
                    cmd.Parameters.AddWithValue("@slept_at", now);
                    cmd.ExecuteNonQuery();
                }

                string updateSql = @"
UPDATE prims
SET ObjectFlags = ObjectFlags - 1,
    VelocityX = 0,
    VelocityY = 0,
    VelocityZ = 0,
    AngularVelocityX = 0,
    AngularVelocityY = 0,
    AngularVelocityZ = 0
WHERE UUID = @object_uuid
  AND (ObjectFlags & 1) != 0;";

                using (MySqlCommand cmd = new MySqlCommand(updateSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@object_uuid", objectUUID);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
        }

        private List<SleepRow> GetSleepRows(UUID regionID)
        {
            List<SleepRow> rows = new List<SleepRow>();

            string sql = @"
SELECT region_uuid, object_uuid, scene_group_id, object_name, original_object_flags
FROM holo_physics_guard_sleep
WHERE region_uuid = @region_uuid
ORDER BY slept_at ASC;";

            using (MySqlConnection conn = OpenDb())
            using (MySqlCommand cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@region_uuid", regionID.ToString());

                using (MySqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        SleepRow row = new SleepRow();
                        row.RegionUUID = UUID.Parse(reader.GetString("region_uuid"));
                        row.ObjectUUID = UUID.Parse(reader.GetString("object_uuid"));
                        row.SceneGroupID = UUID.Parse(reader.GetString("scene_group_id"));
                        row.ObjectName = reader.GetString("object_name");
                        row.OriginalObjectFlags = Convert.ToUInt64(reader["original_object_flags"]);
                        rows.Add(row);
                    }
                }
            }

            return rows;
        }

        private void WakeObjectInDb(UUID regionUUID, UUID objectUUID)
        {
            string sql = @"
UPDATE prims
SET ObjectFlags = ObjectFlags | 1
WHERE UUID = @object_uuid;";

            using (MySqlConnection conn = OpenDb())
            using (MySqlCommand cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@object_uuid", objectUUID.ToString());
                cmd.ExecuteNonQuery();
            }
        }

        private void DeleteSleepRow(UUID regionUUID, UUID objectUUID)
        {
            string sql = @"
DELETE FROM holo_physics_guard_sleep
WHERE region_uuid = @region_uuid
  AND object_uuid = @object_uuid;";

            using (MySqlConnection conn = OpenDb())
            using (MySqlCommand cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@region_uuid", regionUUID.ToString());
                cmd.Parameters.AddWithValue("@object_uuid", objectUUID.ToString());
                cmd.ExecuteNonQuery();
            }
        }

        private static uint UnixTimeNow()
        {
            return (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static string Truncate(string value, int maxLen)
        {
            if (String.IsNullOrEmpty(value))
                return "";

            if (value.Length <= maxLen)
                return value;

            return value.Substring(0, maxLen);
        }

        private static string[] SplitList(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return Array.Empty<string>();

            string[] raw = value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> result = new List<string>();

            foreach (string item in raw)
            {
                string s = item.Trim().ToLowerInvariant();
                if (s.Length > 0)
                    result.Add(s);
            }

            return result.ToArray();
        }

        private class SleepRow
        {
            public UUID RegionUUID;
            public UUID ObjectUUID;
            public UUID SceneGroupID;
            public string ObjectName;
            public ulong OriginalObjectFlags;
        }
    }
}
