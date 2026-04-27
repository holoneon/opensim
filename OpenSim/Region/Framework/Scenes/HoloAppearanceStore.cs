using System;
using System.Linq;
using System.Text;
using StackExchange.Redis;
using Nini.Config;

namespace OpenSim.Region.Framework.Scenes
{
    public static class HoloAppearanceStore
    {

	private static bool m_initialized = false;

        private static ConnectionMultiplexer m_local;
        private static IDatabase m_db;

        private static ConnectionMultiplexer[] m_peers;
        private static IDatabase[] m_peerDbs;

        private static int TTLSeconds = 600;

        private static void EnsureInit()
        {
            if (m_initialized)
                return;
        
            // fallback safe defaults
            m_local = ConnectionMultiplexer.Connect("127.0.0.1:6379");
            m_db = m_local.GetDatabase();
        
            m_peerDbs = Array.Empty<IDatabase>();
        
            m_initialized = true;
        }

        public static void Configure(IConfigSource config)
        {
	    if (m_initialized)
		return;

	    var sconfig = config.Configs["HoloAppearance"];
            if (sconfig != null)
            {

            	string localHost = sconfig.GetString("LocalValkeyHost", "127.0.0.1");
            	int localPort = sconfig.GetInt("LocalValkeyPort", 6379);
            	TTLSeconds = sconfig.GetInt("TTLSeconds", 600);

                // local
                m_local = ConnectionMultiplexer.Connect($"{localHost}:{localPort}");
                m_db = m_local.GetDatabase();

                // peers
                string peers = sconfig.GetString("Peers", "");
                var peerList = peers.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(p => p.Trim())
                                .Where(p => p.Length > 0)
                                .ToArray();


                m_peers = peerList.Select(p =>
                {
                    try
                    {
                        var opts = ConfigurationOptions.Parse(p);
                        opts.AbortOnConnectFail = false;
                        opts.ConnectTimeout = 1000;
                        opts.SyncTimeout = 1000;
                        opts.KeepAlive = 30;
                
                        return ConnectionMultiplexer.Connect(opts);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(p => p != null)
                .ToArray();
                
                m_peerDbs = m_peers
                    .Select(p => p.GetDatabase())
                    .ToArray();

	        m_initialized = true;

             } else {
		EnsureInit();
             }
        }

        private static string Key(string cid) => $"holo:appearance:{cid}";

        // WRITE (local only)
        public static (string cid, string sha256) PutAppearance(string json)
        {
	    EnsureInit();
            string cid = ComputeCID(json);
            string key = Key(cid);

            m_db.StringSet(key, json, TimeSpan.FromSeconds(TTLSeconds));

            return (cid, ComputeSHA256(json));
        }

        // READ (local → peers)
        public static string GetAppearance(string cid)
        {
	    EnsureInit();
            string key = Key(cid);

            // 1. local hit
            var val = m_db.StringGet(key);
            if (val.HasValue)
                return val;

            // 2. peer fallback
            foreach (var peer in m_peerDbs)
            {
                try
                {
                    val = peer.StringGet(key);
                    if (val.HasValue)
                    {
                        // warm local cache
                        m_db.StringSet(key, val, TimeSpan.FromSeconds(TTLSeconds));
                        return val;
                    }
                }
                catch { }
            }

            return null;
        }

        // Simple CID (you can replace with real IPFS later)
        private static string ComputeCID(string json)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
            return Convert.ToHexString(hash).ToLower();
        }

        private static string ComputeSHA256(string json)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
            return Convert.ToHexString(hash).ToLower();
        }
    }
}
