using System;
using System.Collections.Generic;
using System.Text.Json;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes
{
    public class HoloAppearanceData
    {
        public int version { get; set; } = 1;
        public string agent_id { get; set; }
        public int serial { get; set; }
        public float avatar_height { get; set; }
        public string visual_params_b64 { get; set; }
        public string texture_entry_b64 { get; set; }
        public List<HoloWearable> wearables { get; set; } = new();
	public string wearable_cache { get; set; }
    }

    public class HoloWearable
    {
        public int slot { get; set; }
        public string item_id { get; set; }
        public string asset_id { get; set; }
    }

    public static class HoloAppearanceSerializer
    {
        public static string Serialize(ScenePresence sp)
        {
            AvatarAppearance app = sp.Appearance;

            var data = new HoloAppearanceData
            {
                agent_id = sp.UUID.ToString(),
                serial = app.Serial,
                avatar_height = app.AvatarHeight,
                visual_params_b64 = app.VisualParams != null
                    ? Convert.ToBase64String(app.VisualParams)
                    : null,
                texture_entry_b64 = SerializeTextureEntry(app.Texture)
            };

            if (app.Wearables != null)
            {
                for (int slot = 0; slot < app.Wearables.Length; slot++)
                {
                    AvatarWearable wearable = app.Wearables[slot];
                    if (wearable == null)
                        continue;
            
                    for (int idx = 0; idx < wearable.Count; idx++)
                    {
                        WearableItem wi = wearable[idx];
            
                        if (wi.ItemID == UUID.Zero)
                            continue;
            
                        data.wearables.Add(new HoloWearable
                        {
                            slot = slot,
                            item_id = wi.ItemID.ToString(),
                            asset_id = wi.AssetID.ToString()
                        });
                    }
                }
            }

            if (app.WearableCacheItems != null)
            {
                var osd = WearableCacheItem.ToOSD(app.WearableCacheItems, null);
                data.wearable_cache = osd.ToString();
            }

            return JsonSerializer.Serialize(data);
        }

        public static AvatarAppearance Deserialize(string json)
        {
            var data = JsonSerializer.Deserialize<HoloAppearanceData>(json);
            if (data == null)
                return null;
        
            var app = new AvatarAppearance
            {
                Serial = data.serial,
                AvatarHeight = data.avatar_height
            };
        
            if (!string.IsNullOrEmpty(data.visual_params_b64))
                app.VisualParams = Convert.FromBase64String(data.visual_params_b64);
        
            if (!string.IsNullOrEmpty(data.texture_entry_b64))
                app.Texture = DeserializeTextureEntry(data.texture_entry_b64);
        
            // 🔥 Rebuild wearables (this is the important part)
            if (data.wearables != null)
            {
                app.Wearables = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        
                for (int i = 0; i < app.Wearables.Length; i++)
                    app.Wearables[i] = new AvatarWearable();
        
                foreach (var w in data.wearables)
                {
                    if (w.slot < 0 || w.slot >= app.Wearables.Length)
                        continue;
        
                    try
                    {
                        UUID item = UUID.Parse(w.item_id);
                        UUID asset = UUID.Parse(w.asset_id);
        
                        app.Wearables[w.slot].Add(item, asset);
                    }
                    catch
                    {
                        // ignore bad entries
                    }
                }
            }

            WearableCacheItem[] cache = null;

            if (!string.IsNullOrEmpty(data.wearable_cache))
            {
                var osd = OSDParser.DeserializeJson(data.wearable_cache);
                cache = WearableCacheItem.FromOSD(osd, null);
            }

            app.WearableCacheItems = cache;
        
            return app;
        }

        private static string SerializeTextureEntry(Primitive.TextureEntry te)
        {
            if (te == null)
                return null;

            byte[] bytes = te.GetBytes();
            return Convert.ToBase64String(bytes);
        }

        private static Primitive.TextureEntry DeserializeTextureEntry(string b64)
        {
            byte[] bytes = Convert.FromBase64String(b64);
            return new Primitive.TextureEntry(bytes, 0, bytes.Length);
        }
    }
}

