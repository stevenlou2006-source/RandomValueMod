using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using RandomValueLib;
using UnityEngine;
using ExitGames.Client.Photon;

namespace RandomValuablePrice
{
	public static class RandomValueAPI
	{
		public static event Action<ValuableObject, float, float> OnItemRandomized;
		internal static void TriggerItemRandomized(ValuableObject vo, float oldPrice, float newPrice)
		{
			OnItemRandomized?.Invoke(vo, oldPrice, newPrice);
		}
	}

	[Serializable]
	public class RandomValueConfigSnapshot
	{
		public float minMultiplier, maxMultiplier;
		public bool enableBlacklist, enableWhitelist, excludeHigh, cashBag;
		public string blacklist, whitelist;
		public float highThreshold;
		public int levelSeed;
		public long revision;
	}

	[Serializable]
	public class PriceSyncData
	{
		public List<PriceEntry> prices = new List<PriceEntry>();
	}

	[Serializable]
	public class PriceEntry
	{
		public int viewId;
		public float price;
	}

	public class RandomValueRpcRelay : MonoBehaviourPunCallbacks
	{
		private static RandomValueRpcRelay _instance;
		public static RandomValueRpcRelay Instance
		{
			get
			{
				if (_instance == null)
				{
					var punManager = UnityEngine.Object.FindObjectOfType<PunManager>();
					if (punManager != null)
					{
						_instance = punManager.GetComponent<RandomValueRpcRelay>();
						if (_instance == null)
							_instance = punManager.gameObject.AddComponent<RandomValueRpcRelay>();
					}
				}
				return _instance;
			}
		}

		private void Awake()
		{
			if (_instance != null && _instance != this)
			{
				Destroy(this);
				return;
			}
			_instance = this;
			if (GetComponent<PhotonView>() == null)
				gameObject.AddComponent<PhotonView>();
		}

		private System.Collections.IEnumerator PublishConfigDelayed()
		{
			yield return new WaitForSeconds(1f);
			if (PhotonNetwork.IsMasterClient && PhotonNetwork.InRoom)
			{
				RandomValueMod.CurrentLevelSeed = GetLevelSeed();
				PublishConfigSnapshot(RandomValueMod.CurrentConfig, RandomValueMod.CurrentLevelSeed);
				RandomValueMod.StaticLogger.LogInfo("[Config] 延迟发布配置快照完成");
			}
		}

		[PunRPC]
		public void RequestItemPriceRPC(int viewId, int requesterActorNumber)
		{
			if (!PhotonNetwork.IsMasterClient) return;
			var valuable = FindValuableByViewId(viewId);
			if (valuable == null) return;

			float oldPrice = RandomValueService.GetItemPrice(valuable);
			float newPrice = RandomValueService.ApplyRandomPrice(valuable, RandomValueMod.CurrentConfig, RandomValueMod.CurrentLevelSeed);

			if (RandomValueMod.EnableVerboseLogging.Value)
			{
				string playerName = GetPlayerNameByActorNumber(requesterActorNumber);
				string itemName = CleanItemName(valuable.name);
				float multiplier = oldPrice > 0 ? newPrice / oldPrice : 0f;
				RandomValueMod.StaticLogger.LogInfo($"[Host] {playerName} 抓取 {itemName} | {oldPrice:F0} → {newPrice:F0} | 倍率: {multiplier:F2}x");
			}

			if (Math.Abs(newPrice - oldPrice) > 0.01f)
			{
				RandomValueMod.priceCache[viewId] = newPrice;
				photonView.RPC(nameof(SetItemPriceRPC), RpcTarget.All, viewId, newPrice, oldPrice);
			}
		}

