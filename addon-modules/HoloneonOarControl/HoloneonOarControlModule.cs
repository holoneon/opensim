using System;
using System.Collections.Generic;

using log4net;
using Nini.Config;
using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace Holoneon.OarControl
{
    public class HoloneonOarControlModule : INonSharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(typeof(HoloneonOarControlModule));

        private Scene m_scene;
        private bool m_enabled;
        private int m_commandChannel = 77;
        private UUID m_adminUuid = UUID.Zero;

        public string Name
        {
            get { return "HoloneonOarControlModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["HoloneonOarControl"];
            if (config == null)
                return;

            m_enabled = config.GetBoolean("Enabled", false);
            m_commandChannel = config.GetInt("CommandChannel", 77);

            string adminUuid = config.GetString("AdminUUID", string.Empty);
            if (!string.IsNullOrWhiteSpace(adminUuid))
                UUID.TryParse(adminUuid, out m_adminUuid);

            if (m_enabled)
            {
                m_log.InfoFormat(
                    "[HOLONEON OAR CONTROL]: Enabled on chat channel {0}",
                    m_commandChannel
                );
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;
            m_scene.EventManager.OnChatFromClient += OnChatFromClient;

            m_log.InfoFormat(
                "[HOLONEON OAR CONTROL]: Added to region {0}",
                scene.RegionInfo.RegionName
            );
        }

        public void RemoveRegion(Scene scene)
        {
            if (m_scene != null)
                m_scene.EventManager.OnChatFromClient -= OnChatFromClient;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        private void OnChatFromClient(object sender, OSChatMessage chat)
        {
            if (chat == null)
                return;

            if (chat.Channel != m_commandChannel)
                return;

            string message = (chat.Message ?? string.Empty).Trim();

            if (!message.Equals("oar", StringComparison.OrdinalIgnoreCase))
                return;

            UUID avatarId = chat.SenderUUID;

            if (IsPrivileged(avatarId))
            {
                SendMessageToAvatar(
                    avatarId,
                    "OAR tools: Export Region, Import Region, Export My Stuff, Export User Stuff, Restore User Stuff."
                );
            }
            else
            {
                SendMessageToAvatar(
                    avatarId,
                    "OAR tools: Export My Stuff."
                );
            }
        }

        private bool IsPrivileged(UUID avatarId)
        {
            if (avatarId == UUID.Zero)
                return false;

            if (m_adminUuid != UUID.Zero && avatarId == m_adminUuid)
                return true;

            if (m_scene == null || m_scene.RegionInfo == null)
                return false;

            EstateSettings estate = m_scene.RegionInfo.EstateSettings;
            if (estate == null)
                return false;

            if (estate.EstateOwner == avatarId)
                return true;

            UUID[] managers = estate.EstateManagers;
            if (managers != null)
            {
                foreach (UUID managerId in managers)
                {
                        if (managerId == avatarId)
                        return true;
                }
            }

            return false;
        }

        private void SendMessageToAvatar(UUID avatarId, string message)
        {
            if (m_scene == null)
                return;

            m_scene.SimChat(
                message,
                ChatTypeEnum.Region,
                m_commandChannel,
                Vector3.Zero,
                Name,
                UUID.Zero,
                false
            );
        }
    }
}

