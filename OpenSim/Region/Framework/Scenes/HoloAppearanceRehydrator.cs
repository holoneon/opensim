using System;
using System.Threading;
using OpenMetaverse;
using OpenSim.Framework;
using log4net;

namespace OpenSim.Region.Framework.Scenes
{
    public static class HoloAppearanceRehydrator
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(typeof(HoloAppearanceRehydrator));

        public static void TryRehydrateAfterTeleport(ScenePresence sp)
        {
	    //m_log.Info("[HOLO Rehydrate] Rehydrating...");

            if (sp == null || sp.IsDeleted)
                return;

            if (string.IsNullOrEmpty(sp.HoloAppearanceCid))
                return;

            try
            {

		//fetch

//		m_log.Info("[HOLO Rehydrate] fetching appearance json...");

		string json = HoloAppearanceStore.GetAppearance(sp.HoloAppearanceCid);

//		m_log.InfoFormat("[HOLO Rehydrate]: json:{0}", json);

                if (string.IsNullOrEmpty(json))
                    return;

                AvatarAppearance full = HoloAppearanceSerializer.Deserialize(json);
                if (full == null)
                    return;

/*
		m_log.InfoFormat("[HOLO Rehydrate]: deserialized height={0}, wearables={1}",
		    full.AvatarHeight,
		    full.Wearables == null ? -1 : full.Wearables.Length);
*/

                sp.Appearance = full;
		sp.Appearance.Serial = Util.UnixTimeSinceEpoch();

                if (sp.Scene.AvatarFactory != null)
                {

		    Thread.Sleep(200);

                    sp.Scene.AvatarFactory.SetAppearance(
                        sp,
                        full.Texture,
                        full.VisualParams,
                        full.WearableCacheItems
                    );
                }
                
                // Attachments
                if (sp.Scene.AttachmentsModule != null)
                    sp.Scene.AttachmentsModule.RezAttachments(sp);
            }
            catch (Exception e)
            {
                m_log.Warn($"[HOLO]: Rehydrate failed {e}");
            }
        }
    }
}