		[PunRPC]
		public void SetItemPriceRPC(int viewId, float newPrice, float oldPrice)
		{
			var valuable = FindValuableByViewId(viewId);
			if (valuable == null) return;

			RandomValueService.SetItemPrice(valuable, newPrice);
			RandomValueAPI.TriggerItemRandomized(valuable, oldPrice, newPrice);
			RandomValueMod.modifiedItemIds.Add(viewId);
			RandomValueMod.priceCache[viewId] = newPrice;

			if (RandomValueMod.EnableVerboseLogging.Value)
			{
				string itemName = CleanItemName(valuable.name);
				float multiplier = oldPrice > 0 ? newPrice / oldPrice : 0f;
				RandomValueMod.StaticLogger.LogInfo($"[Sync] {itemName} | {oldPrice:F0} → {newPrice:F0} | 倍率: {multiplier:F2}x");
			}
		}

		[PunRPC]
		public void SyncAllPricesRPC(int targetActor, string priceDataJson)
		{
			if (PhotonNetwork.LocalPlayer.ActorNumber != targetActor) return;
			var priceData = JsonUtility.FromJson<PriceSyncData>(priceDataJson);
			if (priceData == null) return;
			foreach (var entry in priceData.prices)
			{
				var valuable = FindValuableByViewId(entry.viewId);
				if (valuable != null)
				{
					RandomValueService.SetItemPrice(valuable, entry.price);
					RandomValueMod.modifiedItemIds.Add(entry.viewId);
					RandomValueMod.priceCache[entry.viewId] = entry.price;
				}
			}
			if (RandomValueMod.EnableVerboseLogging.Value)
				RandomValueMod.StaticLogger.LogInfo($"[Sync] 新玩家补发 {priceData.prices.Count} 个物品价格");
		}

		public override void OnPlayerEnteredRoom(Player newPlayer)
		{
			if (!PhotonNetwork.IsMasterClient) return;
			var data = new PriceSyncData();
			foreach (var kvp in RandomValueMod.priceCache)
				data.prices.Add(new PriceEntry { viewId = kvp.Key, price = kvp.Value });
			string json = JsonUtility.ToJson(data);
			photonView.RPC(nameof(SyncAllPricesRPC), newPlayer, newPlayer.ActorNumber, json);
		}

		public void PublishConfigSnapshot(RandomizationConfig config, int levelSeed)
		{
			if (!PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom) return;
			var snapshot = new RandomValueConfigSnapshot
			{
				minMultiplier = config.MinMultiplier,
				maxMultiplier = config.MaxMultiplier,
				enableBlacklist = config.EnableBlacklist,
				blacklist = string.Join(",", config.BlacklistKeywords),
				enableWhitelist = config.EnableWhitelist,
				whitelist = string.Join(",", config.WhitelistKeywords),
				excludeHigh = config.ExcludeHighValueItems,
				highThreshold = config.HighValueThreshold,
				cashBag = config.RandomizeCashBags,
				levelSeed = levelSeed,
				revision = DateTime.UtcNow.Ticks
			};
			string json = JsonUtility.ToJson(snapshot);
			var props = new Hashtable { ["rv_config"] = json };
			PhotonNetwork.CurrentRoom.SetCustomProperties(props);
		}

		public override void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged)
		{
			if (propertiesThatChanged.ContainsKey("rv_config"))
			{
				string json = propertiesThatChanged["rv_config"] as string;
				if (!string.IsNullOrEmpty(json))
				{
					var snapshot = JsonUtility.FromJson<RandomValueConfigSnapshot>(json);
					if (snapshot != null)
						ApplySnapshot(snapshot);
				}
			}
		}

		public override void OnJoinedRoom()
		{
			RandomValueMod.ClearCache();
			StartCoroutine(PublishConfigDelayed());
		}

		private void ApplySnapshot(RandomValueConfigSnapshot snapshot)
		{
			RandomValueMod.MinValueMultiplier.Value = snapshot.minMultiplier;
			RandomValueMod.MaxValueMultiplier.Value = snapshot.maxMultiplier;
			RandomValueMod.EnableBlacklistMode.Value = snapshot.enableBlacklist;
			RandomValueMod.BlacklistKeywords.Value = snapshot.blacklist;
			RandomValueMod.EnableWhitelistMode.Value = snapshot.enableWhitelist;
			RandomValueMod.WhitelistKeywords.Value = snapshot.whitelist;
			RandomValueMod.ExcludeHighValueItems.Value = snapshot.excludeHigh;
			RandomValueMod.HighValueThreshold.Value = snapshot.highThreshold;
			RandomValueMod.CashBagRandomizationToggle.Value = snapshot.cashBag;
			RandomValueMod.CurrentLevelSeed = snapshot.levelSeed;
			RandomValueMod.StaticLogger.LogInfo($"[Config] 快照已应用 (rev {snapshot.revision})");
		}

		public int GetLevelSeed()
		{
			if (PhotonNetwork.InRoom)
			{
				int roomTimestamp = PhotonNetwork.ServerTimestamp;
				string levelName = RunManager.instance?.levelCurrent?.name ?? "default";
				return roomTimestamp ^ levelName.GetHashCode();
			}
			return UnityEngine.Random.Range(1, int.MaxValue);
		}

		private ValuableObject FindValuableByViewId(int viewId)
		{
			var pv = PhotonView.Find(viewId);
			return pv?.GetComponent<ValuableObject>();
		}

		private static string GetPlayerNameByActorNumber(int actorNumber)
		{
			if (PhotonNetwork.CurrentRoom != null)
			{
				var player = PhotonNetwork.CurrentRoom.GetPlayer(actorNumber);
				if (player != null && !string.IsNullOrEmpty(player.NickName))
					return player.NickName;
			}
			return $"Player {actorNumber}";
		}

		private static string CleanItemName(string rawName)
		{
			if (string.IsNullOrEmpty(rawName)) return rawName;
			return rawName.Replace("Valuable ", "").Replace("(Clone)", "").Trim();
		}
	}

	[BepInPlugin("dyxc666.RandomValuablePrice", "随机贵重物品价格", "2.0.6")]
	[BepInProcess("REPO.exe")]
	public class RandomValueMod : BaseUnityPlugin
	{
		private readonly Harmony harmony = new Harmony("dyxc666.RandomValuablePrice");

		public static ConfigEntry<bool> EnableValueRandomization;
		public static ConfigEntry<float> MinValueMultiplier;
		public static ConfigEntry<float> MaxValueMultiplier;
		public static ConfigEntry<bool> EnableVerboseLogging;
		public static ConfigEntry<string> BlacklistKeywords;
		public static ConfigEntry<string> WhitelistKeywords;
		public static ConfigEntry<bool> EnableBlacklistMode;
		public static ConfigEntry<bool> EnableWhitelistMode;
		public static ConfigEntry<bool> ExcludeHighValueItems;
		public static ConfigEntry<float> HighValueThreshold;
		public static ConfigEntry<bool> CashBagRandomizationToggle;

		public static HashSet<int> modifiedItemIds = new HashSet<int>();
		public static Dictionary<int, float> priceCache = new Dictionary<int, float>();
		public static RandomizationConfig CurrentConfig { get; private set; }
		public static int CurrentLevelSeed { get; set; }
		public static ManualLogSource StaticLogger { get; private set; }

		private void Awake()
		{
			StaticLogger = Logger;
			StaticLogger.LogInfo("随机贵重物品价格 v2.0.6");

			CreateConfigEntries();
			UpdateConfig();

			MinValueMultiplier.SettingChanged += (sender, e) => { UpdateConfig(); PublishConfigIfMaster(); };
			MaxValueMultiplier.SettingChanged += (sender, e) => { UpdateConfig(); PublishConfigIfMaster(); };
			EnableBlacklistMode.SettingChanged += (sender, e) => { UpdateConfig(); PublishConfigIfMaster(); };
			BlacklistKeywords.SettingChanged += (sender, e) => { UpdateConfig(); PublishConfigIfMaster(); };
			EnableWhitelistMode.SettingChanged += (sender, e) => { UpdateConfig(); PublishConfigIfMaster(); };
			WhitelistKeywords.SettingChanged += (sender, e) => { UpdateConfig(); PublishConfigIfMaster(); };
			ExcludeHighValueItems.SettingChanged += (sender, e) => { UpdateConfig(); PublishConfigIfMaster(); };
			HighValueThreshold.SettingChanged += (sender, e) => { UpdateConfig(); PublishConfigIfMaster(); };
			CashBagRandomizationToggle.SettingChanged += (sender, e) => { UpdateConfig(); PublishConfigIfMaster(); };

			ApplyHarmonyPatches();
		}

		private void PublishConfigIfMaster()
		{
			if (!PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom) return;
			var relay = RandomValueRpcRelay.Instance;
			if (relay == null) return;
			CurrentLevelSeed = relay.GetLevelSeed();
			relay.PublishConfigSnapshot(CurrentConfig, CurrentLevelSeed);
		}

		private void UpdateConfig()
		{
			CurrentConfig = new RandomizationConfig
			{
				MinMultiplier = MinValueMultiplier.Value,
				MaxMultiplier = MaxValueMultiplier.Value,
				EnableBlacklist = EnableBlacklistMode.Value,
				BlacklistKeywords = BlacklistKeywords.Value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
									  .Select(k => k.Trim().ToLower()).Where(k => !string.IsNullOrEmpty(k)).ToList(),
				EnableWhitelist = EnableWhitelistMode.Value,
				WhitelistKeywords = WhitelistKeywords.Value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
									  .Select(k => k.Trim().ToLower()).Where(k => !string.IsNullOrEmpty(k)).ToList(),
				ExcludeHighValueItems = ExcludeHighValueItems.Value,
				HighValueThreshold = HighValueThreshold.Value,
				RandomizeCashBags = CashBagRandomizationToggle.Value
			};
		}

		private void CreateConfigEntries()
		{
			EnableValueRandomization = Config.Bind("基础设置", "随机化物品价格", true);
			MinValueMultiplier = Config.Bind("价格设置", "最小价格倍率", 0.5f, new ConfigDescription("最小倍率", new AcceptableValueRange<float>(0.1f, 5f)));
			MaxValueMultiplier = Config.Bind("价格设置", "最大价格倍率", 2f, new ConfigDescription("最大倍率", new AcceptableValueRange<float>(0.5f, 10f)));
			EnableBlacklistMode = Config.Bind("黑白名单", "启用黑名单模式", true);
			BlacklistKeywords = Config.Bind("黑白名单", "黑名单关键词", "");
			EnableWhitelistMode = Config.Bind("黑白名单", "启用白名单模式", false);
			WhitelistKeywords = Config.Bind("黑白名单", "白名单关键词", "");
			ExcludeHighValueItems = Config.Bind("特殊排除规则", "排除高价物品", false);
			HighValueThreshold = Config.Bind("特殊排除规则", "高价阈值", 50000f, new ConfigDescription("阈值", new AcceptableValueRange<float>(1000f, 1000000f)));
			CashBagRandomizationToggle = Config.Bind("特殊排除规则", "现金袋随机化", false);
			EnableVerboseLogging = Config.Bind("调试", "详细日志", true);
		}

		private void ApplyHarmonyPatches()
		{
			harmony.Patch(
				AccessTools.Method(typeof(PhysGrabObject), "GrabStarted"),
				postfix: new HarmonyMethod(typeof(PhysGrabObjectPatch), nameof(PhysGrabObjectPatch.Postfix_GrabStarted))
			);
			harmony.Patch(
				AccessTools.Method(typeof(PhysGrabObject), "GrabEnded"),
				postfix: new HarmonyMethod(typeof(PhysGrabObjectPatch), nameof(PhysGrabObjectPatch.Postfix_GrabEnded))
			);
		}

		public static void ClearCache()
		{
			modifiedItemIds.Clear();
			priceCache.Clear();
			if (EnableVerboseLogging.Value)
				StaticLogger.LogInfo("[Cache] 已清空缓存");
		}
	}

	[HarmonyPatch(typeof(PhysGrabObject))]
	public static class PhysGrabObjectPatch
	{
		[HarmonyPostfix]
		[HarmonyPatch("GrabStarted")]
		public static void Postfix_GrabStarted(PhysGrabObject __instance, PhysGrabber player)
		{
			if (!RandomValueMod.EnableValueRandomization.Value) return;

			var valuable = __instance.GetComponent<ValuableObject>();
			if (valuable == null) return;

			int itemId;
			bool isMultiplayer = PhotonNetwork.IsConnected;
			if (isMultiplayer)
			{
				var pv = valuable.GetComponent<PhotonView>();
				if (pv == null) return;
				itemId = pv.ViewID;
			}
			else
			{
				itemId = valuable.GetInstanceID();
			}

			if (RandomValueMod.modifiedItemIds.Contains(itemId)) return;
			RandomValueMod.modifiedItemIds.Add(itemId);

			if (!isMultiplayer || PhotonNetwork.IsMasterClient)
			{
				float oldPrice = RandomValueService.GetItemPrice(valuable);
				float newPrice = RandomValueService.ApplyRandomPrice(valuable, RandomValueMod.CurrentConfig, RandomValueMod.CurrentLevelSeed);

				if (RandomValueMod.EnableVerboseLogging.Value)
				{
					string itemName = CleanItemName(valuable.name);
					float multiplier = oldPrice > 0 ? newPrice / oldPrice : 0f;
					if (isMultiplayer)
					{
						string playerName = GetPlayerNameByActorNumber(PhotonNetwork.LocalPlayer.ActorNumber);
						RandomValueMod.StaticLogger.LogInfo($"[Host Grab] {playerName} 抓取 {itemName} | {oldPrice:F0} → {newPrice:F0} | 倍率: {multiplier:F2}x");
					}
					else
					{
						string playerName = PhotonNetwork.LocalPlayer.NickName;
						RandomValueMod.StaticLogger.LogInfo($"[Single] {playerName} 抓取 {itemName} | {oldPrice:F0} → {newPrice:F0} | 倍率: {multiplier:F2}x");
					}
				}

				if (Math.Abs(newPrice - oldPrice) > 0.01f)
				{
					if (isMultiplayer)
					{
						RandomValueMod.priceCache[itemId] = newPrice;
						RandomValueRpcRelay.Instance?.photonView.RPC(
							nameof(RandomValueRpcRelay.SetItemPriceRPC),
							RpcTarget.All,
							itemId, newPrice, oldPrice);
					}
				}
			}
			else
			{
				var relay = RandomValueRpcRelay.Instance;
				relay?.photonView.RPC(
					nameof(RandomValueRpcRelay.RequestItemPriceRPC),
					RpcTarget.MasterClient,
					itemId, PhotonNetwork.LocalPlayer.ActorNumber);
			}
		}

		[HarmonyPostfix]
		[HarmonyPatch("GrabEnded")]
		public static void Postfix_GrabEnded(PhysGrabObject __instance, PhysGrabber player) { }

		private static string GetPlayerNameByActorNumber(int actorNumber)
		{
			if (PhotonNetwork.CurrentRoom != null)
			{
				var player = PhotonNetwork.CurrentRoom.GetPlayer(actorNumber);
				if (player != null && !string.IsNullOrEmpty(player.NickName))
					return player.NickName;
			}
			return $"Player {actorNumber}";
		}

		private static string CleanItemName(string rawName)
		{
			if (string.IsNullOrEmpty(rawName)) return rawName;
			return rawName.Replace("Valuable ", "").Replace("(Clone)", "").Trim();
		}
	}
}